namespace DoubaoComputerUse.Desktop.Models;

public sealed class ServiceSnapshot
{
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.Now;
    public EndpointSettings Endpoints { get; init; } = new();
    public ProbeResult ToolServer { get; init; } = new();
    public ProbeResult Planner { get; init; } = new();
    public ProbeResult Models { get; init; } = new();
    public ProbeResult McpServer { get; init; } = new();
    public ProbeResult Overlay { get; init; } = new();
    public IReadOnlyList<ModelOption> ModelOptions { get; init; } = [];
    public string OverlayJson { get; init; } = "";

    public bool IsReady => ToolServer.Ok && Planner.Ok && McpServer.Ok;
    public bool HasPartialService => ToolServer.Ok || Planner.Ok || McpServer.Ok || Overlay.Ok;
    public string AvailabilityState => IsReady ? "ready" : HasPartialService ? "degraded" : "unavailable";
}
