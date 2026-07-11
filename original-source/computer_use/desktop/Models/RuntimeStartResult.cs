namespace DoubaoComputerUse.Desktop.Models;

public sealed class RuntimeStartResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string Diagnostic { get; init; } = "";
}
