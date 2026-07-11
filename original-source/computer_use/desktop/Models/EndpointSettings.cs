namespace DoubaoComputerUse.Desktop.Models;

public sealed class EndpointSettings
{
    public string ToolServerUrl { get; init; } = "http://127.0.0.1:38102";
    public string PlannerUrl { get; init; } = "http://127.0.0.1:38189";
    public string McpServerUrl { get; init; } = "http://127.0.0.1:38080/sse";
}

