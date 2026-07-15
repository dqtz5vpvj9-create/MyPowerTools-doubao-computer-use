using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace DoubaoAgent.Surface.Services;

public sealed partial class DoubaoAgentToolService
{
    public const string DefaultPlannerApiBaseUrl = "https://ark.cn-beijing.volces.com/api/v3";
    private static readonly JsonSerializerOptions SessionJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private readonly object _settingsSync = new();
    private readonly string _settingsFilePath;

    public string SettingsFilePath => _settingsFilePath;

    public string PlannerOverrideConfigPath => Path.Combine(
        Path.GetDirectoryName(_settingsFilePath) ?? DoubaoSecureRuntimeController.ResolveWritableDataRoot(),
        "planner.override.toml");

    public DoubaoSecretConfiguration ReadSecretConfiguration()
    {
        return new DoubaoSecretConfiguration(
            HasEnvironmentValue("ARK_API_KEY"),
            HasEnvironmentValue("AUTH_KEY"),
            HasEnvironmentValue("AUTH_API_KEY"));
    }

    public async Task<DoubaoAgentOperationResult> SaveConfigurationAsync(
        DoubaoAgentConfigurationUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryNormalizePlannerApiBaseUrl(update.PlannerApiBaseUrl, out var plannerApiBaseUrl, out var validationError))
        {
            return new DoubaoAgentOperationResult(false, validationError, "planner-api-base-url-invalid");
        }

        var secretValidation = ValidateSecretUpdate(update.Secrets);
        if (secretValidation is not null)
        {
            return new DoubaoAgentOperationResult(false, secretValidation, "secret-input-invalid");
        }

        var authenticationValidation = ValidateEffectiveAuthentication(update.Secrets);
        if (authenticationValidation is not null)
        {
            return new DoubaoAgentOperationResult(false, authenticationValidation, "auth-keys-mismatch");
        }

        try
        {
            await SaveSecretsAtomicAsync(update.Secrets, cancellationToken).ConfigureAwait(false);
            Session.PlannerApiBaseUrl = plannerApiBaseUrl;
            PersistSession();
            return new DoubaoAgentOperationResult(
                true,
                "豆包配置已安全保存。",
                "secrets-updated; preferences-persisted; planner-override-updated");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new DoubaoAgentOperationResult(false, "保存豆包配置失败。", ex.Message);
        }
    }

    public async Task<DoubaoAgentOperationResult> TestConfigurationAsync(
        CancellationToken cancellationToken = default)
    {
        var authenticationValidation = ValidateEffectiveAuthentication(DoubaoSecretUpdate.Empty);
        if (authenticationValidation is not null)
        {
            return new DoubaoAgentOperationResult(false, authenticationValidation, "auth-keys-mismatch");
        }

        var tool = await ProbeJsonAsync(new Uri(_toolServer, "/config"), cancellationToken).ConfigureAwait(false);
        if (!tool.Ok)
        {
            return new DoubaoAgentOperationResult(
                false,
                "Tool Server 连接或鉴权失败。",
                DescribeProbeFailure(tool));
        }

        var planner = await ProbeJsonAsync(new Uri(_planner, "/health"), cancellationToken).ConfigureAwait(false);
        if (!planner.Ok)
        {
            return new DoubaoAgentOperationResult(false, "Planner 连接失败。", DescribeProbeFailure(planner));
        }

        var mcp = await ProbeMcpSseAsync(cancellationToken).ConfigureAwait(false);
        if (!mcp.Ok)
        {
            return new DoubaoAgentOperationResult(false, "MCP Server SSE 连接失败。", DescribeProbeFailure(mcp));
        }

        var arkApiKey = ReadEnvironmentValue(_secretFilePath, "ARK_API_KEY");
        if (string.IsNullOrWhiteSpace(arkApiKey))
        {
            return new DoubaoAgentOperationResult(false, "Planner 已连接，ARK_API_KEY 尚未配置。", "ark-api-key-missing");
        }

        if (!TryNormalizePlannerApiBaseUrl(Session.PlannerApiBaseUrl, out var baseUrl, out var validationError))
        {
            return new DoubaoAgentOperationResult(false, validationError, "planner-api-base-url-invalid");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{baseUrl}/models"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", arkApiKey);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new DoubaoAgentOperationResult(
                    false,
                    "Planner 已连接，模型密钥验证失败。",
                    $"model-api-http-{(int)response.StatusCode}");
            }

            return new DoubaoAgentOperationResult(
                true,
                "Tool Server、Planner、MCP Server 与模型 API 均已连接。",
                "tool-auth-ok; planner-ok; mcp-sse-ok; model-api-ok");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DoubaoAgentOperationResult(false, "Planner 已连接，模型 API 验证超时。", "model-api-timeout");
        }
        catch (HttpRequestException ex)
        {
            return new DoubaoAgentOperationResult(false, "Planner 已连接，模型 API 无法访问。", ex.Message);
        }
    }

    public DoubaoAgentOperationResult PersistSession()
    {
        try
        {
            lock (_settingsSync)
            {
                NormalizeSession(Session);
                var payload = JsonSerializer.Serialize(Session, SessionJsonOptions);
                WriteTextAtomic(_settingsFilePath, payload, restrictToCurrentUser: false);
                WriteTextAtomic(PlannerOverrideConfigPath, BuildPlannerOverrideToml(Session.PlannerApiBaseUrl), restrictToCurrentUser: false);
            }
            return new DoubaoAgentOperationResult(true, "豆包偏好已保存。", "preferences-persisted");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new DoubaoAgentOperationResult(false, "豆包偏好保存失败。", ex.Message);
        }
    }

    public void PersistSessionBestEffort()
    {
        _ = PersistSession();
    }

    private DoubaoAgentSessionState LoadSessionState()
    {
        if (!File.Exists(_settingsFilePath))
        {
            return new DoubaoAgentSessionState();
        }

        try
        {
            var state = JsonSerializer.Deserialize<DoubaoAgentSessionState>(
                File.ReadAllText(_settingsFilePath, Encoding.UTF8),
                SessionJsonOptions) ?? new DoubaoAgentSessionState();
            NormalizeSession(state);
            return state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new DoubaoAgentSessionState();
        }
    }

    private async Task SaveSecretsAtomicAsync(
        DoubaoSecretUpdate update,
        CancellationToken cancellationToken)
    {
        var values = ReadEnvironmentFileValues(_secretFilePath);
        ApplySecretUpdate(values, "ARK_API_KEY", update.ArkApiKey);
        ApplySecretUpdate(values, "AUTH_KEY", update.AuthKey);
        ApplySecretUpdate(values, "AUTH_API_KEY", update.AuthApiKey);

        var payload = string.Join(
            Environment.NewLine,
            values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}={pair.Value}"));
        if (payload.Length > 0)
        {
            payload += Environment.NewLine;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await Task.Run(
            () => WriteTextAtomic(_secretFilePath, payload, restrictToCurrentUser: true),
            cancellationToken).ConfigureAwait(false);
    }

    private bool HasEnvironmentValue(string name) =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)) ||
        !string.IsNullOrWhiteSpace(ReadEnvironmentValue(_secretFilePath, name));

    private static Dictionary<string, string> ReadEnvironmentFileValues(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return values;
        }

        foreach (var line in File.ReadLines(path))
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
        return values;
    }

    private static void ApplySecretUpdate(IDictionary<string, string> values, string name, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values[name] = value.Trim();
        }
    }

    private static string? ValidateSecretUpdate(DoubaoSecretUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        foreach (var (name, value) in new[]
                 {
                     ("ARK_API_KEY", update.ArkApiKey),
                     ("AUTH_KEY", update.AuthKey),
                     ("AUTH_API_KEY", update.AuthApiKey)
                 })
        {
            if (value.Length > 8192)
            {
                return $"{name} 长度超过 8192 个字符。";
            }
            if (value.IndexOfAny(['\r', '\n', '\0']) >= 0)
            {
                return $"{name} 包含无效换行或空字符。";
            }
        }
        return null;
    }

    private string? ValidateEffectiveAuthentication(DoubaoSecretUpdate update)
    {
        var values = ReadEnvironmentFileValues(_secretFilePath);
        ApplySecretUpdate(values, "AUTH_KEY", update.AuthKey);
        ApplySecretUpdate(values, "AUTH_API_KEY", update.AuthApiKey);

        var authKey = ReadEffectiveEnvironmentValue(values, "AUTH_KEY");
        var authApiKey = ReadEffectiveEnvironmentValue(values, "AUTH_API_KEY");
        if (string.IsNullOrWhiteSpace(authKey) && !string.IsNullOrWhiteSpace(authApiKey))
        {
            return "AUTH_API_KEY 需要对应的 AUTH_KEY；也可以仅配置 AUTH_KEY，由本地运行时自动继承。";
        }
        if (!string.IsNullOrWhiteSpace(authKey) &&
            !string.IsNullOrWhiteSpace(authApiKey) &&
            !string.Equals(authKey, authApiKey, StringComparison.Ordinal))
        {
            return "AUTH_KEY 与 AUTH_API_KEY 必须相同；也可以留空 AUTH_API_KEY，由本地运行时自动继承。";
        }
        return null;
    }

    private static string ReadEffectiveEnvironmentValue(
        IReadOnlyDictionary<string, string> values,
        string name)
    {
        if (values.TryGetValue(name, out var configured) && !string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }
        return Environment.GetEnvironmentVariable(name)?.Trim() ?? "";
    }

    private async Task<DoubaoProbe> ProbeMcpSseAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _mcpServer);
            request.Headers.Accept.ParseAdd("text/event-stream");
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new DoubaoProbe(
                    false,
                    (int)response.StatusCode,
                    stopwatch.ElapsedMilliseconds,
                    "",
                    $"HTTP {(int)response.StatusCode}");
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                return new DoubaoProbe(
                    false,
                    (int)response.StatusCode,
                    stopwatch.ElapsedMilliseconds,
                    "",
                    $"unexpected-content-type: {mediaType}");
            }

            return new DoubaoProbe(
                true,
                (int)response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                "",
                "");
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

    private static string DescribeProbeFailure(DoubaoProbe probe)
    {
        if (!string.IsNullOrWhiteSpace(probe.Error))
        {
            return probe.Error;
        }
        return probe.StatusCode is { } statusCode ? $"HTTP {statusCode}" : "未连接";
    }

    private static bool TryNormalizePlannerApiBaseUrl(
        string value,
        out string normalized,
        out string error)
    {
        normalized = "";
        error = "模型 API Base URL 无效。";
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrWhiteSpace(uri.UserInfo) ||
            !string.IsNullOrWhiteSpace(uri.Query) ||
            !string.IsNullOrWhiteSpace(uri.Fragment))
        {
            return false;
        }
        normalized = uri.ToString().TrimEnd('/');
        error = "";
        return true;
    }

    private static void NormalizeSession(DoubaoAgentSessionState state)
    {
        state.SelectedModelName = string.IsNullOrWhiteSpace(state.SelectedModelName)
            ? PreferredDefaultModelName
            : state.SelectedModelName.Trim();
        state.Instruction = string.IsNullOrWhiteSpace(state.Instruction)
            ? DefaultTaskInstruction
            : state.Instruction[..Math.Min(state.Instruction.Length, 10_000)];
        state.SystemPrompt ??= "";
        state.SystemPrompt = state.SystemPrompt[..Math.Min(state.SystemPrompt.Length, 20_000)];
        state.OverlayX = Math.Max(0, state.OverlayX);
        state.OverlayY = Math.Max(0, state.OverlayY);
        state.OverlayDurationMs = Math.Clamp(state.OverlayDurationMs, 200, 6000);
        state.OverlayRadius = Math.Clamp(state.OverlayRadius, 8, 120);
        if (!TryNormalizePlannerApiBaseUrl(state.PlannerApiBaseUrl, out var baseUrl, out _))
        {
            baseUrl = DefaultPlannerApiBaseUrl;
        }
        state.PlannerApiBaseUrl = baseUrl;
    }

    private static string ResolveSettingsFilePath() =>
        Path.Combine(DoubaoSecureRuntimeController.ResolveWritableDataRoot(), "settings.json");

    private static string BuildPlannerOverrideToml(string baseUrl)
    {
        var escapedBaseUrl = EscapeToml(baseUrl);
        var builder = new StringBuilder("# Generated by MyPowerTools. Secrets remain in the per-user env file.\n");
        foreach (var model in FallbackModels)
        {
            builder.Append("\n[models.'")
                .Append(model.Name.Replace('-', '_'))
                .Append("']\nname = \"")
                .Append(EscapeToml(model.Name))
                .Append("\"\nbase_url = \"")
                .Append(escapedBaseUrl)
                .Append("\"\napi_key = \"\"\nmax_action = 100\nmax_images = 5\ndisplay_name = \"")
                .Append(EscapeToml(model.DisplayName))
                .Append("\"\n");
        }
        builder.Append("\n[sandbox]\n")
            .Append("mcp_server_endpoint = \"http://127.0.0.1:38080/sse\"\n")
            .Append("manager_endpoint = \"\"\nmanager_req_timeout = 10\n")
            .Append("tool_server_endpoint_format = \"http://{sandbox_primary_ip}:38102\"\n")
            .Append("\n[planner]\nstep_interval = 3\naction_wait_interval = 10\nauth_api_key = \"\"\n")
            .Append("\n[log]\nenv = \"dev\"\nfilename = \"planner.log\"\n")
            .Append("backup_count = 5\nmax_bytes = 104857600\n");
        return builder.ToString();
    }

    private static string EscapeToml(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", "", StringComparison.Ordinal);

    private static void WriteTextAtomic(string path, string content, bool restrictToCurrentUser)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("配置文件目录无效。");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (restrictToCurrentUser)
            {
                RestrictFileAccess(temporaryPath);
            }
            File.Move(temporaryPath, path, overwrite: true);
            if (restrictToCurrentUser)
            {
                RestrictFileAccess(path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void RestrictFileAccess(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            RestrictWindowsFileAccess(path);
            return;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictWindowsFileAccess(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException("无法识别当前 Windows 用户。");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(new FileInfo(path), security);
    }
}

public sealed record DoubaoSecretUpdate(string ArkApiKey, string AuthKey, string AuthApiKey)
{
    public static DoubaoSecretUpdate Empty { get; } = new("", "", "");
}

public sealed record DoubaoAgentConfigurationUpdate(
    DoubaoSecretUpdate Secrets,
    string PlannerApiBaseUrl);

public sealed record DoubaoSecretConfiguration(
    bool ArkApiKeyConfigured,
    bool AuthKeyConfigured,
    bool AuthApiKeyConfigured);
