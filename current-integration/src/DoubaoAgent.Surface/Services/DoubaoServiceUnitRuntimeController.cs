using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using MyPowerTools.Abstractions;

namespace DoubaoAgent.Surface.Services;

public interface IDoubaoRuntimeSnapshotProvider
{
    Task<DoubaoControllerStatusSnapshot> GetCachedSnapshotAsync(
        CancellationToken cancellationToken = default);
}

public sealed record DoubaoControllerStatusSnapshot(
    bool SecuritySafe,
    string SecurityDetail,
    IReadOnlyList<DoubaoTcpListenerState> Listeners,
    IReadOnlyList<DoubaoOwnedProcessState> OwnedProcesses,
    bool ToolOnline,
    bool PlannerOnline,
    bool McpOnline,
    string RuntimeRoot,
    DateTimeOffset CheckedAt);

/// <summary>
/// Runtime controller used by the product Surface. Lifecycle ownership remains in the independent
/// controller Service Unit; this object only sends scoped lifecycle and business commands.
/// </summary>
public sealed class DoubaoServiceUnitRuntimeController :
    IDoubaoSecureRuntimeController,
    IDoubaoRuntimeSnapshotProvider
{
    public const string UnitId = "doubao-agent.controller.service";
    public const string PipeName = "mypowertools.doubao-agent.controller";

    private readonly IServiceUnitClient _serviceUnits;

    public DoubaoServiceUnitRuntimeController(IServiceUnitClient serviceUnits)
    {
        _serviceUnits = serviceUnits;
    }

    public async Task<DoubaoControllerStatusSnapshot> GetCachedSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var unit = await EnsureControllerRunningAsync(cancellationToken).ConfigureAwait(false);
        var pipeName = ResolvePipeName(unit);
        Exception? lastError = null;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                using var response = await SendAsync(
                    pipeName,
                    "state",
                    null,
                    TimeSpan.FromSeconds(5),
                    cancellationToken).ConfigureAwait(false);
                return ParseSnapshot(response.RootElement.GetProperty("data"));
            }
            catch (InvalidOperationException ex)
            {
                lastError = ex;
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }
        }

        throw lastError ?? new InvalidOperationException("Doubao controller state is unavailable.");
    }

    public async Task<DoubaoRuntimeSecurityState> InspectAsync(
        string runtimeRoot,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetCachedSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return new DoubaoRuntimeSecurityState(
            snapshot.SecuritySafe,
            snapshot.Listeners,
            snapshot.OwnedProcesses.Any(process => process.IsValidated),
            snapshot.SecurityDetail,
            snapshot.OwnedProcesses);
    }

    public async Task<DoubaoAgentOperationResult> StartAsync(
        string runtimeRoot,
        string secretFilePath,
        CancellationToken cancellationToken = default)
    {
        var unit = await EnsureControllerRunningAsync(cancellationToken).ConfigureAwait(false);
        using var response = await SendAsync(
            ResolvePipeName(unit),
            "start",
            new Dictionary<string, object?>
            {
                ["runtimeRoot"] = runtimeRoot,
                ["secretFile"] = secretFilePath
            },
            TimeSpan.FromSeconds(60),
            cancellationToken).ConfigureAwait(false);
        return ParseOperation(response.RootElement.GetProperty("data"));
    }

    public async Task<DoubaoAgentOperationResult> StopAsync(
        string runtimeRoot,
        CancellationToken cancellationToken = default)
    {
        var unit = await EnsureControllerRunningAsync(cancellationToken).ConfigureAwait(false);
        using var response = await SendAsync(
            ResolvePipeName(unit),
            "stop",
            new Dictionary<string, object?> { ["runtimeRoot"] = runtimeRoot },
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
        return ParseOperation(response.RootElement.GetProperty("data"));
    }

    public void Dispose()
    {
    }

    private async Task<ServiceUnitSnapshot> EnsureControllerRunningAsync(CancellationToken cancellationToken)
    {
        var units = await _serviceUnits.ListAsync(cancellationToken).ConfigureAwait(false);
        var unit = units.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, UnitId, StringComparison.Ordinal));
        if (unit is null)
        {
            await _serviceUnits.ReloadAsync(cancellationToken).ConfigureAwait(false);
            units = await _serviceUnits.ListAsync(cancellationToken).ConfigureAwait(false);
            unit = units.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, UnitId, StringComparison.Ordinal));
        }

        if (unit is null)
        {
            throw new InvalidOperationException($"Service Unit '{UnitId}' is not installed for this tool.");
        }

        if (unit.State is not ServiceUnitState.Active and not ServiceUnitState.Degraded)
        {
            unit = await _serviceUnits.StartAsync(UnitId, cancellationToken).ConfigureAwait(false);
        }

        if (unit.State is not ServiceUnitState.Active and not ServiceUnitState.Degraded)
        {
            throw new InvalidOperationException(
                unit.LastError ?? $"Service Unit '{UnitId}' did not become ready.");
        }

        return unit;
    }

    private static async Task<JsonDocument> SendAsync(
        string pipeName,
        string command,
        IReadOnlyDictionary<string, object?>? arguments,
        TimeSpan operationTimeout,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(operationTimeout);
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);

        var request = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["command"] = command
        };
        if (arguments is not null)
        {
            foreach (var pair in arguments)
            {
                request[pair.Key] = pair.Value;
            }
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(request);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await pipe.WriteAsync(header, timeout.Token).ConfigureAwait(false);
        await pipe.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
        await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);

        var responseHeader = new byte[4];
        await ReadExactlyAsync(pipe, responseHeader, timeout.Token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(responseHeader);
        if (length <= 0 || length > 1024 * 1024)
        {
            throw new InvalidDataException($"Doubao controller returned invalid length {length}.");
        }

        var responsePayload = new byte[length];
        await ReadExactlyAsync(pipe, responsePayload, timeout.Token).ConfigureAwait(false);
        var response = JsonDocument.Parse(responsePayload);
        if (!response.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var error = response.RootElement.TryGetProperty("error", out var errorElement)
                ? errorElement.GetString()
                : null;
            response.Dispose();
            throw new InvalidOperationException(error ?? $"Controller command '{command}' failed.");
        }
        return response;
    }

    private static string ResolvePipeName(ServiceUnitSnapshot unit)
    {
        var readiness = unit.Readiness;
        var address = readiness?.Address;
        return string.Equals(readiness?.Kind, "pipe", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(address)
            ? address
            : PipeName;
    }

    private static DoubaoControllerStatusSnapshot ParseSnapshot(JsonElement data)
    {
        var listeners = ReadArray(data, "listeners")
            .Select(item => new DoubaoTcpListenerState(
                ReadString(item, "address", ""),
                ReadInt32(item, "port"),
                ReadInt32(item, "processId")))
            .ToArray();
        var ownedProcesses = ReadArray(data, "ownedProcesses")
            .Select(item => new DoubaoOwnedProcessState(
                ReadString(item, "id", "unknown"),
                ReadInt32(item, "processId"),
                ReadInt32(item, "port"),
                ReadDateTimeOffset(item, "startedAtUtc"),
                ReadBoolean(item, "isValidated"),
                ReadString(item, "detail", "")))
            .ToArray();

        return new DoubaoControllerStatusSnapshot(
            ReadBoolean(data, "securitySafe"),
            ReadString(data, "securityDetail", "Controller state is unavailable."),
            listeners,
            ownedProcesses,
            ReadBoolean(data, "toolOnline"),
            ReadBoolean(data, "plannerOnline"),
            ReadBoolean(data, "mcpOnline"),
            ReadString(data, "runtimeRoot", ""),
            ReadDateTimeOffset(data, "checkedAt"));
    }

    private static DoubaoAgentOperationResult ParseOperation(JsonElement data) =>
        new(
            ReadBoolean(data, "success"),
            ReadString(data, "message", "Controller operation completed without a message."),
            ReadString(data, "technicalDetails", ""));

    private static IEnumerable<JsonElement> ReadArray(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToArray()
            : [];

    private static string ReadString(JsonElement element, string name, string fallback) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static int ReadInt32(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result)
            ? result
            : 0;

    private static bool ReadBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean();

    private static DateTimeOffset ReadDateTimeOffset(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), out var result)
            ? result
            : DateTimeOffset.MinValue;

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }
            offset += read;
        }
    }
}
