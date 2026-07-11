using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using MyPowerTools.Protocol;
using MyPowerTools.Abstractions;

namespace DoubaoAgent.MyPowerTools;

public sealed class DoubaoAgentModule : IMptModule
{
    private static readonly DoubaoServiceEndpoint[] DefaultEndpoints =
    [
        new("planner", "Agent Planner", "http://127.0.0.1:38189", "/health", false, true),
        new("tool", "Tool Server", "http://127.0.0.1:38102", "/config", false, false),
        new("mcp", "MCP Server", "http://127.0.0.1:38080", "/sse", true, false)
    ];

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private ModuleContext? _context;
    private DoubaoSettings _settings = DoubaoSettings.Default();

    public DoubaoAgentModule()
        : this(CreateLoopbackHttpClient(), ownsHttpClient: true)
    {
    }

    internal DoubaoAgentModule(HttpClient httpClient)
        : this(httpClient, ownsHttpClient: false)
    {
    }

    private DoubaoAgentModule(HttpClient httpClient, bool ownsHttpClient)
    {
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
    }

    public string Id => "doubao-agent";
    public string PackageId => "doubao-agent";
    public Version Version => new(0, 3, 0);

    private ModuleContext Context => _context ?? throw new InvalidOperationException("Doubao Agent was not initialized.");

    public ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        Directory.CreateDirectory(context.DataDirectory);
        Directory.CreateDirectory(context.CacheDirectory);
        Directory.CreateDirectory(context.LogDirectory);
        return ValueTask.FromResult(new InitializeResult(true, context.ProtocolVersion, ["lifecycle", "status", "commands", "settings", "logs", "dashboardCard", "detailPage"]));
    }

    public async ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        var checks = await CheckServicesAsync(ResolveEndpoints(new JsonObject()), cancellationToken);
        var running = checks.Count(check => check.Ok);
        var state = running == checks.Count ? "running" : running == 0 ? "degraded" : "degraded";
        var summary = running == checks.Count
            ? "Planner, Tool Server, and MCP Server are reachable."
            : $"{running}/{checks.Count} Doubao runtime service(s) are reachable.";
        return new ModuleStatusSnapshot(Id, state, summary, DateTimeOffset.UtcNow, checks, 0);
    }

    public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken)
    {
        var endpointParameters = EndpointParameters();
        IReadOnlyList<MptCommandDescriptor> commands =
        [
            Command("doubao-agent.status.summary", "Summarize Doubao runtime status", "Checks Planner 38189, Tool Server 38102, and MCP Server 38080", endpointParameters),
            Command("doubao-agent.health.check", "Check all Doubao runtime services", "Queries the three local Computer Use services", endpointParameters),
            Command("doubao-agent.planner.health", "Check Doubao planner", "Queries Agent Planner on port 38189", endpointParameters),
            Command("doubao-agent.tool.health", "Check Doubao tool server", "Queries Tool Server on port 38102", endpointParameters),
            Command("doubao-agent.mcp.health", "Check Doubao MCP server", "Checks the MCP SSE endpoint on port 38080", endpointParameters),
            Command("doubao-agent.self-test", "Run Doubao controller self-test", "Verifies data, cache, logs, settings, and redaction boundaries"),
            Command("doubao-agent.logs.summary", "Summarize Doubao module logs", "Reports Runner-managed log directory state")
        ];
        return ValueTask.FromResult(commands);
    }

    public async ValueTask<CommandExecutionResult> ExecuteCommandAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        return request.CommandId switch
        {
            "doubao-agent.status.summary" or "doubao-agent.health.check" => await StatusSummaryAsync(request, cancellationToken),
            "doubao-agent.planner.health" => await SingleServiceAsync(request, "planner", cancellationToken),
            "doubao-agent.tool.health" => await SingleServiceAsync(request, "tool", cancellationToken),
            "doubao-agent.mcp.health" => await SingleServiceAsync(request, "mcp", cancellationToken),
            "doubao-agent.self-test" => SelfTest(request),
            "doubao-agent.logs.summary" => LogsSummary(request),
            _ => Failed(request, MptErrorCodes.NotFound, $"Command '{request.CommandId}' is not implemented by Doubao Agent.")
        };
    }

    public async IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(EventCursor cursor, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (cursor.LastEventSeq >= 1)
        {
            yield break;
        }

        var checks = await CheckServicesAsync(ResolveEndpoints(new JsonObject()), cancellationToken);
        var running = checks.Count(check => check.Ok);
        yield return new MptModuleEvent(
            Id,
            1,
            running == checks.Count ? "planner.up" : "planner.down",
            DateTimeOffset.UtcNow,
            new JsonObject
            {
                ["title"] = "Doubao Computer Use services",
                ["message"] = $"{running}/{checks.Count} Doubao runtime service(s) are reachable.",
                ["reachableCount"] = running,
                ["serviceCount"] = checks.Count,
                ["services"] = new JsonArray(checks.Select(ToServiceJson).Select(node => node.DeepClone()).ToArray())
            });
    }

    public ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSchemaDocument(Id, """
        {
          "type": "object",
          "properties": {
            "plannerBaseUrl": { "type": "string", "const": "http://127.0.0.1:38189", "default": "http://127.0.0.1:38189", "readOnly": true },
            "toolBaseUrl": { "type": "string", "const": "http://127.0.0.1:38102", "default": "http://127.0.0.1:38102", "readOnly": true },
            "mcpBaseUrl": { "type": "string", "const": "http://127.0.0.1:38080", "default": "http://127.0.0.1:38080", "readOnly": true },
            "plannerHealthPath": { "type": "string", "const": "/health", "default": "/health", "readOnly": true },
            "toolHealthPath": { "type": "string", "const": "/config", "default": "/config", "readOnly": true },
            "mcpHealthPath": { "type": "string", "const": "/sse", "default": "/sse", "readOnly": true },
            "redactSensitiveOutput": { "type": "boolean", "default": true }
          }
        }
        """));
    }

    public ValueTask<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(_settings.ToSnapshot(Id));
    }

    public ValueTask<SettingsValidationResult> ValidateSettingsAsync(SettingsPatch patch, CancellationToken cancellationToken)
    {
        var messages = new List<string>();
        var canonical = DoubaoSettings.Default().ToJson();
        foreach (var key in new[]
                 {
                     "plannerBaseUrl", "toolBaseUrl", "mcpBaseUrl",
                     "plannerHealthPath", "toolHealthPath", "mcpHealthPath"
                 })
        {
            if (patch.Patch.TryGetPropertyValue(key, out var node) && node is not null)
            {
                var expected = canonical[key]!.GetValue<string>();
                if (node is not JsonValue value ||
                    !value.TryGetValue<string>(out var requested) ||
                    !string.Equals(requested, expected, StringComparison.Ordinal))
                {
                    messages.Add($"{key} is fixed to '{expected}'.");
                }
            }
        }

        return ValueTask.FromResult(new SettingsValidationResult(
            messages.Count == 0,
            messages,
            messages.Count == 0 ? null : new MptRuntimeError(MptErrorCodes.ValidationFailed, string.Join("; ", messages))));
    }

    public ValueTask<SettingsSnapshotDocument> ApplySettingsAsync(SettingsSnapshotDocument snapshot, CancellationToken cancellationToken)
    {
        _settings = DoubaoSettings.FromJson(snapshot.Values);
        return ValueTask.FromResult(_settings.ToSnapshot(Id) with { Revision = snapshot.Revision });
    }

    public ValueTask<IReadOnlyList<UiSurfaceDescriptor>> ListSurfacesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<UiSurfaceDescriptor> surfaces =
        [
            new("doubao-agent.dashboard", "dashboard-card", "Doubao Agent", new JsonObject { ["moduleId"] = Id }),
            new("doubao-agent.services", "tool-page", "Doubao Computer Use", new JsonObject { ["moduleId"] = Id, ["routeId"] = "services" }),
            new("doubao-agent.detail", "detail-page", "Doubao Agent Runtime", new JsonObject { ["moduleId"] = Id }),
            new("doubao-agent.settings", "settings", "Doubao Agent Settings", new JsonObject { ["moduleId"] = Id }),
            new("doubao-agent.logs", "logs", "Doubao Agent Logs", new JsonObject { ["moduleId"] = Id })
        ];
        return ValueTask.FromResult(surfaces);
    }

    public ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private async Task<CommandExecutionResult> StatusSummaryAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var endpoints = ResolveEndpoints(request.Args);
        var checks = await CheckServicesAsync(endpoints, cancellationToken);
        var services = new JsonArray(checks.Select(ToServiceJson).ToArray<JsonNode?>());
        var running = checks.Count(check => check.Ok);
        var payload = new JsonObject
        {
            ["moduleId"] = Id,
            ["state"] = running == checks.Count ? "running" : "degraded",
            ["runningServices"] = running,
            ["totalServices"] = checks.Count,
            ["services"] = services,
            ["ports"] = new JsonObject
            {
                ["planner"] = ExtractPort(endpoints.First(endpoint => endpoint.Id == "planner").BaseUrl),
                ["tool"] = ExtractPort(endpoints.First(endpoint => endpoint.Id == "tool").BaseUrl),
                ["mcp"] = ExtractPort(endpoints.First(endpoint => endpoint.Id == "mcp").BaseUrl)
            }
        };

        return Succeeded(request, payload.ToJsonString());
    }

    private async Task<CommandExecutionResult> SingleServiceAsync(CommandRequest request, string serviceId, CancellationToken cancellationToken)
    {
        var endpoint = ResolveEndpoints(request.Args).First(endpoint => endpoint.Id == serviceId);
        var check = await CheckServiceAsync(endpoint, cancellationToken);
        var payload = ToServiceJson(check);
        return check.Ok
            ? Succeeded(request, payload.ToJsonString())
            : Failed(request, MptErrorCodes.RuntimeUnavailable, check.Message, retryable: true, details: payload);
    }

    private CommandExecutionResult SelfTest(CommandRequest request)
    {
        var endpoints = ResolveEndpoints(request.Args);
        var payload = new JsonObject
        {
            ["moduleId"] = Id,
            ["settingsSchema"] = "available",
            ["dataDirectory"] = RedactPath(Context.DataDirectory),
            ["cacheDirectory"] = RedactPath(Context.CacheDirectory),
            ["logDirectory"] = RedactPath(Context.LogDirectory),
            ["redaction"] = MptLogRedactor.Redact("token=abc123 secret=hidden password=hunter2"),
            ["services"] = new JsonArray(endpoints.Select(endpoint => new JsonObject
            {
                ["id"] = endpoint.Id,
                ["label"] = endpoint.Label,
                ["baseUrl"] = MptLogRedactor.Redact(endpoint.BaseUrl),
                ["healthPath"] = endpoint.HealthPath
            }).ToArray<JsonNode?>())
        };

        File.AppendAllText(Path.Combine(Context.LogDirectory, "doubao-agent.log"), $"{DateTimeOffset.UtcNow:O} self-test completed{Environment.NewLine}");
        return Succeeded(request, payload.ToJsonString());
    }

    private CommandExecutionResult LogsSummary(CommandRequest request)
    {
        var directory = new DirectoryInfo(Context.LogDirectory);
        var files = directory.Exists ? directory.GetFiles("*.log") : [];
        var payload = new JsonObject
        {
            ["moduleId"] = Id,
            ["logDirectory"] = RedactPath(Context.LogDirectory),
            ["fileCount"] = files.Length,
            ["files"] = new JsonArray(files.Select(file => new JsonObject
            {
                ["name"] = file.Name,
                ["length"] = file.Length,
                ["updatedAt"] = file.LastWriteTimeUtc
            }).ToArray<JsonNode?>())
        };
        return Succeeded(request, payload.ToJsonString());
    }

    private async Task<IReadOnlyList<HealthCheckSnapshot>> CheckServicesAsync(IReadOnlyList<DoubaoServiceEndpoint> endpoints, CancellationToken cancellationToken)
    {
        return await Task.WhenAll(endpoints.Select(endpoint => CheckServiceAsync(endpoint, cancellationToken)));
    }

    private async Task<HealthCheckSnapshot> CheckServiceAsync(DoubaoServiceEndpoint endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(1000));
            var uri = new Uri(new Uri(endpoint.BaseUrl.TrimEnd('/') + "/"), endpoint.HealthPath.TrimStart('/'));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd(endpoint.HeadersOnly ? "text/event-stream" : "application/json");
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            var body = response.IsSuccessStatusCode
                ? endpoint.HeadersOnly
                    ? "SSE endpoint ready"
                    : endpoint.IncludeSuccessBody
                        ? MptLogRedactor.Redact(await response.Content.ReadAsStringAsync(timeout.Token))
                        : "endpoint ready"
                : MptLogRedactor.Redact(await response.Content.ReadAsStringAsync(timeout.Token));
            var message = response.IsSuccessStatusCode
                ? $"HTTP {(int)response.StatusCode}: {Trim(body)}"
                : $"HTTP {(int)response.StatusCode}: {Trim(body)}";
            return new HealthCheckSnapshot($"doubao.{endpoint.Id}", endpoint.Label, response.IsSuccessStatusCode, message);
        }
        catch (OperationCanceledException)
        {
            return new HealthCheckSnapshot($"doubao.{endpoint.Id}", endpoint.Label, false, $"Timed out while checking {endpoint.BaseUrl}.");
        }
        catch (Exception ex)
        {
            return new HealthCheckSnapshot($"doubao.{endpoint.Id}", endpoint.Label, false, MptLogRedactor.Redact(ex.Message));
        }
    }

    private DoubaoServiceEndpoint[] ResolveEndpoints(JsonObject args)
    {
        return
        [
            new("planner", "Agent Planner", _settings.PlannerBaseUrl, _settings.PlannerHealthPath, false, true),
            new("tool", "Tool Server", _settings.ToolBaseUrl, _settings.ToolHealthPath, false, false),
            new("mcp", "MCP Server", _settings.McpBaseUrl, _settings.McpHealthPath, true, false)
        ];
    }

    private static JsonObject ToServiceJson(HealthCheckSnapshot check)
    {
        var id = check.Id.StartsWith("doubao.", StringComparison.Ordinal) ? check.Id["doubao.".Length..] : check.Id;
        return new JsonObject
        {
            ["id"] = id,
            ["label"] = check.Label,
            ["ok"] = check.Ok,
            ["message"] = check.Message
        };
    }

    private static MptCommandDescriptor Command(string id, string title, string subtitle, IReadOnlyList<CommandParameterDescriptor>? parameters = null)
    {
        return new MptCommandDescriptor(
            id,
            "doubao-agent",
            title,
            subtitle,
            "action",
            Category: "Doubao Agent",
            TimeoutMs: 8000,
            Execution: new JsonObject { ["type"] = "module.execute" },
            Parameters: parameters);
    }

    private static IReadOnlyList<CommandParameterDescriptor> EndpointParameters()
    {
        return [];
    }

    private static CommandExecutionResult Succeeded(CommandRequest request, string output)
    {
        return new CommandExecutionResult(request.InvocationId, request.CommandId, "succeeded", true, output);
    }

    private static CommandExecutionResult Failed(CommandRequest request, string code, string message, bool retryable = false, JsonObject? details = null)
    {
        return new CommandExecutionResult(request.InvocationId, request.CommandId, "failed", false, "", new MptRuntimeError(code, message, retryable, details));
    }

    private static int ExtractPort(string baseUrl)
    {
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Port : 0;
    }

    private static string RedactPath(string path)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(local)
            ? path
            : path.Replace(local, "%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase);
    }

    private static string Trim(string value)
    {
        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= 300 ? value : value[..300] + "...";
    }

    private static HttpClient CreateLoopbackHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromMilliseconds(1000)
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMilliseconds(1200)
        };
    }

    private sealed record DoubaoServiceEndpoint(
        string Id,
        string Label,
        string BaseUrl,
        string HealthPath,
        bool HeadersOnly,
        bool IncludeSuccessBody);

    private sealed record DoubaoSettings(
        string PlannerBaseUrl,
        string ToolBaseUrl,
        string McpBaseUrl,
        string PlannerHealthPath,
        string ToolHealthPath,
        string McpHealthPath,
        bool RedactSensitiveOutput)
    {
        public static DoubaoSettings Default()
        {
            return new DoubaoSettings(
                DefaultEndpoints[0].BaseUrl,
                DefaultEndpoints[1].BaseUrl,
                DefaultEndpoints[2].BaseUrl,
                DefaultEndpoints[0].HealthPath,
                DefaultEndpoints[1].HealthPath,
                DefaultEndpoints[2].HealthPath,
                true);
        }

        public static DoubaoSettings FromJson(JsonObject values)
        {
            var defaults = Default();
            return new DoubaoSettings(
                defaults.PlannerBaseUrl,
                defaults.ToolBaseUrl,
                defaults.McpBaseUrl,
                defaults.PlannerHealthPath,
                defaults.ToolHealthPath,
                defaults.McpHealthPath,
                SettingsJson.ReadBool(values, "redactSensitiveOutput") ?? defaults.RedactSensitiveOutput);
        }

        public SettingsSnapshotDocument ToSnapshot(string moduleId)
        {
            return new SettingsSnapshotDocument(moduleId, 1, ToJson(), DateTimeOffset.UtcNow);
        }

        public JsonObject ToJson()
        {
            return new JsonObject
            {
                ["plannerBaseUrl"] = PlannerBaseUrl,
                ["toolBaseUrl"] = ToolBaseUrl,
                ["mcpBaseUrl"] = McpBaseUrl,
                ["plannerHealthPath"] = PlannerHealthPath,
                ["toolHealthPath"] = ToolHealthPath,
                ["mcpHealthPath"] = McpHealthPath,
                ["redactSensitiveOutput"] = RedactSensitiveOutput
            };
        }
    }
}

