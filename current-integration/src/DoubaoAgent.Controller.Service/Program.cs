using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DoubaoAgent.Surface.Services;
using MyPowerTools.Ipc;

// Doubao Agent Controller Service Unit — supervised by MyPowerTools.ServiceManager.
//
// Role: own the secure runtime controller (Planner / Tool Runtime / MCP Bridge subprocess
// launch, stop, restart, identity validation), the health/readiness probe fan-out and a
// crash-watchdog — with a life independent of the Shell and the Runner. Before this process
// existed, the subprocesses were started/monitored from the Surface, so a Shell window close
// or Runner recycle left the Python services unmanaged (a crashed service stayed down until the
// user reopened the page and hit Restart). This worker keeps the children supervised: it probes
// their /config, /health and TCP endpoints on a timer, restarts a crashed child within the
// configured backoff, and exposes a status snapshot over the control pipe so the Surface can
// render cached state immediately with zero UI-thread network waits.
//
// Transport: a named pipe (default `mypowertools.doubao-agent.controller`) speaks the same
// length-prefixed binary-JSON framing as the ScreenEase / Remote Notifications units. Commands:
// `ping` (readiness), `state`/`get_state` (cached status snapshot), `inspect` (runtime security),
// `start` (launch the secure chain), `stop` (verified kill), `restart` (stop then start).

var pipeName = GetOption(args, "--pipe") ?? "mypowertools.doubao-agent.controller";
var heartbeatFile = GetOption(args, "--heartbeat-file");
var runtimeRoot = GetOption(args, "--runtime-root");
var secretFilePath = GetOption(args, "--secret-file");
var probeIntervalMs = int.TryParse(GetOption(args, "--probe-interval-ms"), out var pi) ? pi : 3000;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

var pid = Environment.ProcessId;
Console.WriteLine($"DoubaoAgent.Controller.Service starting pid={pid} pipe={pipeName}");

var controller = new DoubaoSecureRuntimeController();
var state = new ControllerState();

var pipeCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
_ = Task.Run(() => ServeControlPipe(pipeName, controller, state, runtimeRoot, secretFilePath, pipeCts.Token));

// Probe + watchdog loop: refresh the cached status snapshot periodically so the Surface reads
// a fresh value with no blocking wait. A crashed owned child is restarted within backoff.
try
{
    while (!cts.Token.IsCancellationRequested)
    {
        try
        {
            RefreshStatus(controller, state, runtimeRoot, secretFilePath);
            WatchOwnedProcesses(controller, state, runtimeRoot, secretFilePath);
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
        {
            break;
        }
        catch (Exception ex)
        {
            state.RecordFailure(ex.Message);
            try { Console.Error.WriteLine($"DoubaoAgent.Controller.Service cycle error: {ex.Message}"); } catch { }
        }

        var heartbeat = $"heartbeat pid={pid} ts={DateTimeOffset.UtcNow:O}";
        Console.WriteLine(heartbeat);
        if (!string.IsNullOrEmpty(heartbeatFile))
        {
            try { await File.AppendAllTextAsync(heartbeatFile, heartbeat + Environment.NewLine, cts.Token); }
            catch { /* heartbeat file is best-effort */ }
        }

        try
        {
            await Task.Delay(probeIntervalMs, cts.Token);
        }
        catch (TaskCanceledException)
        {
            break;
        }
    }
}
catch (OperationCanceledException)
{
    // expected on stop
}

Console.WriteLine($"DoubaoAgent.Controller.Service stopping pid={pid}");
controller.Dispose();
return 0;

// ---------------------------------------------------------------------------
// Refresh the cached status snapshot by fanning out the health probes that the Surface's
// RefreshCoreAsync ran inline. Each probe has a short timeout; failures mark a service offline.
// ---------------------------------------------------------------------------
static void RefreshStatus(DoubaoSecureRuntimeController controller, ControllerState state, string? runtimeRoot, string? secretFilePath)
{
    var root = ResolveRuntimeRoot(runtimeRoot);
    var security = controller.InspectAsync(root, CancellationToken.None).GetAwaiter().GetResult();

    var tool = ProbeJson("http://127.0.0.1:38102/config");
    var planner = ProbeJson("http://127.0.0.1:38189/health");
    var mcp = ProbeTcp("127.0.0.1", 38080);

    state.UpdateSnapshot(new ControllerSnapshot(
        Environment.ProcessId,
        security.IsSafe,
        security.Detail,
        security.Listeners.Select(l => new ListenerInfo(l.Address, l.Port, l.ProcessId, l.IsLoopback)).ToArray(),
        security.OwnedProcesses?.Select(p => new OwnedProcessInfo(p.Id, p.ProcessId, p.Port, p.StartedAtUtc, p.IsValidated, p.Detail)).ToArray() ?? [],
        tool.Online,
        planner.Online,
        mcp,
        root,
        DateTimeOffset.UtcNow));
}

// ---------------------------------------------------------------------------
// Watchdog: if an owned child process has exited unexpectedly, attempt a restart of the
// secure chain (the controller's StartAsync re-checks for free ports and re-launches). This
// is the recovery path that did not exist when supervision lived in the Surface.
// ---------------------------------------------------------------------------
static void WatchOwnedProcesses(DoubaoSecureRuntimeController controller, ControllerState state, string? runtimeRoot, string? secretFilePath)
{
    var root = ResolveRuntimeRoot(runtimeRoot);
    var security = controller.InspectAsync(root, CancellationToken.None).GetAwaiter().GetResult();
    if (!security.IsSafe || security.OwnedProcesses is null || security.OwnedProcesses.Count == 0)
    {
        return; // nothing owned, nothing to watch
    }

    var anyExited = security.OwnedProcesses.Any(p => !p.IsValidated);
    if (!anyExited)
    {
        state.ClearRestartBackoff();
        return;
    }

    if (!state.ShouldAttemptRestart())
    {
        return; // respect backoff window
    }

    var secret = ResolveSecretFilePath(secretFilePath);
    if (string.IsNullOrEmpty(secret))
    {
        state.RecordFailure("Cannot restart: secret file path is not configured.");
        return;
    }

    try
    {
        Console.WriteLine($"DoubaoAgent.Controller.Service watchdog: restarting secure chain after owned process exit.");
        var stopResult = controller.StopAsync(root, CancellationToken.None).GetAwaiter().GetResult();
        var startResult = controller.StartAsync(root, secret, CancellationToken.None).GetAwaiter().GetResult();
        state.RecordRestartAttempt(startResult.Success, startResult.Message);
    }
    catch (Exception ex)
    {
        state.RecordRestartAttempt(false, ex.Message);
    }
}

// ---------------------------------------------------------------------------
// Control pipe. Same framed-JSON wire format as the other Service Units.
// ---------------------------------------------------------------------------
static async Task ServeControlPipe(
    string name,
    DoubaoSecureRuntimeController controller,
    ControllerState state,
    string? runtimeRoot,
    string? secretFilePath,
    CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        NamedPipeServerStream? server = null;
        try
        {
            server = MptNamedPipePolicy.CreateServer(name);
            await server.WaitForConnectionAsync(cancellationToken);

            while (server.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                var request = await ReadFramedMessageAsync(server, cancellationToken);
                if (request is null)
                {
                    break;
                }

                var command = ExtractCommand(request);
                object? data = command switch
                {
                    "ping" => new { pong = true },
                    "state" or "get_state" => state.CurrentSnapshot,
                    "inspect" => HandleInspect(controller, runtimeRoot),
                    "start" => HandleStart(controller, state, runtimeRoot, secretFilePath, request),
                    "stop" => HandleStop(controller, state, runtimeRoot, request),
                    "restart" => HandleRestart(controller, state, runtimeRoot, secretFilePath, request),
                    _ => null
                };

                var ok = data is not null;
                var response = new
                {
                    ok,
                    command,
                    data,
                    error = ok ? null : $"Unknown command '{command}'."
                };
                await WriteFramedMessageAsync(server, response, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (Exception ex)
        {
            try { Console.Error.WriteLine($"DoubaoAgent.Controller.Service pipe error: {ex.Message}"); } catch { }
        }
        finally
        {
            server?.Dispose();
        }
    }
}

static object HandleInspect(DoubaoSecureRuntimeController controller, string? runtimeRoot)
{
    var root = ResolveRuntimeRoot(runtimeRoot);
    var security = controller.InspectAsync(root, CancellationToken.None).GetAwaiter().GetResult();
    return new
    {
        inspectionAvailable = security.InspectionAvailable,
        isSafe = security.IsSafe,
        detail = security.Detail,
        listeners = security.Listeners.Select(l => new { address = l.Address, port = l.Port, processId = l.ProcessId, isLoopback = l.IsLoopback }),
        ownedProcesses = security.OwnedProcesses?.Select(p => new { id = p.Id, processId = p.ProcessId, port = p.Port, isValidated = p.IsValidated, detail = p.Detail })
    };
}

static object HandleStart(DoubaoSecureRuntimeController controller, ControllerState state, string? runtimeRoot, string? secretFilePath, JsonDocument request)
{
    var root = ResolveRuntimeRoot(runtimeRoot, request);
    var secret = ResolveSecretFilePath(secretFilePath, request);
    if (string.IsNullOrEmpty(secret))
    {
        return new { success = false, message = "Secret file path is not configured.", technicalDetails = "no-secret-file" };
    }

    var result = controller.StartAsync(root, secret, CancellationToken.None).GetAwaiter().GetResult();
    return new { success = result.Success, message = result.Message, technicalDetails = result.TechnicalDetails };
}

static object HandleStop(DoubaoSecureRuntimeController controller, ControllerState state, string? runtimeRoot, JsonDocument request)
{
    var root = ResolveRuntimeRoot(runtimeRoot, request);
    var result = controller.StopAsync(root, CancellationToken.None).GetAwaiter().GetResult();
    return new { success = result.Success, message = result.Message, technicalDetails = result.TechnicalDetails };
}

static object HandleRestart(DoubaoSecureRuntimeController controller, ControllerState state, string? runtimeRoot, string? secretFilePath, JsonDocument request)
{
    var root = ResolveRuntimeRoot(runtimeRoot, request);
    var stopResult = controller.StopAsync(root, CancellationToken.None).GetAwaiter().GetResult();
    if (!stopResult.Success)
    {
        return new { success = false, message = stopResult.Message, technicalDetails = stopResult.TechnicalDetails };
    }

    var secret = ResolveSecretFilePath(secretFilePath, request);
    if (string.IsNullOrEmpty(secret))
    {
        return new { success = false, message = "Secret file path is not configured.", technicalDetails = "no-secret-file" };
    }

    var startResult = controller.StartAsync(root, secret, CancellationToken.None).GetAwaiter().GetResult();
    return new { success = startResult.Success, message = startResult.Message, technicalDetails = startResult.TechnicalDetails };
}

// ---------------------------------------------------------------------------
// Health probes (mirrors DoubaoAgentToolService.ProbeJsonAsync / ProbeTcpAsync with 2s timeout).
// ---------------------------------------------------------------------------
static ProbeResult ProbeJson(string uri)
{
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        using var response = client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token).GetAwaiter().GetResult();
        return new ProbeResult(response.IsSuccessStatusCode, (int)response.StatusCode);
    }
    catch
    {
        return new ProbeResult(false, 0);
    }
}

static bool ProbeTcp(string host, int port)
{
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var client = new TcpClient();
        client.ConnectAsync(host, port, cts.Token).GetAwaiter().GetResult();
        return client.Connected;
    }
    catch
    {
        return false;
    }
}

// ---------------------------------------------------------------------------
// Runtime/secret root resolution. The controller reads DOUBAO_COMPUTER_USE_ROOT and the
// secret env file path from env or defaults; the pipe can override per-request for tests.
// ---------------------------------------------------------------------------
static string ResolveRuntimeRoot(string? configured, JsonDocument? request = null)
{
    if (request is not null && request.RootElement.TryGetProperty("runtimeRoot", out var rr) && rr.ValueKind == JsonValueKind.String)
    {
        var fromRequest = rr.GetString();
        if (!string.IsNullOrWhiteSpace(fromRequest)) { return fromRequest; }
    }
    if (!string.IsNullOrWhiteSpace(configured)) { return configured; }
    var env = Environment.GetEnvironmentVariable("DOUBAO_COMPUTER_USE_ROOT");
    if (!string.IsNullOrWhiteSpace(env)) { return env; }

    var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
    var packagedCandidates = new[]
    {
        Path.Combine(baseDirectory, "Runtimes", "Doubao"),
        Path.GetFullPath(Path.Combine(baseDirectory, "..", "Runtimes", "Doubao"))
    };
    foreach (var candidate in packagedCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        if (Directory.Exists(candidate)) { return candidate; }
    }

    var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    return Path.Combine(profile, ".codex", "computer-use", "doubao-computer-use-local");
}

static string ResolveSecretFilePath(string? configured, JsonDocument? request = null)
{
    if (request is not null && request.RootElement.TryGetProperty("secretFile", out var sf) && sf.ValueKind == JsonValueKind.String)
    {
        var fromRequest = sf.GetString();
        if (!string.IsNullOrWhiteSpace(fromRequest)) { return fromRequest; }
    }
    if (!string.IsNullOrWhiteSpace(configured)) { return configured; }
    var env = Environment.GetEnvironmentVariable("DOUBAO_COMPUTER_USE_ENV");
    if (!string.IsNullOrWhiteSpace(env)) { return env; }
    var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    return Path.Combine(profile, ".codex", "secrets", "doubao-computer-use.env");
}

// ---------------------------------------------------------------------------
// Framed-JSON helpers (shared wire format across Service Units).
// ---------------------------------------------------------------------------
static async Task<JsonDocument?> ReadFramedMessageAsync(Stream stream, CancellationToken cancellationToken)
{
    var header = new byte[4];
    var read = 0;
    while (read < 4)
    {
        var n = await stream.ReadAsync(header.AsMemory(read, 4 - read), cancellationToken);
        if (n == 0)
        {
            return read == 0 ? null : throw new EndOfStreamException();
        }
        read += n;
    }

    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
    if (length <= 0 || length > 1024 * 1024)
    {
        throw new InvalidDataException($"Invalid message length {length}");
    }

    var payload = new byte[length];
    read = 0;
    while (read < length)
    {
        var n = await stream.ReadAsync(payload.AsMemory(read, length - read), cancellationToken);
        if (n == 0)
        {
            throw new EndOfStreamException();
        }
        read += n;
    }

    return JsonDocument.Parse(payload);
}

static async Task WriteFramedMessageAsync(Stream stream, object message, CancellationToken cancellationToken)
{
    var json = JsonSerializer.SerializeToUtf8Bytes(message, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    });

    var header = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, json.Length);
    await stream.WriteAsync(header, cancellationToken);
    await stream.WriteAsync(json, cancellationToken);
    await stream.FlushAsync(cancellationToken);
}

static string ExtractCommand(JsonDocument doc)
{
    foreach (var key in new[] { "command", "type", "action" })
    {
        if (doc.RootElement.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String)
        {
            return el.GetString()?.Trim().ToLowerInvariant() ?? "state";
        }
    }
    return "state";
}

static string? GetOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }
    return null;
}

// ---------------------------------------------------------------------------
// Records
// ---------------------------------------------------------------------------
sealed record ProbeResult(bool Online, int StatusCode);

sealed record ListenerInfo(string Address, int Port, int ProcessId, bool IsLoopback);

sealed record OwnedProcessInfo(string Id, int ProcessId, int Port, DateTimeOffset StartedAtUtc, bool IsValidated, string Detail);

sealed record ControllerSnapshot(
    int Pid,
    bool SecuritySafe,
    string SecurityDetail,
    IReadOnlyList<ListenerInfo> Listeners,
    IReadOnlyList<OwnedProcessInfo> OwnedProcesses,
    bool ToolOnline,
    bool PlannerOnline,
    bool McpOnline,
    string RuntimeRoot,
    DateTimeOffset CheckedAt)
{
    public bool AllServicesOnline => SecuritySafe && ToolOnline && PlannerOnline && McpOnline;
}

// ---------------------------------------------------------------------------
// Mutable controller status + restart-backoff state.
// ---------------------------------------------------------------------------
sealed class ControllerState
{
    private readonly object _gate = new();
    private ControllerSnapshot? _snapshot;
    private string? _lastError;
    private DateTimeOffset _lastRestartAttempt = DateTimeOffset.MinValue;
    private int _restartAttempts;
    private readonly TimeSpan _restartBackoff = TimeSpan.FromSeconds(30);
    private const int MaxRestartAttempts = 4;

    public ControllerSnapshot? CurrentSnapshot
    {
        get { lock (_gate) { return _snapshot; } }
    }

    public void UpdateSnapshot(ControllerSnapshot snapshot)
    {
        lock (_gate) { _snapshot = snapshot; }
    }

    public void RecordFailure(string message)
    {
        lock (_gate) { _lastError = message; }
    }

    public bool ShouldAttemptRestart()
    {
        lock (_gate)
        {
            if (_restartAttempts >= MaxRestartAttempts)
            {
                return false;
            }
            return DateTimeOffset.UtcNow - _lastRestartAttempt >= _restartBackoff;
        }
    }

    public void RecordRestartAttempt(bool success, string message)
    {
        lock (_gate)
        {
            _lastRestartAttempt = DateTimeOffset.UtcNow;
            if (!success)
            {
                _restartAttempts++;
                _lastError = message;
            }
            else
            {
                _restartAttempts = 0;
                _lastError = null;
            }
        }
    }

    public void ClearRestartBackoff()
    {
        lock (_gate) { _restartAttempts = 0; }
    }
}
