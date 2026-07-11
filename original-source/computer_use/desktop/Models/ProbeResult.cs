namespace DoubaoComputerUse.Desktop.Models;

public sealed class ProbeResult
{
    public bool Ok { get; init; }
    public int? Status { get; init; }
    public long Ms { get; init; }
    public string Error { get; init; } = "";
    public string Data { get; init; } = "";

    public string State => Ok ? "ready" : "unavailable";

    public string Detail
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Error))
            {
                return Error;
            }

            return Status.HasValue ? $"HTTP {Status} / {Ms}ms" : $"{Ms}ms";
        }
    }
}
