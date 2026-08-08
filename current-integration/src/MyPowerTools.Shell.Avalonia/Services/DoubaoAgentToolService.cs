using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed partial class DoubaoAgentToolService : IDisposable
{
    public const string ModuleId = "doubao-agent";
    public const string PreferredDefaultModelName = "doubao-seed-2-0-lite-260428";
    public const int MaxScreenshotEncodedCharacters = 28 * 1024 * 1024;
    public const int MaxTraceRawCharacters = 128 * 1024;
    public const int MaxTraceDetailCharacters = 32 * 1024;
    public static readonly Uri CanonicalToolServerUri = new("http://127.0.0.1:38102/");
    public static readonly Uri CanonicalMcpServerUri = new("http://127.0.0.1:38080/sse");
    public static readonly Uri CanonicalPlannerUri = new("http://127.0.0.1:38189/");
    public const string DefaultTaskInstruction =
        "打开 Edge 浏览器访问 https://example.com，等待页面加载完成后 finished(content='edge_example_loaded')。";
    public const string DefaultSystemPrompt =
        "你是本机 Windows VLM computer use agent。每一步必须先观察截图，基于截图完成 grounding，并返回一个可执行动作。动作坐标必须来自本轮截图和执行时获取到的坐标转换。遇到账户、安全、支付、验证码、删除、安装器确认时调用用户。";

    private static readonly DoubaoAgentModel[] FallbackModels =
    [
        new("doubao-1.5-ui-tars-250328", "Doubao-1.5-UI-TARS"),
        new("doubao-seed-2-0-lite-260428", "Doubao-Seed-2.0-Lite"),
        new("doubao-seed-2-1-turbo-260628", "Doubao-Seed-2.1-Turbo"),
        new("doubao-seed-2-1-pro-260628", "Doubao-Seed-2.1-Pro")
    ];
    private static readonly Regex EmbeddedImagePayloadPattern = new(
        "\"(?:screenshot|image|image_base64|screenshot_base64|base64_screenshot)\"\\s*:\\s*\"(?:\\\\.|[^\"])*\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Uri _toolServer;
    private readonly Uri _planner;
    private readonly Uri _mcpServer;
    private readonly string _secretFilePath;
    private readonly Func<string, int, CancellationToken, Task<bool>>? _tcpProbeOverride;
    private readonly TimeSpan _shutdownReadyTimeout;
    private readonly IDoubaoSecureRuntimeController _runtimeController;
    private readonly bool _ownsRuntimeController;
    private readonly object _snapshotGate = new();
    private readonly object _refreshGate = new();
    private DoubaoAgentSnapshot _currentSnapshot = null!;
    private Task<DoubaoAgentSnapshot>? _refreshTask;
    private long _snapshotRevision;

    public DoubaoAgentToolService(
        HttpClient? httpClient = null,
        string? runtimeRoot = null,
        string? secretFilePath = null,
        Func<string, int, CancellationToken, Task<bool>>? tcpProbe = null,
        TimeSpan? shutdownReadyTimeout = null,
        IDoubaoSecureRuntimeController? runtimeController = null,
        string? settingsFilePath = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? CreateLoopbackHttpClient();
        RuntimeRoot = Path.GetFullPath(runtimeRoot ?? ResolveRuntimeRoot());
        _secretFilePath = Path.GetFullPath(secretFilePath ?? ResolveSecretFilePath());
        _toolServer = CanonicalToolServerUri;
        _planner = CanonicalPlannerUri;
        _mcpServer = CanonicalMcpServerUri;
        _tcpProbeOverride = tcpProbe;
        _shutdownReadyTimeout = shutdownReadyTimeout ?? TimeSpan.FromSeconds(10);
        _runtimeController = runtimeController ?? new DoubaoSecureRuntimeController();
        _ownsRuntimeController = runtimeController is null;
        _settingsFilePath = Path.GetFullPath(settingsFilePath ?? ResolveSettingsFilePath());
        Session = LoadSessionState();
        _currentSnapshot = CreateInitialSnapshot();
    }

    public string RuntimeRoot { get; }
    public DoubaoAgentSessionState Session { get; }
    public event Action<DoubaoAgentSnapshot>? SnapshotChanged;

    public DoubaoAgentSnapshot CurrentSnapshot
    {
        get
        {
            lock (_snapshotGate)
            {
                return _currentSnapshot;
            }
        }
    }

    public Task<DoubaoAgentSnapshot> LoadAsync(CancellationToken cancellationToken = default) =>
        RefreshAsync(cancellationToken);

    public Task<DoubaoAgentSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        Task<DoubaoAgentSnapshot> refreshTask;
        lock (_refreshGate)
        {
            if (_refreshTask is { IsCompleted: false } running)
            {
                refreshTask = running;
            }
            else
            {
                refreshTask = RefreshCoreAndReleaseAsync(cancellationToken);
                _refreshTask = refreshTask;
            }
        }

        return cancellationToken.CanBeCanceled
            ? refreshTask.WaitAsync(cancellationToken)
            : refreshTask;
    }

    private async Task<DoubaoAgentSnapshot> RefreshCoreAndReleaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_refreshGate)
            {
                _refreshTask = null;
            }
        }
    }

    private async Task<DoubaoAgentSnapshot> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = ReadConfigurationState();
        PublishSnapshot(snapshot => snapshot with
        {
            RuntimeRoot = RuntimeRoot,
            RuntimeInstalled = RuntimeFilesAvailable(),
            SecretConfigured = configuration.ArkApiKeyConfigured,
            Configuration = configuration,
            Logs = ReadLogFiles(),
            CheckedAt = DateTimeOffset.Now,
            IsRefreshing = true
        });

        var securityTask = _runtimeController.InspectAsync(RuntimeRoot, cancellationToken);
        var toolTask = ProbeJsonAsync(new Uri(_toolServer, "/config"), cancellationToken);
        var plannerTask = ProbeJsonAsync(new Uri(_planner, "/health"), cancellationToken);
        var modelsTask = ProbeJsonAsync(new Uri(_planner, "/models"), cancellationToken);
        var mcpTask = ProbeTcpAsync(_mcpServer.Host, _mcpServer.Port, cancellationToken);
        var overlayTask = ProbeJsonAsync(ToolActionUri("OverlayStatus"), cancellationToken);

        var incrementalUpdates = new Task[]
        {
            PublishSecurityAsync(securityTask),
            PublishServiceAsync("tool", "Tool Server", _toolServer.ToString().TrimEnd('/'), toolTask),
            PublishServiceAsync("planner", "Agent Planner", _planner.ToString().TrimEnd('/'), plannerTask),
            PublishServiceAsync("mcp", "MCP Server", _mcpServer.ToString(), mcpTask),
            PublishModelsAsync(modelsTask),
            PublishOverlayAsync(overlayTask)
        };

        try
        {
            await Task.WhenAll(incrementalUpdates).ConfigureAwait(false);
            return PublishSnapshot(snapshot => snapshot with
            {
                CheckedAt = DateTimeOffset.Now,
                IsRefreshing = false
            });
        }
        catch (OperationCanceledException)
        {
            PublishSnapshot(snapshot => snapshot with { IsRefreshing = false });
            throw;
        }
    }

    private async Task PublishSecurityAsync(Task<DoubaoRuntimeSecurityState> securityTask)
    {
        var security = await securityTask.ConfigureAwait(false);
        if (!security.IsSafe)
        {
            Session.AutoStartEnabled = false;
        }
        PublishSnapshot(snapshot => snapshot with
        {
            Security = security,
            Runtime = ReadProcessState(snapshot.Services, security)
        });
    }

    private async Task PublishServiceAsync(
        string id,
        string displayName,
        string endpoint,
        Task<DoubaoProbe> probeTask)
    {
        var status = ToService(id, displayName, endpoint, await probeTask.ConfigureAwait(false));
        PublishSnapshot(snapshot =>
        {
            var services = snapshot.Services
                .Select(service => string.Equals(service.Id, id, StringComparison.Ordinal) ? status : service)
                .ToArray();
            return snapshot with
            {
                Services = services,
                Runtime = ReadProcessState(services, snapshot.RuntimeSecurity)
            };
        });
    }

    private async Task PublishModelsAsync(Task<DoubaoProbe> modelsTask)
    {
        var models = ParseModels((await modelsTask.ConfigureAwait(false)).Body);
        PublishSnapshot(snapshot => snapshot with { Models = MergeModels(models) });
    }

    private async Task PublishOverlayAsync(Task<DoubaoProbe> overlayTask)
    {
        var overlay = ParseOverlay((await overlayTask.ConfigureAwait(false)).Body);
        PublishSnapshot(snapshot => snapshot with { Overlay = overlay });
    }

    private DoubaoAgentSnapshot PublishSnapshot(Func<DoubaoAgentSnapshot, DoubaoAgentSnapshot> update)
    {
        DoubaoAgentSnapshot snapshot;
        lock (_snapshotGate)
        {
            snapshot = update(_currentSnapshot);
            snapshot = snapshot with { Revision = ++_snapshotRevision };
            _currentSnapshot = snapshot;
        }

        var handlers = SnapshotChanged;
        if (handlers is not null)
        {
            foreach (Action<DoubaoAgentSnapshot> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(snapshot);
                }
                catch (ObjectDisposedException)
                {
                    // A view can detach while a background refresh is publishing its last update.
                }
            }
        }
        return snapshot;
    }

    private DoubaoAgentSnapshot CreateInitialSnapshot()
    {
        var services = new[]
        {
            PendingService("planner", "Agent Planner", _planner.ToString().TrimEnd('/')),
            PendingService("tool", "Tool Server", _toolServer.ToString().TrimEnd('/')),
            PendingService("mcp", "MCP Server", _mcpServer.ToString())
        };
        return new DoubaoAgentSnapshot(
            RuntimeRoot,
            false,
            false,
            services,
            FallbackModels,
            new DoubaoAgentOverlayStatus(false, false, ""),
            [],
            new DoubaoAgentRuntimeState(null, false, []),
            new DoubaoAgentConfigurationState(
                false,
                false,
                false,
                false,
                _secretFilePath,
                SettingsFilePath: _settingsFilePath,
                PlannerOverrideConfigPath: PlannerOverrideConfigPath),
            DateTimeOffset.Now,
            DoubaoRuntimeSecurityState.Unverified("正在读取本机服务端口状态。"),
            IsRefreshing: true);
    }

    private static DoubaoAgentServiceStatus PendingService(string id, string displayName, string endpoint) =>
        new(id, displayName, endpoint, false, "正在检查…", null, 0);

    public async Task<DoubaoAgentOperationResult> StartAsync(CancellationToken cancellationToken = default)
    {
        if (!RuntimeFilesAvailable())
        {
            return new DoubaoAgentOperationResult(false, "未找到豆包 Computer Use 本地运行时。", "runtime-missing");
        }

        var current = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (current.AllServicesOnline)
        {
            return new DoubaoAgentOperationResult(true, "豆包 Computer Use 已在运行。", "already-running");
        }

        var started = await _runtimeController
            .StartAsync(RuntimeRoot, _secretFilePath, cancellationToken)
            .ConfigureAwait(false);
        if (!started.Success)
        {
            return started;
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot.AllServicesOnline)
            {
                return new DoubaoAgentOperationResult(true, "豆包 Computer Use 已启动。", started.TechnicalDetails);
            }
            await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
        }

        return new DoubaoAgentOperationResult(false, "服务启动超时，请查看运行时详情与日志。", started.TechnicalDetails);
    }

    public async Task<DoubaoAgentOperationResult> StopAsync(CancellationToken cancellationToken = default)
    {
        var stopped = await _runtimeController.StopAsync(RuntimeRoot, cancellationToken).ConfigureAwait(false);
        if (!stopped.Success)
        {
            return stopped;
        }

        var deadline = DateTimeOffset.UtcNow.Add(_shutdownReadyTimeout);
        DoubaoAgentSnapshot? snapshot = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshot = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!snapshot.AnyServiceOnline)
            {
                return stopped;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        while (DateTimeOffset.UtcNow < deadline);

        var remaining = string.Join(", ", snapshot.Services
            .Where(service => service.IsOnline)
            .Select(service => service.DisplayName));
        return new DoubaoAgentOperationResult(
            false,
            "本地服务未完全停止，请查看进程与端口状态。",
            $"仍在线: {remaining}; {stopped.TechnicalDetails}");
    }

    public async Task<DoubaoAgentOperationResult> RestartAsync(CancellationToken cancellationToken = default)
    {
        var stopped = await StopAsync(cancellationToken).ConfigureAwait(false);
        if (!stopped.Success)
        {
            return stopped;
        }

        return await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RunTaskAsync(
        DoubaoAgentTaskRequest request,
        Func<DoubaoAgentTaskEvent, Task> onEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        var security = await _runtimeController.InspectAsync(RuntimeRoot, cancellationToken).ConfigureAwait(false);
        if (!security.IsSafe)
        {
            throw new InvalidOperationException($"无法读取豆包服务端口状态：{security.Detail}");
        }

        var prompt = request.Instruction.Trim();
        if (prompt.Length is 0 or > 10_000)
        {
            throw new ArgumentException("任务描述需要包含 1 到 10000 个字符。", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.ModelName) || request.ModelName.Length > 200)
        {
            throw new ArgumentException("请选择可用模型。", nameof(request));
        }

        if (request.SystemPrompt.Length > 20_000)
        {
            throw new ArgumentException("系统提示词不能超过 20000 个字符。", nameof(request));
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_planner, "/run/task"))
        {
            Content = JsonContent.Create(new
            {
                user_prompt = prompt,
                model_name = request.ModelName,
                system_prompt = request.SystemPrompt
            })
        };
        message.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await _httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(UserFacingHttpFailure("Planner", response.StatusCode, body));
        }

        if (!string.Equals(
                response.Content.Headers.ContentType?.MediaType,
                "text/event-stream",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Planner 未返回受支持的事件流响应。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var payload = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (payload.Length > 0)
                {
                    await DispatchTaskEventAsync(payload.ToString(), onEvent).ConfigureAwait(false);
                    payload.Clear();
                }
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (payload.Length > 0)
                {
                    payload.AppendLine();
                }
                payload.Append(line[5..].TrimStart());
            }
        }

        if (payload.Length > 0)
        {
            await DispatchTaskEventAsync(payload.ToString(), onEvent).ConfigureAwait(false);
        }
    }

    public Task<DoubaoAgentOperationResult> ShowOverlayTestAsync(CancellationToken cancellationToken = default)
    {
        return CallOverlayAsync("show", 640, 360, 1800, 34, cancellationToken);
    }

    public Task<DoubaoAgentOperationResult> HideOverlayAsync(CancellationToken cancellationToken = default)
    {
        return CallOverlayAsync("hide", 640, 360, 1800, 34, cancellationToken);
    }

    public async Task<DoubaoAgentOperationResult> CallOverlayAsync(
        string command,
        int x,
        int y,
        int durationMs,
        int radius,
        CancellationToken cancellationToken = default)
    {
        var action = command switch
        {
            "show" => "ShowOverlay",
            "hide" => "HideOverlay",
            "self-test" => "OverlaySelfTest",
            "status" => "OverlayStatus",
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, "不支持的 Overlay 操作。")
        };
        if (action != "OverlayStatus")
        {
            var security = await _runtimeController.InspectAsync(RuntimeRoot, cancellationToken).ConfigureAwait(false);
            if (!security.IsSafe)
            {
                return new DoubaoAgentOperationResult(
                    false,
                    "无法读取豆包服务端口状态，已阻止桌面操作。",
                    security.Detail);
            }
        }
        var parameters = action == "ShowOverlay"
            ? new[]
            {
                ("PositionX", Math.Max(0, x).ToString()),
                ("PositionY", Math.Max(0, y).ToString()),
                ("Label", "Doubao"),
                ("DurationMs", Math.Clamp(durationMs, 200, 6000).ToString()),
                ("Radius", Math.Clamp(radius, 8, 120).ToString())
            }
            : [];
        var successMessage = command switch
        {
            "show" => "已显示桌面定位标记。",
            "hide" => "桌面定位标记已隐藏。",
            "self-test" => "桌面定位标记自检已完成。",
            _ => "桌面定位标记状态已刷新。"
        };
        return await CallToolActionAsync(
            action,
            ToolActionUri(action, parameters),
            successMessage,
            cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        PersistSessionBestEffort();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
        if (_ownsRuntimeController)
        {
            _runtimeController.Dispose();
        }
    }

    private async Task<DoubaoAgentOperationResult> CallToolActionAsync(
        string action,
        Uri uri,
        string successMessage,
        CancellationToken cancellationToken)
    {
        var probe = await ProbeJsonAsync(uri, cancellationToken).ConfigureAwait(false);
        var semanticDetail = "Tool Server 返回了无法验证的操作结果。";
        if (probe.Ok && ToolActionSucceeded(action, probe.Body, out semanticDetail))
        {
            return new DoubaoAgentOperationResult(true, successMessage, probe.Body);
        }

        var technicalDetails = probe.StatusCode is { } status
            ? probe.Ok
                ? semanticDetail
                : UserFacingHttpFailure("Tool Server", (HttpStatusCode)status, probe.Body)
            : probe.Error;
        return new DoubaoAgentOperationResult(
            false,
            "Tool Server 当前无法执行该操作。",
            technicalDetails);
    }

    private async Task<DoubaoProbe> ProbeJsonAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/json");
            var toolAuthKey = ReadToolAuthKey(RuntimeRoot, _secretFilePath);
            if (uri.Port == CanonicalToolServerUri.Port &&
                string.Equals(uri.Host, CanonicalToolServerUri.Host, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(toolAuthKey))
            {
                request.Headers.TryAddWithoutValidation("X-API-Key", toolAuthKey);
            }
            using var response = await _httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return new DoubaoProbe(response.IsSuccessStatusCode, (int)response.StatusCode, stopwatch.ElapsedMilliseconds, body, "");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DoubaoProbe(false, null, stopwatch.ElapsedMilliseconds, "", "连接超时");
        }
        catch (HttpRequestException)
        {
            return new DoubaoProbe(false, null, stopwatch.ElapsedMilliseconds, "", "未连接");
        }
    }

    private async Task<DoubaoProbe> ProbeTcpAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var connected = _tcpProbeOverride is not null
                ? await _tcpProbeOverride(host, port, timeout.Token).ConfigureAwait(false)
                : await ConnectTcpAsync(host, port, timeout.Token).ConfigureAwait(false);
            return connected
                ? new DoubaoProbe(true, null, stopwatch.ElapsedMilliseconds, $"tcp://{host}:{port}", "")
                : new DoubaoProbe(false, null, stopwatch.ElapsedMilliseconds, "", "未连接");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DoubaoProbe(false, null, stopwatch.ElapsedMilliseconds, "", "连接超时");
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException)
        {
            return new DoubaoProbe(false, null, stopwatch.ElapsedMilliseconds, "", "未连接");
        }
    }

    private static async Task<bool> ConnectTcpAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        return client.Connected;
    }

    private Uri ToolActionUri(string action, params (string Name, string Value)[] parameters)
    {
        var query = new StringBuilder($"Action={Uri.EscapeDataString(action)}&Version=2020-04-01");
        foreach (var (name, value) in parameters)
        {
            query.Append('&').Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
        }
        return new Uri(_toolServer, "/?" + query);
    }

    private static async Task DispatchTaskEventAsync(
        string payload,
        Func<DoubaoAgentTaskEvent, Task> onEvent)
    {
        if (payload == "[DONE]")
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var action = ReadText(root, "action", "Action");
            var hasError = HasErrorValue(root);
            var kind = ReadText(root, "event", "status") ??
                       (hasError ? "error" : action is null ? "message" : "action");
            var detail = LimitText(BuildEventDetail(root), MaxTraceDetailCharacters);
            var screenshot = ReadString(root, "screenshot", "Screenshot", "image", "Image", "image_base64", "ImageBase64");
            var isError = hasError ||
                           string.Equals(kind, "error", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(kind, "failed", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(kind, "timeout", StringComparison.OrdinalIgnoreCase);
            await onEvent(new DoubaoAgentTaskEvent(
                DateTimeOffset.Now,
                LimitText(kind, 256),
                LimitText(action ?? EventTitle(kind), 2048),
                detail,
                LimitText(action ?? "", 2048),
                NormalizeScreenshot(screenshot),
                isError,
                SanitizeEventJson(root, screenshot))).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            var safePayload = RedactPotentialImagePayload(payload);
            await onEvent(new DoubaoAgentTaskEvent(
                DateTimeOffset.Now,
                "message",
                "运行消息",
                LimitText(safePayload, MaxTraceDetailCharacters),
                "",
                "",
                false,
                LimitText(safePayload, MaxTraceRawCharacters))).ConfigureAwait(false);
        }
    }

    private bool RuntimeFilesAvailable()
    {
        return File.Exists(Path.Combine(RuntimeRoot, ".venv", "Scripts", "python.exe")) &&
               File.Exists(Path.Combine(RuntimeRoot, "tool_server", "main.py")) &&
               File.Exists(Path.Combine(RuntimeRoot, "tool_server", "config.toml")) &&
               File.Exists(Path.Combine(RuntimeRoot, ".venv", "Scripts", "mcp-server.exe")) &&
               File.Exists(Path.Combine(RuntimeRoot, "mcp_server", "src", "mcp_server", "main.py")) &&
               File.Exists(Path.Combine(RuntimeRoot, ".venv", "Scripts", "python.exe")) &&
               File.Exists(Path.Combine(RuntimeRoot, "planner", "src", "planner", "app.py")) &&
               File.Exists(Path.Combine(RuntimeRoot, "planner", "config.toml"));
    }

    private bool SecretIsConfigured()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ARK_API_KEY")))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(ReadEnvironmentValue(_secretFilePath, "ARK_API_KEY"));
    }

    private IReadOnlyList<DoubaoAgentLogFile> ReadLogFiles()
    {
        var logsDirectory = DoubaoSecureRuntimeController.ResolveLogsDirectory(RuntimeRoot);
        if (!Directory.Exists(logsDirectory))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(logsDirectory, "*.log", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(6)
                .Select(file => new DoubaoAgentLogFile(
                    file.Name,
                    file.Length,
                    file.LastWriteTimeUtc == DateTime.MinValue
                        ? DateTimeOffset.MinValue
                        : new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero).ToLocalTime()))
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private DoubaoAgentRuntimeState ReadProcessState(
        IReadOnlyList<DoubaoAgentServiceStatus> services,
        DoubaoRuntimeSecurityState security)
    {
        var statePath = Path.Combine(
            DoubaoSecureRuntimeController.ResolveLogsDirectory(RuntimeRoot),
            "mypowertools-secure-runtime.json");
        var owned = security.VerifiedOwnedProcesses;
        if (owned.Count == 0)
        {
            return new DoubaoAgentRuntimeState(null, File.Exists(statePath), []);
        }

        var processes = owned.Select(process => new DoubaoAgentRuntimeProcess(
                process.Id,
                ServiceDisplayName(process.Id),
                process.Port,
                process.ProcessId,
                true,
                services.FirstOrDefault(service => service.Id == process.Id)?.IsOnline ?? false))
            .OrderBy(process => process.Id, StringComparer.Ordinal)
            .ToArray();
        return new DoubaoAgentRuntimeState(
            owned.Min(process => process.StartedAtUtc).ToLocalTime(),
            true,
            processes);
    }

    private DoubaoAgentConfigurationState ReadConfigurationState()
    {
        var envPath = _secretFilePath;
        var secrets = ReadSecretConfiguration();
        return new DoubaoAgentConfigurationState(
            File.Exists(Path.Combine(RuntimeRoot, "tool_server", "config.toml")),
            File.Exists(Path.Combine(RuntimeRoot, "planner", "config.toml")),
            File.Exists(envPath),
            Directory.Exists(DoubaoSecureRuntimeController.ResolveLogsDirectory(RuntimeRoot)),
            envPath,
            secrets.ArkApiKeyConfigured,
            secrets.AuthKeyConfigured,
            secrets.AuthApiKeyConfigured,
            _settingsFilePath,
            PlannerOverrideConfigPath);
    }

    private static string ServiceDisplayName(string id)
    {
        return id switch
        {
            "planner" => "Agent Planner",
            "tool" => "Tool Server",
            "mcp" => "MCP Server",
            _ => id
        };
    }

    private static DoubaoAgentServiceStatus ToService(
        string id,
        string displayName,
        string endpoint,
        DoubaoProbe probe)
    {
        var detail = probe.Ok
            ? probe.StatusCode is { } statusCode
                ? $"HTTP {statusCode} · {probe.ElapsedMilliseconds} ms"
                : $"TCP 已连接 · {probe.ElapsedMilliseconds} ms"
            : probe.Error.Length > 0
                ? probe.Error
                : probe.StatusCode is { } status
                    ? $"HTTP {status}"
                    : "未连接";
        return new DoubaoAgentServiceStatus(id, displayName, endpoint, probe.Ok, detail, probe.StatusCode, probe.ElapsedMilliseconds);
    }

    private static IReadOnlyList<DoubaoAgentModel> ParseModels(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return models.EnumerateArray()
                .Select(model => new DoubaoAgentModel(
                    ReadString(model, "name") ?? "",
                    ReadString(model, "display_name") ?? ReadString(model, "name") ?? ""))
                .Where(model => model.Name.Length > 0)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<DoubaoAgentModel> MergeModels(IReadOnlyList<DoubaoAgentModel> runtimeModels)
    {
        var byName = new Dictionary<string, DoubaoAgentModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in FallbackModels.Concat(runtimeModels))
        {
            byName[model.Name] = model;
        }

        return byName.Values
            .OrderBy(
                model => string.IsNullOrWhiteSpace(model.DisplayName) ? model.Name : model.DisplayName,
                StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static DoubaoAgentOverlayStatus ParseOverlay(string body)
    {
        try
        {
            var node = JsonNode.Parse(body) as JsonObject;
            var result = NodeObject(node, "Result") ?? node;
            var exclusionMethod = ReadNodeString(result, "exclude_method");
            if (string.IsNullOrWhiteSpace(exclusionMethod))
            {
                exclusionMethod = ReadNodeString(result, "affinity_mode");
            }
            return new DoubaoAgentOverlayStatus(
                ReadBool(result, "running"),
                ReadBool(result, "visible"),
                exclusionMethod,
                node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? body,
                ReadBool(result, "ready"),
                ReadNullableBool(result, "affinity_ok"));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new DoubaoAgentOverlayStatus(false, false, "", body);
        }
    }

    private static bool ToolActionSucceeded(string action, string body, out string detail)
    {
        detail = "Tool Server 返回了无法验证的操作结果。";
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException ex)
        {
            detail = $"Tool Server 返回了无效 JSON：{ex.Message}";
            return false;
        }

        var result = NodeObject(root, "Result") ?? NodeObject(root, "result") ?? root;
        if (result is null)
        {
            return false;
        }

        var errorNode = Node(result, "error") ?? Node(root, "error");
        if (NodeHasValue(errorNode))
        {
            detail = $"Tool Server 报告操作失败：{NodeDisplayText(errorNode)}";
            return false;
        }

        var ok = NodeBoolean(result, "ok") ?? NodeBoolean(root, "ok");
        if (ok == false)
        {
            detail = "Tool Server 报告 ok=false。";
            return false;
        }

        var running = NodeBoolean(result, "running");
        var ready = NodeBoolean(result, "ready");
        var visible = NodeBoolean(result, "visible");
        var valid = action switch
        {
            "ShowOverlay" => running == true && ready == true && visible == true,
            "OverlaySelfTest" => running == true && ready == true,
            "HideOverlay" => running == true && ready == true && visible == false,
            "OverlayStatus" => running is not null && ready is not null,
            _ => ok == true
        };
        if (!valid)
        {
            detail = $"Tool Server 操作状态无效：running={ValueText(running)}, ready={ValueText(ready)}, visible={ValueText(visible)}, ok={ValueText(ok)}。";
            return false;
        }

        detail = "Tool Server 操作状态已验证。";
        return true;
    }

    private static JsonObject? NodeObject(JsonObject? value, string name) =>
        Node(value, name) as JsonObject;

    private static JsonNode? Node(JsonObject? value, string name) =>
        value?.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static bool? NodeBoolean(JsonObject? value, string name)
    {
        var node = Node(value, name);
        if (node is not JsonValue jsonValue)
        {
            return null;
        }
        if (jsonValue.TryGetValue<bool>(out var boolean))
        {
            return boolean;
        }
        if (jsonValue.TryGetValue<string>(out var text) && bool.TryParse(text, out boolean))
        {
            return boolean;
        }
        return null;
    }

    private static bool NodeHasValue(JsonNode? value)
    {
        if (value is null)
        {
            return false;
        }
        if (value is JsonValue jsonValue &&
            jsonValue.TryGetValue<string>(out var text))
        {
            return !string.IsNullOrWhiteSpace(text);
        }
        return true;
    }

    private static string NodeDisplayText(JsonNode? value)
    {
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
        {
            return text;
        }
        return value?.ToJsonString() ?? "unknown";
    }

    private static string ValueText(bool? value) => value?.ToString().ToLowerInvariant() ?? "missing";

    private static string ResolveRuntimeRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("DOUBAO_COMPUTER_USE_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return configuredRoot;
        }

        var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var packagedCandidates = new[]
        {
            Path.Combine(baseDirectory, "Runtimes", "Doubao"),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "Runtimes", "Doubao"))
        };
        foreach (var packagedRoot in packagedCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(packagedRoot))
            {
                return packagedRoot;
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "computer-use",
            "doubao-computer-use-local");
    }

    private static string ResolveSecretFilePath()
    {
        return Environment.GetEnvironmentVariable("DOUBAO_COMPUTER_USE_ENV") ??
               Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "secrets", "doubao-computer-use.env");
    }

    private static HttpClient CreateLoopbackHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(2)
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static string ReadEnvironmentValue(string path, string name)
    {
        if (File.Exists(path))
        {
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                    {
                        continue;
                    }
                    var separator = trimmed.IndexOf('=');
                    if (separator < 1 ||
                        !string.Equals(trimmed[..separator].Trim(), name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    return trimmed[(separator + 1)..].Trim().Trim('"', '\'');
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return Environment.GetEnvironmentVariable(name)?.Trim() ?? "";
    }

    private static string ReadToolAuthKey(string runtimeRoot, string secretFilePath)
    {
        var secret = ReadEnvironmentValue(secretFilePath, "AUTH_KEY");
        if (!string.IsNullOrWhiteSpace(secret))
        {
            return secret;
        }

        var configPath = Path.Combine(runtimeRoot, "tool_server", "config.toml");
        if (!File.Exists(configPath))
        {
            return "";
        }
        try
        {
            foreach (var line in File.ReadLines(configPath))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    continue;
                }
                var separator = trimmed.IndexOf('=');
                if (separator < 1 ||
                    !string.Equals(trimmed[..separator].Trim(), "auth_key", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                return trimmed[(separator + 1)..].Trim().Trim('"', '\'');
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return "";
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        return null;
    }

    private static string? ReadText(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => value.GetRawText()
            };
        }

        return null;
    }

    private static int ReadInt(JsonElement element, string name, int fallback = 0)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return fallback;
        }
        return value.TryGetInt32(out var result) ? result : fallback;
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        return value.TryGetInt32(out var result) ? result : null;
    }

    private static int? ReadFirstInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (ReadInt(element, name) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string name)
    {
        var value = ReadString(element, name);
        return DateTimeOffset.TryParse(value, out var result) ? result : null;
    }

    private static bool ReadBool(JsonObject? value, string name)
    {
        return NodeBoolean(value, name) ?? false;
    }

    private static bool? ReadNullableBool(JsonObject? value, string name)
    {
        return NodeBoolean(value, name);
    }

    private static string ReadNodeString(JsonObject? value, string name)
    {
        var node = Node(value, name);
        return node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text)
            ? text
            : "";
    }

    private static string NormalizeScreenshot(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }
        var (_, encoded) = SplitScreenshot(value);
        if (encoded.Length == 0 || encoded.Length > MaxScreenshotEncodedCharacters)
        {
            return "";
        }
        try
        {
            _ = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            return "";
        }
        return value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
            ? value
            : "data:image/png;base64," + value;
    }

    private static string SanitizeEventJson(JsonElement root, string? screenshot)
    {
        var node = JsonNode.Parse(root.GetRawText());
        if (node is null)
        {
            return "{}";
        }
        RemoveScreenshotPayloads(node);
        if (node is JsonObject value && !string.IsNullOrWhiteSpace(screenshot))
        {
            value["screenshot_metadata"] = ScreenshotMetadata(screenshot);
        }
        var raw = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (raw.Length <= MaxTraceRawCharacters)
        {
            return raw;
        }
        var truncated = new JsonObject
        {
            ["truncated"] = true,
            ["originalCharacters"] = raw.Length,
            ["preview"] = raw[..Math.Min(MaxTraceRawCharacters / 2, raw.Length)]
        };
        if (node is JsonObject rootObject && rootObject["screenshot_metadata"] is { } metadata)
        {
            truncated["screenshot_metadata"] = metadata.DeepClone();
        }
        return truncated.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void RemoveScreenshotPayloads(JsonNode node)
    {
        if (node is JsonObject value)
        {
            foreach (var (name, child) in value.ToArray())
            {
                if (IsScreenshotPayloadName(name))
                {
                    value.Remove(name);
                    continue;
                }
                if (child is not null)
                {
                    RemoveScreenshotPayloads(child);
                }
            }
            return;
        }

        if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                if (child is not null)
                {
                    RemoveScreenshotPayloads(child);
                }
            }
        }
    }

    private static bool IsScreenshotPayloadName(string name) =>
        name.Equals("screenshot", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("image", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("image_base64", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("screenshot_base64", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("base64_screenshot", StringComparison.OrdinalIgnoreCase);

    private static JsonObject ScreenshotMetadata(string screenshot)
    {
        var (mimeType, encoded) = SplitScreenshot(screenshot);
        var metadata = new JsonObject
        {
            ["mimeType"] = mimeType,
            ["encodedCharacters"] = encoded.Length
        };
        if (encoded.Length > MaxScreenshotEncodedCharacters)
        {
            metadata["error"] = "payload-too-large";
            return metadata;
        }
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            metadata["byteLength"] = bytes.Length;
            metadata["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
        catch (FormatException)
        {
            metadata["error"] = "invalid-base64";
        }
        return metadata;
    }

    private static (string MimeType, string Encoded) SplitScreenshot(string screenshot)
    {
        if (!screenshot.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return ("image/png", screenshot.Trim());
        }
        var comma = screenshot.IndexOf(',');
        if (comma < 0)
        {
            return ("image/unknown", "");
        }
        var descriptor = screenshot[5..comma];
        var semicolon = descriptor.IndexOf(';');
        var mimeType = (semicolon >= 0 ? descriptor[..semicolon] : descriptor).Trim();
        return (mimeType.Length == 0 ? "image/unknown" : mimeType, screenshot[(comma + 1)..].Trim());
    }

    private static string EventTitle(string kind)
    {
        return kind.ToLowerInvariant() switch
        {
            "screenshot" => "已观察屏幕",
            "started" or "start" => "任务已开始",
            "agent_step" => "代理执行步骤",
            "agent_step_retry" => "模型动作重试",
            "tool_result" => "工具执行结果",
            "finished" or "complete" or "completed" => "任务已完成",
            "error" or "failed" => "执行失败",
            _ => "运行消息"
        };
    }

    private static bool HasErrorValue(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error))
        {
            return false;
        }

        return error.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(error.GetString()),
            _ => true
        };
    }

    private static string BuildEventDetail(JsonElement root)
    {
        var values = new List<string>();
        AddEventText(values, root, null, "parsed_summary", "summary");
        AddEventText(values, root, null, "message");
        AddEventText(values, root, "错误", "error");
        AddEventText(values, root, "警告", "warning");
        AddEventText(values, root, "坐标转换错误", "conversion_error");
        AddEventText(values, root, "模型响应", "model_response");
        AddEventText(values, root, "解析动作", "parsed_action");
        AddEventText(values, root, "工具", "tool_name");
        AddEventText(values, root, "工具参数", "tool_arguments");
        AddEventText(values, root, "坐标转换", "coordinate_transform");
        AddEventText(values, root, "屏幕标记", "overlay_result");
        AddEventText(values, root, "工具结果", "tool_result");
        return string.Join(Environment.NewLine, values);
    }

    private static void AddEventText(
        ICollection<string> values,
        JsonElement root,
        string? label,
        params string[] names)
    {
        var value = ReadText(root, names);
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        values.Add(string.IsNullOrWhiteSpace(label) ? value : $"{label}: {value}");
    }

    private static string UserFacingHttpFailure(string service, HttpStatusCode status, string body)
    {
        var reason = body.ReplaceLineEndings(" ").Trim();
        if (reason.Length > 180)
        {
            reason = reason[..180] + "…";
        }
        return reason.Length == 0
            ? $"{service} 返回 HTTP {(int)status}。"
            : $"{service} 返回 HTTP {(int)status}：{reason}";
    }

    private static string LimitText(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters
            ? value
            : value[..maximumCharacters] + $"…（已截断 {value.Length - maximumCharacters} 个字符）";

    private static string RedactPotentialImagePayload(string value)
    {
        try
        {
            return EmbeddedImagePayloadPattern.Replace(value, "\"image_payload\":\"[redacted-invalid-json]\"");
        }
        catch (RegexMatchTimeoutException)
        {
            return "[invalid event payload omitted]";
        }
    }

    private sealed record DoubaoProbe(
        bool Ok,
        int? StatusCode,
        long ElapsedMilliseconds,
        string Body,
        string Error);
}

public sealed record DoubaoAgentSnapshot(
    string RuntimeRoot,
    bool RuntimeInstalled,
    bool SecretConfigured,
    IReadOnlyList<DoubaoAgentServiceStatus> Services,
    IReadOnlyList<DoubaoAgentModel> Models,
    DoubaoAgentOverlayStatus Overlay,
    IReadOnlyList<DoubaoAgentLogFile> Logs,
    DoubaoAgentRuntimeState Runtime,
    DoubaoAgentConfigurationState Configuration,
    DateTimeOffset CheckedAt,
    DoubaoRuntimeSecurityState? Security = null,
    bool IsRefreshing = false,
    long Revision = 0)
{
    public DoubaoRuntimeSecurityState RuntimeSecurity =>
        Security ?? DoubaoRuntimeSecurityState.Unverified();
    public int OnlineServiceCount => Services.Count(service => service.IsOnline);
    public bool AllServicesOnline =>
        RuntimeSecurity.IsSafe && Services.Count > 0 && Services.All(service => service.IsOnline);
    public bool AnyServiceOnline => Services.Any(service => service.IsOnline);
    public bool HasOwnedProcesses => RuntimeSecurity.HasOwnedProcesses;
}

public sealed record DoubaoAgentServiceStatus(
    string Id,
    string DisplayName,
    string Endpoint,
    bool IsOnline,
    string Detail,
    int? StatusCode,
    long ElapsedMilliseconds)
{
    public string StateText => IsOnline ? "已连接" : "未连接";
}

public sealed record DoubaoAgentModel(string Name, string DisplayName);

public sealed record DoubaoAgentOverlayStatus(
    bool IsRunning,
    bool IsVisible,
    string ExclusionMethod,
    string RawJson = "",
    bool IsReady = false,
    bool? CaptureProtectionOk = null);

public sealed record DoubaoAgentLogFile(string Name, long Length, DateTimeOffset UpdatedAt)
{
    public string SizeText => Length < 1024
        ? $"{Length} B"
        : Length < 1024 * 1024
            ? $"{Length / 1024d:0.#} KB"
            : $"{Length / 1024d / 1024d:0.#} MB";
    public string UpdatedAtText => UpdatedAt == DateTimeOffset.MinValue ? "—" : UpdatedAt.ToString("MM-dd HH:mm");
}

public sealed record DoubaoAgentRuntimeState(
    DateTimeOffset? StartedAt,
    bool PidFileExists,
    IReadOnlyList<DoubaoAgentRuntimeProcess> Processes);

public sealed record DoubaoAgentRuntimeProcess(
    string Id,
    string DisplayName,
    int Port,
    int? ProcessId,
    bool ProcessIsRunning,
    bool EndpointIsOnline)
{
    public string ProcessIdText => ProcessId?.ToString() ?? "—";
    public string PortText => Port.ToString();
    public string StateText => EndpointIsOnline
        ? "正在监听"
        : ProcessIsRunning
            ? "进程存在，端口未就绪"
            : "已停止";
}

public sealed record DoubaoAgentConfigurationState(
    bool ToolConfigExists,
    bool PlannerConfigExists,
    bool SecretFileExists,
    bool LogDirectoryExists,
    string SecretFilePath,
    bool ArkApiKeyConfigured = false,
    bool AuthKeyConfigured = false,
    bool AuthApiKeyConfigured = false,
    string SettingsFilePath = "",
    string PlannerOverrideConfigPath = "");

public sealed record DoubaoAgentTaskRequest(string Instruction, string ModelName, string SystemPrompt);

public sealed record DoubaoAgentTaskEvent(
    DateTimeOffset ReceivedAt,
    string Kind,
    string Title,
    string Detail,
    string Action,
    string ScreenshotDataUrl,
    bool IsError,
    string RawJson);

public sealed record DoubaoAgentOperationResult(bool Success, string Message, string TechnicalDetails = "");

public sealed class DoubaoAgentSessionState
{
    public bool AutoStartEnabled { get; set; } = true;
    public string SelectedModelName { get; set; } = DoubaoAgentToolService.PreferredDefaultModelName;
    public string Instruction { get; set; } = DoubaoAgentToolService.DefaultTaskInstruction;
    public string SystemPrompt { get; set; } = DoubaoAgentToolService.DefaultSystemPrompt;
    public bool ShowSystemPrompt { get; set; }
    public int OverlayX { get; set; } = 640;
    public int OverlayY { get; set; } = 360;
    public int OverlayDurationMs { get; set; } = 1800;
    public int OverlayRadius { get; set; } = 34;
    public string PlannerApiBaseUrl { get; set; } = "https://ark.cn-beijing.volces.com/api/v3";
}
