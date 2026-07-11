namespace DoubaoComputerUse.Desktop.Models;

public sealed class ServiceRow
{
    public string Name { get; init; } = "";
    public string State { get; init; } = "unavailable";
    public string StateText { get; init; } = "未就绪";
    public string Detail { get; init; } = "";
    public string Diagnostic { get; init; } = "";
}
