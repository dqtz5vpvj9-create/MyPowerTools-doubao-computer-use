using DoubaoAgent.Surface.Services;
using Xunit;

namespace PersonalUx.Tests;

public sealed class PersonalUxRunReportTests
{
    [Fact]
    public void Report_keeps_more_than_the_ui_window_in_chronological_order_and_uses_submitted_context()
    {
        var report = new DoubaoRunReport();
        report.Reset(new DoubaoAgentTaskRequest("original instruction", "submitted-model", "original system prompt"), DateTimeOffset.Parse("2026-09-05T10:00:00+09:00"));
        for (var i = 0; i < 100; i++) report.Add(Event($"event-{i:000}"));
        var text = report.CreateText("Complete", "10 seconds");
        Assert.Contains("Events observed: 100", text);
        Assert.Contains("submitted-model", text);
        Assert.Contains("original instruction", text);
        Assert.True(text.IndexOf("event-000", StringComparison.Ordinal) < text.IndexOf("event-099", StringComparison.Ordinal));
        Assert.DoesNotContain("raw-transport-payload", text);
        Assert.DoesNotContain("data:image", text);
        Assert.False(report.IsTruncated);
    }

    [Fact]
    public void Report_marks_truncation_and_reset_removes_previous_task()
    {
        var report = new DoubaoRunReport(80);
        report.Add(Event(new string('x', 300)));
        report.Add(Event("not retained"));
        Assert.True(report.IsTruncated);
        Assert.Contains("later event text is omitted", report.CreateText("Stopped", "1 second"));
        Assert.Equal(2, report.EventCount);
        report.Reset(new DoubaoAgentTaskRequest("new task", "new model", ""));
        Assert.Equal(0, report.EventCount);
        Assert.False(report.IsTruncated);
        Assert.DoesNotContain(new string('x', 20), report.CreateText("Ready", "0 seconds"));
    }

    private static DoubaoAgentTaskEvent Event(string detail) => new(
        DateTimeOffset.Parse("2026-09-05T10:01:00+09:00"), "agent_step", "Step", detail,
        "click", "data:image/png;base64,ignored", false, "raw-transport-payload");
}
