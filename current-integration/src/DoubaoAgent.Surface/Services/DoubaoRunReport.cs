using System.Text;

namespace DoubaoAgent.Surface.Services;

/// <summary>Retains a chronological text report independently of the UI's 64-row trace window.</summary>
public sealed class DoubaoRunReport(int maxCharacters = 4 * 1024 * 1024)
{
    private readonly StringBuilder _entries = new();
    private DoubaoAgentTaskRequest? _request;
    private DateTimeOffset? _startedAt;
    public int EventCount { get; private set; }
    public bool IsTruncated { get; private set; }

    public void Reset(DoubaoAgentTaskRequest? request = null, DateTimeOffset? startedAt = null)
    {
        _entries.Clear();
        _request = request;
        _startedAt = startedAt;
        EventCount = 0;
        IsTruncated = false;
    }

    public void Add(DoubaoAgentTaskEvent taskEvent)
    {
        EventCount++;
        if (IsTruncated) return;
        // Screenshots and raw transport JSON remain in the live details pane, not the text report.
        var entry = new StringBuilder()
            .AppendLine($"[{taskEvent.ReceivedAt:O}] {taskEvent.Kind}: {taskEvent.Title}")
            .AppendLine(taskEvent.Detail);
        if (!string.IsNullOrWhiteSpace(taskEvent.Action)) entry.AppendLine($"Action: {taskEvent.Action}");
        if (taskEvent.IsError) entry.AppendLine("Result: error");
        entry.AppendLine();
        var remaining = Math.Max(0, maxCharacters - _entries.Length);
        var text = entry.ToString();
        _entries.Append(text.AsSpan(0, Math.Min(text.Length, remaining)));
        IsTruncated = text.Length > remaining;
    }

    public string CreateText(string status, string duration)
    {
        var text = new StringBuilder("Doubao Computer Use — task report\n")
            .AppendLine($"Started: {_startedAt:O}")
            .AppendLine($"Model: {_request?.ModelName ?? "(no task submitted)"}")
            .AppendLine($"Status: {status}")
            .AppendLine($"Duration: {duration}")
            .AppendLine($"Events observed: {EventCount}")
            .AppendLine().AppendLine("Instruction:").AppendLine(_request?.Instruction ?? "")
            .AppendLine().AppendLine("System prompt:").AppendLine(_request?.SystemPrompt ?? "")
            .AppendLine().AppendLine("Chronological events:").Append(_entries);
        if (IsTruncated) text.AppendLine().AppendLine("[Report text limit reached; later event text is omitted.]");
        return text.ToString();
    }
}
