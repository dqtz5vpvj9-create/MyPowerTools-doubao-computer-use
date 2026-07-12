using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyPowerTools.Shell.Avalonia.Services;

public interface IDoubaoSecureRuntimeController : IDisposable
{
    Task<DoubaoRuntimeSecurityState> InspectAsync(
        string runtimeRoot,
        CancellationToken cancellationToken = default);

    Task<DoubaoAgentOperationResult> StartAsync(
        string runtimeRoot,
        string secretFilePath,
        CancellationToken cancellationToken = default);

    Task<DoubaoAgentOperationResult> StopAsync(
        string runtimeRoot,
        CancellationToken cancellationToken = default);
}

public sealed record DoubaoTcpListenerState(string Address, int Port, int ProcessId)
{
    public bool IsLoopback => IPAddress.TryParse(Address, out var address) && IPAddress.IsLoopback(address);
}

public sealed record DoubaoOwnedProcessState(
    string Id,
    int ProcessId,
    int Port,
    DateTimeOffset StartedAtUtc,
    bool IsValidated,
    string Detail);

public sealed record DoubaoRuntimeSecurityState(
    bool InspectionAvailable,
    IReadOnlyList<DoubaoTcpListenerState> Listeners,
    bool HasOwnedProcesses,
    string Detail,
    IReadOnlyList<DoubaoOwnedProcessState>? OwnedProcesses = null)
{
    public bool HasUnsafeListeners => !InspectionAvailable;
    public bool IsSafe => InspectionAvailable;
    public bool HasAnyListeners => Listeners.Count > 0;
    public IReadOnlyList<DoubaoOwnedProcessState> VerifiedOwnedProcesses =>
        OwnedProcesses?.Where(process => process.IsValidated).ToArray() ?? [];

    public static DoubaoRuntimeSecurityState Unverified(string detail = "监听地址尚未验证。") =>
        new(false, [], false, detail);
}

public sealed class DoubaoSecureRuntimeController : IDoubaoSecureRuntimeController
{
    public static readonly int[] ServicePorts = [38102, 38080, 38189];

    private const string DataRootEnvironmentVariable = "DOUBAO_COMPUTER_USE_DATA_ROOT";
    private const int ErrorInsufficientBuffer = 122;
    private const int AddressFamilyInet = 2;
    private const int AddressFamilyInet6 = 23;
    private const TcpTableClass OwnerPidListenerTable = TcpTableClass.OwnerPidListener;
    private static readonly JsonSerializerOptions StateJsonOptions = new() { WriteIndented = true };
    private readonly Dictionary<int, Process> _heldProcesses = [];

    public async Task<DoubaoRuntimeSecurityState> InspectAsync(
        string runtimeRoot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return DoubaoRuntimeSecurityState.Unverified(
                "当前平台无法使用 Windows TCP 所有者表验证豆包服务监听地址。");
        }

        try
        {
            var listeners = ReadTargetListeners();
            var state = LoadState(runtimeRoot);
            var hasLegacyControlRecord = state is null && LoadLegacyState(runtimeRoot) is not null;
            var ownedProcesses = new List<DoubaoOwnedProcessState>();
            if (state is not null)
            {
                foreach (var identity in state.Processes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IdentityValidation validation;
                    try
                    {
                        validation = await ValidateIdentityAsync(identity, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                    {
                        validation = new IdentityValidation(IdentityValidationState.Invalid, ex.Message);
                    }
                    ownedProcesses.Add(new DoubaoOwnedProcessState(
                        identity.Id,
                        identity.ProcessId,
                        identity.Port,
                        identity.StartedAtUtc,
                        validation.State == IdentityValidationState.Valid,
                        validation.Detail));
                }
            }
            var detail = ownedProcesses.Any(process => !process.IsValidated)
                    ? "安全启动记录中存在已退出或身份不匹配的进程。"
                : hasLegacyControlRecord
                    ? "发现旧运行时控制记录；停止或重启时会先完成整组进程身份验证。"
                : listeners.Count == 0
                    ? "三个固定端口当前均未监听。"
                    : "已读取三个固定端口的监听进程；0.0.0.0 与 loopback 均为允许配置。";
            return new DoubaoRuntimeSecurityState(
                true,
                listeners,
                ownedProcesses.Any(process => process.IsValidated) || hasLegacyControlRecord,
                detail,
                ownedProcesses);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return DoubaoRuntimeSecurityState.Unverified(
                $"监听地址验证失败：{ex.Message}");
        }
    }

    public async Task<DoubaoAgentOperationResult> StartAsync(
        string runtimeRoot,
        string secretFilePath,
        CancellationToken cancellationToken = default)
    {
        var security = await InspectAsync(runtimeRoot, cancellationToken).ConfigureAwait(false);
        if (!security.IsSafe)
        {
            return new DoubaoAgentOperationResult(false, "无法读取豆包服务端口占用状态，已阻止重复启动。", security.Detail);
        }
        if (security.HasAnyListeners)
        {
            return new DoubaoAgentOperationResult(
                false,
                "固定端口已被现有服务占用。",
                "请确认现有服务身份；MyPowerTools 只管理由自身安全启动的进程。");
        }

        var pythonHomeError = ConfigureBundledPythonHome(runtimeRoot);
        if (pythonHomeError is not null)
        {
            return new DoubaoAgentOperationResult(
                false,
                "豆包便携 Python 运行时配置失败。",
                pythonHomeError);
        }

        var specifications = BuildSpecifications(runtimeRoot, secretFilePath);
        var missing = specifications
            .SelectMany(specification => new[] { specification.ExecutablePath, specification.RequiredSourcePath })
            .Concat(new[]
            {
                Path.Combine(runtimeRoot, "tool_server", "config.toml"),
                Path.Combine(runtimeRoot, "planner", "config.toml")
            })
            .FirstOrDefault(path => !File.Exists(path));
        if (missing is not null)
        {
            return new DoubaoAgentOperationResult(false, "豆包本地运行时文件不完整。", $"missing: {missing}");
        }

        var state = new OwnedRuntimeState(1, DateTimeOffset.UtcNow, []);
        var started = new List<OwnedProcessIdentity>();
        try
        {
            foreach (var specification in specifications)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var process = StartProcess(specification);
                var identity = new OwnedProcessIdentity(
                    specification.Id,
                    process.Id,
                    Path.GetFullPath(specification.ExecutablePath),
                    process.StartTime.ToUniversalTime(),
                    specification.Port,
                    specification.Arguments.ToArray());
                started.Add(identity);
                state = state with { Processes = started.ToArray() };
                SaveState(runtimeRoot, state);
                await WaitForOwnedLoopbackListenerAsync(identity, cancellationToken).ConfigureAwait(false);
            }

            return new DoubaoAgentOperationResult(
                true,
                "豆包 Computer Use 已通过安全 loopback 启动链启动。",
                string.Join(", ", started.Select(process => $"{process.Id}:PID {process.ProcessId}")));
        }
        catch (OperationCanceledException)
        {
            var remaining = await StopStartedProcessesAfterFailureAsync(started).ConfigureAwait(false);
            if (remaining.Length == 0)
            {
                DeleteState(runtimeRoot);
            }
            else
            {
                SaveState(runtimeRoot, state with { Processes = remaining });
            }
            throw;
        }
        catch (Exception ex)
        {
            var remaining = await StopStartedProcessesAfterFailureAsync(started).ConfigureAwait(false);
            if (remaining.Length == 0)
            {
                DeleteState(runtimeRoot);
            }
            else
            {
                SaveState(runtimeRoot, state with { Processes = remaining });
            }
            return new DoubaoAgentOperationResult(false, "安全启动链未能完成。", ex.Message);
        }
    }

    private static string? ConfigureBundledPythonHome(string runtimeRoot)
    {
        var fullRuntimeRoot = Path.GetFullPath(runtimeRoot);
        var runtimesRoot = Directory.GetParent(fullRuntimeRoot)?.FullName;
        if (string.IsNullOrWhiteSpace(runtimesRoot))
        {
            return null;
        }

        var pythonHome = Path.Combine(runtimesRoot, "Python312");
        if (!File.Exists(Path.Combine(pythonHome, "python.exe")))
        {
            return null;
        }

        try
        {
            foreach (var serviceName in new[] { "tool_server", "mcp_server", "planner" })
            {
                var configPath = Path.Combine(fullRuntimeRoot, serviceName, ".venv", "pyvenv.cfg");
                if (!File.Exists(configPath))
                {
                    continue;
                }

                var lines = File.ReadAllLines(configPath).ToList();
                var homeIndex = lines.FindIndex(line =>
                    line.StartsWith("home = ", StringComparison.OrdinalIgnoreCase));
                var expectedLine = $"home = {pythonHome}";
                if (homeIndex >= 0 &&
                    string.Equals(lines[homeIndex], expectedLine, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (homeIndex >= 0)
                {
                    lines[homeIndex] = expectedLine;
                }
                else
                {
                    lines.Add(expectedLine);
                }

                File.WriteAllLines(configPath, lines);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    public async Task<DoubaoAgentOperationResult> StopAsync(
        string runtimeRoot,
        CancellationToken cancellationToken = default)
    {
        var state = LoadState(runtimeRoot);
        if (state is null || state.Processes.Count == 0)
        {
            return await StopVerifiedLegacyRuntimeAsync(runtimeRoot, cancellationToken).ConfigureAwait(false);
        }

        var remaining = new List<OwnedProcessIdentity>();
        var errors = new List<string>();
        foreach (var identity in state.Processes.Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var validation = await ValidateIdentityAsync(identity, cancellationToken).ConfigureAwait(false);
            if (validation.State == IdentityValidationState.Exited)
            {
                ReleaseHeldProcess(identity.ProcessId);
                continue;
            }
            if (validation.State != IdentityValidationState.Valid)
            {
                remaining.Add(identity);
                errors.Add($"{identity.Id}: {validation.Detail}");
                continue;
            }

            try
            {
                using var process = Process.GetProcessById(identity.ProcessId);
                process.Kill(entireProcessTree: true);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                ReleaseHeldProcess(identity.ProcessId);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or OperationCanceledException)
            {
                remaining.Add(identity);
                errors.Add($"{identity.Id}: {ex.Message}");
            }
        }

        if (remaining.Count == 0)
        {
            DeleteState(runtimeRoot);
        }
        else
        {
            SaveState(runtimeRoot, state with { Processes = remaining });
        }

        return errors.Count == 0
            ? new DoubaoAgentOperationResult(true, "豆包 Computer Use 已停止。", "只终止了通过身份验证的 MyPowerTools 进程。")
            : new DoubaoAgentOperationResult(
                false,
                "部分进程未通过身份验证，已保留运行与身份记录。",
                string.Join(Environment.NewLine, errors));
    }

    private static async Task<DoubaoAgentOperationResult> StopVerifiedLegacyRuntimeAsync(
        string runtimeRoot,
        CancellationToken cancellationToken)
    {
        var legacy = LoadLegacyState(runtimeRoot);
        if (legacy is null)
        {
            return new DoubaoAgentOperationResult(
                false,
                "没有可由 MyPowerTools 安全停止的豆包进程。",
                "未找到安全启动记录或可验证的旧运行时身份记录。");
        }

        var validation = await ValidateLegacyRuntimeAsync(runtimeRoot, legacy, cancellationToken)
            .ConfigureAwait(false);
        if (!validation.Success)
        {
            return new DoubaoAgentOperationResult(
                false,
                "旧豆包运行时身份验证失败，所有进程均保持运行。",
                validation.Detail);
        }

        var errors = new List<string>();
        foreach (var processId in validation.WrapperProcessIds.Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var process = Process.GetProcessById(processId);
                process.Kill(entireProcessTree: true);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                // The process exited after the atomic identity check.
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or OperationCanceledException)
            {
                errors.Add($"PID {processId}: {ex.Message}");
            }
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        IReadOnlyList<DoubaoTcpListenerState> remainingListeners;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            remainingListeners = ReadTargetListeners();
            if (remainingListeners.Count == 0)
            {
                break;
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        while (DateTimeOffset.UtcNow < deadline);

        if (remainingListeners.Count > 0)
        {
            errors.Add("固定端口仍被占用: " + string.Join(", ", remainingListeners.Select(
                listener => $"{listener.Address}:{listener.Port}/PID {listener.ProcessId}")));
        }
        if (errors.Count > 0)
        {
            return new DoubaoAgentOperationResult(
                false,
                "旧豆包运行时未完全停止。",
                string.Join(Environment.NewLine, errors));
        }

        DeleteLegacyState(runtimeRoot);
        return new DoubaoAgentOperationResult(
            true,
            "已停止通过完整身份验证的旧豆包运行时。",
            "下一步将以 127.0.0.1 固定监听重新启动三个服务。");
    }

    private static async Task<LegacyValidation> ValidateLegacyRuntimeAsync(
        string runtimeRoot,
        LegacyRuntimeState state,
        CancellationToken cancellationToken)
    {
        runtimeRoot = Path.GetFullPath(runtimeRoot);
        var services = new[]
        {
            new LegacyProcessSpecification(
                "tool", state.ToolPort, state.ToolWrapperPid, state.ToolServerPid,
                Path.Combine(runtimeRoot, "tool_server", ".venv", "Scripts", "python.exe"),
                ["main.py"], ["main.py"]),
            new LegacyProcessSpecification(
                "mcp", state.McpPort, state.McpWrapperPid, state.McpServerPid,
                Path.Combine(runtimeRoot, "mcp_server", ".venv", "Scripts", "mcp-server.exe"),
                ["--transport", "sse"],
                [Path.Combine(runtimeRoot, "mcp_server", ".venv", "Scripts", "mcp-server.exe"), "--transport", "sse"]),
            new LegacyProcessSpecification(
                "planner", state.PlannerPort, state.PlannerWrapperPid, state.PlannerPid,
                Path.Combine(runtimeRoot, "planner", ".venv", "Scripts", "python.exe"),
                ["-m", "uvicorn", "app:app", "--host", "0.0.0.0", "--port", "38189"],
                ["-m", "uvicorn", "app:app", "--host", "0.0.0.0", "--port", "38189"])
        };
        if (!services.Select(service => service.Port).SequenceEqual(ServicePorts) ||
            services.Any(service => service.WrapperProcessId <= 0 || service.ServerProcessId <= 0) ||
            services.SelectMany(service => new[] { service.WrapperProcessId, service.ServerProcessId }).Distinct().Count() != 6)
        {
            return LegacyValidation.Failed("旧 PID 文件中的端口或进程编号不符合固定运行时拓扑。");
        }

        var listeners = ReadTargetListeners();
        foreach (var service in services)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var portListeners = listeners.Where(listener => listener.Port == service.Port).ToArray();
            if (portListeners.Length != 1 || portListeners[0].ProcessId != service.ServerProcessId)
            {
                return LegacyValidation.Failed(
                    $"{service.Id}: {service.Port} 的监听进程与旧 PID 记录不一致。");
            }

            var wrapper = await QueryProcessSnapshotAsync(service.WrapperProcessId, cancellationToken)
                .ConfigureAwait(false);
            if (wrapper is null ||
                !string.Equals(Path.GetFullPath(wrapper.ExecutablePath), Path.GetFullPath(service.WrapperExecutablePath), StringComparison.OrdinalIgnoreCase) ||
                !CommandArgumentsEqual(wrapper.CommandLine, service.WrapperArguments))
            {
                return LegacyValidation.Failed($"{service.Id}: 包装进程的可执行文件或完整参数不匹配。");
            }

            var server = await QueryProcessSnapshotAsync(service.ServerProcessId, cancellationToken)
                .ConfigureAwait(false);
            if (server is null || !CommandArgumentsEqual(server.CommandLine, service.ServerArguments))
            {
                return LegacyValidation.Failed($"{service.Id}: 监听进程的完整参数不匹配。");
            }
            if (!await IsDescendantOfAsync(service.ServerProcessId, service.WrapperProcessId, cancellationToken)
                .ConfigureAwait(false))
            {
                return LegacyValidation.Failed($"{service.Id}: 监听进程不属于记录中的包装进程树。");
            }
        }

        return new LegacyValidation(true, services.Select(service => service.WrapperProcessId).ToArray(), "身份验证通过。");
    }

    private static bool CommandArgumentsEqual(string commandLine, IReadOnlyList<string> expectedArguments)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }
        try
        {
            var arguments = ParseCommandLineArguments(commandLine);
            return arguments.Count > 0 && arguments.Skip(1).SequenceEqual(expectedArguments, StringComparer.OrdinalIgnoreCase);
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static async Task<bool> IsDescendantOfAsync(
        int processId,
        int ancestorProcessId,
        CancellationToken cancellationToken)
    {
        var visited = new HashSet<int>();
        var current = processId;
        for (var depth = 0; depth < 8 && current > 0 && visited.Add(current); depth++)
        {
            if (current == ancestorProcessId)
            {
                return true;
            }
            var snapshot = await QueryProcessSnapshotAsync(current, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                return false;
            }
            current = snapshot.ParentProcessId;
        }
        return false;
    }

    public void Dispose()
    {
        foreach (var process in _heldProcesses.Values)
        {
            process.Dispose();
        }
        _heldProcesses.Clear();
    }

    private Process StartProcess(RuntimeProcessSpecification specification)
    {
        Directory.CreateDirectory(ResolveLogsDirectory(specification.RuntimeRoot));
        var startInfo = new ProcessStartInfo
        {
            FileName = specification.ExecutablePath,
            WorkingDirectory = specification.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in specification.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var name in specification.RemovedEnvironmentNames)
        {
            startInfo.Environment.Remove(name);
        }
        foreach (var (name, value) in specification.Environment)
        {
            startInfo.Environment[name] = value;
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"无法启动 {specification.Id}。" );
        _heldProcesses[process.Id] = process;
        return process;
    }

    private static IReadOnlyList<RuntimeProcessSpecification> BuildSpecifications(
        string runtimeRoot,
        string secretFilePath)
    {
        runtimeRoot = Path.GetFullPath(runtimeRoot);
        var secrets = ReadEnvironmentFile(secretFilePath);
        var removedEnvironmentNames = secrets.Keys
            .Concat(new[]
            {
                "ARK_API_KEY", "AUTH_KEY", "AUTH_API_KEY", "TOOL_SERVER_ENDPOINT",
                "PLANNER_ENDPOINT", "MCP_SERVER_ENDPOINT", "CONFIG_FILES", "FASTMCP_HOST",
                "FASTMCP_PORT", "SANDBOX__MCP_SERVER_ENDPOINT", "SANDBOX__TOOL_SERVER_ENDPOINT_FORMAT"
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var toolEnvironment = SelectEnvironment(secrets, "AUTH_KEY");
        var mcpEnvironment = SelectEnvironment(secrets, "AUTH_API_KEY");
        if (!mcpEnvironment.ContainsKey("AUTH_API_KEY") &&
            secrets.TryGetValue("AUTH_KEY", out var authKey) &&
            !string.IsNullOrWhiteSpace(authKey))
        {
            mcpEnvironment["AUTH_API_KEY"] = authKey;
        }
        var plannerEnvironment = SelectEnvironment(secrets, "ARK_API_KEY");

        var toolDirectory = Path.Combine(runtimeRoot, "tool_server");
        var mcpDirectory = Path.Combine(runtimeRoot, "mcp_server");
        var mcpSourceDirectory = Path.Combine(mcpDirectory, "src");
        var plannerDirectory = Path.Combine(runtimeRoot, "planner");
        var plannerAppDirectory = Path.Combine(plannerDirectory, "src", "planner");
        var logsDirectory = ResolveLogsDirectory(runtimeRoot);
        var plannerOverrideConfigPath = ResolvePlannerOverrideConfigPath();
        var plannerConfigPath = File.Exists(plannerOverrideConfigPath)
            ? plannerOverrideConfigPath
            : Path.Combine(plannerDirectory, "config.toml");

        return
        [
            new RuntimeProcessSpecification(
                "tool",
                runtimeRoot,
                Path.Combine(toolDirectory, ".venv", "Scripts", "python.exe"),
                Path.Combine(toolDirectory, "main.py"),
                toolDirectory,
                ["-m", "uvicorn", "main:app", "--host", "127.0.0.1", "--port", "38102"],
                WithEnvironment(toolEnvironment,
                    ("CONFIG_FILES", Path.Combine(toolDirectory, "config.toml")),
                    ("LOG__FILENAME", Path.Combine(logsDirectory, "tool_server.log"))),
                removedEnvironmentNames,
                38102),
            new RuntimeProcessSpecification(
                "mcp",
                runtimeRoot,
                Path.Combine(mcpDirectory, ".venv", "Scripts", "python.exe"),
                Path.Combine(mcpDirectory, "src", "mcp_server", "main.py"),
                mcpDirectory,
                ["-m", "mcp_server.main", "--transport", "sse"],
                WithEnvironment(mcpEnvironment,
                    ("FASTMCP_HOST", "127.0.0.1"),
                    ("FASTMCP_PORT", "38080"),
                    ("PYTHONPATH", mcpSourceDirectory),
                    ("TOOL_SERVER_ENDPOINT", "http://127.0.0.1:38102"),
                    ("PLANNER_ENDPOINT", "http://127.0.0.1:38189"),
                    ("LOGGING__FILE", Path.Combine(logsDirectory, "mcp_server.log"))),
                removedEnvironmentNames,
                38080),
            new RuntimeProcessSpecification(
                "planner",
                runtimeRoot,
                Path.Combine(plannerDirectory, ".venv", "Scripts", "python.exe"),
                Path.Combine(plannerAppDirectory, "app.py"),
                plannerAppDirectory,
                ["-m", "uvicorn", "app:app", "--host", "127.0.0.1", "--port", "38189"],
                WithEnvironment(plannerEnvironment,
                    ("CONFIG_FILES", plannerConfigPath),
                    ("SANDBOX__MCP_SERVER_ENDPOINT", "http://127.0.0.1:38080/sse"),
                    ("SANDBOX__TOOL_SERVER_ENDPOINT_FORMAT", "http://127.0.0.1:38102"),
                    ("LOG__FILENAME", Path.Combine(logsDirectory, "planner.log"))),
                removedEnvironmentNames,
                38189)
        ];
    }

    private static Dictionary<string, string> SelectEnvironment(
        IReadOnlyDictionary<string, string> source,
        params string[] names)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (source.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                result[name] = value;
            }
        }
        return result;
    }

    private static Dictionary<string, string> WithEnvironment(
        IReadOnlyDictionary<string, string> source,
        params (string Name, string Value)[] values)
    {
        var result = new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values)
        {
            result[name] = value;
        }
        return result;
    }

    private static Dictionary<string, string> ReadEnvironmentFile(string secretFilePath)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(secretFilePath))
        {
            foreach (var line in File.ReadLines(secretFilePath))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    continue;
                }
                var separator = trimmed.IndexOf('=');
                if (separator < 1)
                {
                    continue;
                }
                var name = trimmed[..separator].Trim();
                var value = trimmed[(separator + 1)..].Trim().Trim('"', '\'');
                if (name.Length > 0)
                {
                    values[name] = value;
                }
            }
        }

        foreach (var name in new[] { "ARK_API_KEY", "AUTH_KEY", "AUTH_API_KEY" })
        {
            if (!values.ContainsKey(name) &&
                Environment.GetEnvironmentVariable(name) is { Length: > 0 } inherited)
            {
                values[name] = inherited;
            }
        }
        return values;
    }

    private static async Task WaitForOwnedLoopbackListenerAsync(
        OwnedProcessIdentity identity,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(18);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var listeners = ReadTargetListeners();
            var matching = listeners.Where(listener => listener.Port == identity.Port).ToArray();
            if (matching.Any(listener => !listener.IsLoopback))
            {
                throw new InvalidOperationException($"{identity.Id} 尝试监听非 loopback 地址。" );
            }
            foreach (var listener in matching.Where(listener => listener.IsLoopback))
            {
                if (listener.ProcessId == identity.ProcessId ||
                    await IsDescendantOfAsync(listener.ProcessId, identity.ProcessId, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return;
                }
            }
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException($"{identity.Id} 未在 127.0.0.1:{identity.Port} 就绪。" );
    }

    private async Task<OwnedProcessIdentity[]> StopStartedProcessesAfterFailureAsync(
        IReadOnlyList<OwnedProcessIdentity> started)
    {
        var remainingProcessIds = new HashSet<int>();
        foreach (var identity in started.Reverse())
        {
            try
            {
                if (_heldProcesses.TryGetValue(identity.ProcessId, out var held) && !held.HasExited)
                {
                    held.Kill(entireProcessTree: true);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    await held.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                if (ProcessIdentityStillExists(identity))
                {
                    remainingProcessIds.Add(identity.ProcessId);
                }
                else
                {
                    ReleaseHeldProcess(identity.ProcessId);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or OperationCanceledException)
            {
                remainingProcessIds.Add(identity.ProcessId);
            }
        }
        return started.Where(identity => remainingProcessIds.Contains(identity.ProcessId)).ToArray();
    }

    private static async Task<IdentityValidation> ValidateIdentityAsync(
        OwnedProcessIdentity identity,
        CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(identity.ProcessId);
            if (process.HasExited)
            {
                process.Dispose();
                return new IdentityValidation(IdentityValidationState.Exited, "进程已退出。");
            }
        }
        catch (ArgumentException)
        {
            return new IdentityValidation(IdentityValidationState.Exited, "进程已退出。");
        }

        using (process)
        {
            string actualPath;
            DateTimeOffset actualStart;
            try
            {
                actualPath = Path.GetFullPath(process.MainModule?.FileName ?? "");
                actualStart = process.StartTime.ToUniversalTime();
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                return new IdentityValidation(IdentityValidationState.Invalid, $"无法验证可执行文件与启动时间：{ex.Message}");
            }
            if (!string.Equals(actualPath, Path.GetFullPath(identity.ExecutablePath), StringComparison.OrdinalIgnoreCase))
            {
                return new IdentityValidation(IdentityValidationState.Invalid, "可执行文件路径与启动记录不一致。");
            }
            if (Math.Abs((actualStart - identity.StartedAtUtc).TotalMilliseconds) > 100)
            {
                return new IdentityValidation(IdentityValidationState.Invalid, "进程启动时间与身份记录不一致。");
            }
        }

        var commandLine = await QueryCommandLineAsync(identity.ProcessId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return new IdentityValidation(IdentityValidationState.Invalid, "无法读取进程命令行身份。");
        }

        IReadOnlyList<string> actualArguments;
        try
        {
            actualArguments = ParseCommandLineArguments(commandLine);
        }
        catch (Win32Exception ex)
        {
            return new IdentityValidation(IdentityValidationState.Invalid, $"无法解析进程完整命令行：{ex.Message}");
        }
        if (actualArguments.Count == 0 ||
            !actualArguments.Skip(1).SequenceEqual(identity.CommandArguments, StringComparer.Ordinal))
        {
            return new IdentityValidation(IdentityValidationState.Invalid, "进程完整参数序列与安全启动记录不一致。");
        }
        return new IdentityValidation(IdentityValidationState.Valid, "身份验证通过。");
    }

    private static IReadOnlyList<string> ParseCommandLineArguments(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var argumentCount);
        if (argv == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            var arguments = new string[argumentCount];
            for (var index = 0; index < argumentCount; index++)
            {
                var pointer = Marshal.ReadIntPtr(argv, index * IntPtr.Size);
                arguments[index] = Marshal.PtrToStringUni(pointer) ?? "";
            }
            return arguments;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    private static async Task<string> QueryCommandLineAsync(int processId, CancellationToken cancellationToken)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var powerShell = Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powerShell))
        {
            return "";
        }
        var command = $"$p = Get-CimInstance Win32_Process -Filter \"ProcessId = {processId}\"; if ($null -eq $p) {{ exit 3 }}; [Console]::Out.Write($p.CommandLine)";
        var startInfo = new ProcessStartInfo
        {
            FileName = powerShell,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return "";
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? await output.ConfigureAwait(false) : "";
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw;
        }
    }

    private static async Task<ProcessSnapshot?> QueryProcessSnapshotAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var powerShell = Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powerShell))
        {
            return null;
        }
        var command =
            $"$p = Get-CimInstance Win32_Process -Filter \"ProcessId = {processId}\"; " +
            "if ($null -eq $p) { exit 3 }; " +
            "$o = [ordered]@{ ProcessId = [int]$p.ProcessId; ParentProcessId = [int]$p.ParentProcessId; " +
            "ExecutablePath = [string]$p.ExecutablePath; CommandLine = [string]$p.CommandLine }; " +
            "$o | ConvertTo-Json -Compress";
        var startInfo = new ProcessStartInfo
        {
            FileName = powerShell,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                return null;
            }
            return JsonSerializer.Deserialize<ProcessSnapshot>(await output.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<DoubaoTcpListenerState> ReadTargetListeners()
    {
        var listeners = new List<DoubaoTcpListenerState>();
        ReadIpv4Listeners(listeners);
        ReadIpv6Listeners(listeners);
        return listeners
            .Where(listener => ServicePorts.Contains(listener.Port))
            .OrderBy(listener => listener.Port)
            .ThenBy(listener => listener.Address, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ReadIpv4Listeners(ICollection<DoubaoTcpListenerState> listeners)
    {
        foreach (var row in ReadTable<MibTcpRowOwnerPid>(AddressFamilyInet))
        {
            var address = new IPAddress(row.LocalAddress).ToString();
            listeners.Add(new DoubaoTcpListenerState(address, DecodePort(row.LocalPort), unchecked((int)row.OwningPid)));
        }
    }

    private static void ReadIpv6Listeners(ICollection<DoubaoTcpListenerState> listeners)
    {
        foreach (var row in ReadTable<MibTcp6RowOwnerPid>(AddressFamilyInet6))
        {
            var address = new IPAddress(row.LocalAddress, row.LocalScopeId).ToString();
            listeners.Add(new DoubaoTcpListenerState(address, DecodePort(row.LocalPort), unchecked((int)row.OwningPid)));
        }
    }

    private static IReadOnlyList<TRow> ReadTable<TRow>(int addressFamily) where TRow : struct
    {
        var size = 0;
        var first = GetExtendedTcpTable(IntPtr.Zero, ref size, false, addressFamily, OwnerPidListenerTable, 0);
        if (first != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(unchecked((int)first));
        }
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var result = GetExtendedTcpTable(buffer, ref size, false, addressFamily, OwnerPidListenerTable, 0);
            if (result != 0)
            {
                throw new Win32Exception(unchecked((int)result));
            }
            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<TRow>();
            var cursor = IntPtr.Add(buffer, sizeof(uint));
            var rows = new TRow[count];
            for (var index = 0; index < count; index++)
            {
                rows[index] = Marshal.PtrToStructure<TRow>(IntPtr.Add(cursor, index * rowSize));
            }
            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int DecodePort(uint port)
    {
        return unchecked((ushort)IPAddress.NetworkToHostOrder(unchecked((short)(port & 0xffff))));
    }

    internal static string ResolveWritableDataRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredRoot));
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Path.GetTempPath();
        }

        return Path.Combine(localAppData, "MyPowerTools", "Doubao");
    }

    internal static string ResolveLogsDirectory(string runtimeRoot)
    {
        _ = runtimeRoot;
        return Path.Combine(ResolveWritableDataRoot(), "logs");
    }

    internal static string ResolvePlannerOverrideConfigPath() =>
        Path.Combine(ResolveWritableDataRoot(), "planner.override.toml");

    private static string StatePath(string runtimeRoot) =>
        Path.Combine(ResolveLogsDirectory(runtimeRoot), "mypowertools-secure-runtime.json");

    private static string LegacyStatePath(string runtimeRoot) =>
        Path.Combine(ResolveLogsDirectory(runtimeRoot), "local-computer-use.pids.json");

    private static OwnedRuntimeState? LoadState(string runtimeRoot)
    {
        var path = StatePath(runtimeRoot);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<OwnedRuntimeState>(File.ReadAllText(path), StateJsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void SaveState(string runtimeRoot, OwnedRuntimeState state)
    {
        var path = StatePath(runtimeRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, StateJsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    private static void DeleteState(string runtimeRoot)
    {
        var path = StatePath(runtimeRoot);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static LegacyRuntimeState? LoadLegacyState(string runtimeRoot)
    {
        var path = LegacyStatePath(runtimeRoot);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<LegacyRuntimeState>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void DeleteLegacyState(string runtimeRoot)
    {
        var path = LegacyStatePath(runtimeRoot);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private void ReleaseHeldProcess(int processId)
    {
        if (_heldProcesses.Remove(processId, out var process))
        {
            process.Dispose();
        }
    }

    private static bool ProcessIdentityStillExists(OwnedProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed record RuntimeProcessSpecification(
        string Id,
        string RuntimeRoot,
        string ExecutablePath,
        string RequiredSourcePath,
        string WorkingDirectory,
        IReadOnlyList<string> Arguments,
        IReadOnlyDictionary<string, string> Environment,
        IReadOnlyList<string> RemovedEnvironmentNames,
        int Port);

    private sealed record OwnedRuntimeState(
        int Version,
        DateTimeOffset CreatedAtUtc,
        IReadOnlyList<OwnedProcessIdentity> Processes);

    private sealed record OwnedProcessIdentity(
        string Id,
        int ProcessId,
        string ExecutablePath,
        DateTimeOffset StartedAtUtc,
        int Port,
        IReadOnlyList<string> CommandArguments);

    private sealed record LegacyProcessSpecification(
        string Id,
        int Port,
        int WrapperProcessId,
        int ServerProcessId,
        string WrapperExecutablePath,
        IReadOnlyList<string> WrapperArguments,
        IReadOnlyList<string> ServerArguments);

    private sealed record LegacyValidation(
        bool Success,
        IReadOnlyList<int> WrapperProcessIds,
        string Detail)
    {
        public static LegacyValidation Failed(string detail) => new(false, [], detail);
    }

    private sealed record ProcessSnapshot(
        int ProcessId,
        int ParentProcessId,
        string ExecutablePath,
        string CommandLine);

    private sealed record LegacyRuntimeState(
        [property: JsonPropertyName("tool_port")] int ToolPort,
        [property: JsonPropertyName("mcp_port")] int McpPort,
        [property: JsonPropertyName("planner_port")] int PlannerPort,
        [property: JsonPropertyName("tool_wrapper_pid")] int ToolWrapperPid,
        [property: JsonPropertyName("tool_server_pid")] int ToolServerPid,
        [property: JsonPropertyName("mcp_wrapper_pid")] int McpWrapperPid,
        [property: JsonPropertyName("mcp_server_pid")] int McpServerPid,
        [property: JsonPropertyName("planner_wrapper_pid")] int PlannerWrapperPid,
        [property: JsonPropertyName("planner_pid")] int PlannerPid);

    private sealed record IdentityValidation(IdentityValidationState State, string Detail);

    private enum IdentityValidationState
    {
        Valid,
        Exited,
        Invalid
    }

    private enum TcpTableClass
    {
        BasicListener,
        BasicConnections,
        BasicAll,
        OwnerPidListener,
        OwnerPidConnections,
        OwnerPidAll,
        OwnerModuleListener,
        OwnerModuleConnections,
        OwnerModuleAll
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddress;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddress;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        TcpTableClass tableClass,
        uint reserved);

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(
        string commandLine,
        out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
