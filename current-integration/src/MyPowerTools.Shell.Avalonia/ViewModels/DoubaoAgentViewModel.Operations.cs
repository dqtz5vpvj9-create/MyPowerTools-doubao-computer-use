using System.Diagnostics;
using Avalonia.Threading;
using MyPowerTools.Shell.Avalonia.Services;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed partial class DoubaoAgentViewModel
{
    public async Task InitializeAsync()
    {
        if (AutoStartEnabled && !AllServicesOnline)
        {
            await EnsureRuntimeReadyAsync().ConfigureAwait(true);
        }
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        HasActionError = false;
        try
        {
            ApplySnapshot(await _service.LoadAsync());
            ActionMessage = "状态已刷新";
        }
        catch (Exception ex)
        {
            HasActionError = true;
            ActionMessage = $"刷新失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunRuntimeOperationAsync(
        Func<CancellationToken, Task<DoubaoAgentOperationResult>> operation)
    {
        IsBusy = true;
        HasActionError = false;
        ActionMessage = "正在更新运行时…";
        try
        {
            var result = await operation(CancellationToken.None);
            HasActionError = !result.Success;
            ActionMessage = result.Message;
            _lastOperationDiagnostic = result.TechnicalDetails;
            ApplySnapshot(await _service.LoadAsync());
        }
        catch (Exception ex)
        {
            HasActionError = true;
            ActionMessage = $"运行时操作失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StopRuntimeAsync()
    {
        _taskCancellation?.Cancel();
        await RunRuntimeOperationAsync(_service.StopAsync);
    }

    private async Task RunTaskAsync()
    {
        if (SelectedModel is null)
        {
            return;
        }

        if (!AllServicesOnline && !await EnsureRuntimeReadyAsync())
        {
            return;
        }

        _taskCancellation?.Dispose();
        _taskCancellation = new CancellationTokenSource();
        ClearTraceCore();
        IsTaskRunning = true;
        HasActionError = false;
        ActionMessage = "豆包正在观察屏幕并执行任务…";
        _taskStopwatch = Stopwatch.StartNew();
        AddTrace(CreateMessageEvent("request", "已提交任务", Instruction.Trim()));

        try
        {
            await _service.RunTaskAsync(
                new DoubaoAgentTaskRequest(Instruction, SelectedModel.Name, SystemPrompt),
                AddTraceOnUiAsync,
                _taskCancellation.Token);
            ActionMessage = "任务流已完成";
            await AddTraceOnUiAsync(CreateMessageEvent("stream_closed", "任务流已完成", "Planner 已关闭本次任务事件流。"));
        }
        catch (OperationCanceledException)
        {
            ActionMessage = "任务已停止";
            await AddTraceOnUiAsync(CreateMessageEvent(
                "stream_stopped",
                "任务已停止",
                "已停止接收并执行后续动作。"));
        }
        catch (Exception ex)
        {
            HasActionError = true;
            ActionMessage = $"任务失败：{ex.Message}";
            await AddTraceOnUiAsync(CreateMessageEvent("error", "任务执行失败", ex.Message, isError: true));
        }
        finally
        {
            _taskStopwatch?.Stop();
            UpdateTaskDuration();
            _taskCancellation?.Dispose();
            _taskCancellation = null;
            IsTaskRunning = false;
            try
            {
                ApplySnapshot(await _service.LoadAsync());
            }
            catch (Exception ex)
            {
                _lastOperationDiagnostic = $"任务结束后刷新状态失败：{ex.Message}";
                OnPropertyChanged(nameof(DiagnosticText));
            }
        }
    }

    private Task StopTaskAsync()
    {
        _taskCancellation?.Cancel();
        return Task.CompletedTask;
    }

    private Task ClearTraceAsync()
    {
        ClearTraceCore();
        ActionMessage = "运行记录已清空";
        return Task.CompletedTask;
    }

    private async Task RunOverlayOperationAsync(
        string command,
        Func<CancellationToken, Task<DoubaoAgentOperationResult>> operation)
    {
        if (!ToolServerOnline && !await EnsureRuntimeReadyAsync().ConfigureAwait(true))
        {
            return;
        }

        IsBusy = true;
        HasActionError = false;
        try
        {
            var result = await operation(CancellationToken.None);
            HasActionError = !result.Success;
            ActionMessage = result.Message;
            _lastOperationDiagnostic = result.TechnicalDetails;
            await AddTraceOnUiAsync(CreateOverlayEvent(command, result));
            ApplySnapshot(await _service.LoadAsync());
        }
        catch (Exception ex)
        {
            HasActionError = true;
            ActionMessage = $"定位标记操作失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AddTraceOnUiAsync(DoubaoAgentTaskEvent taskEvent)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            AddTrace(taskEvent);
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() => AddTrace(taskEvent));
    }

    private void AddTrace(DoubaoAgentTaskEvent taskEvent)
    {
        var item = new DoubaoAgentTraceItemViewModel(taskEvent);
        Trace.Insert(0, item);
        SelectedTrace ??= item;
        while (Trace.Count > MaxTraceItems)
        {
            Trace.RemoveAt(Trace.Count - 1);
        }

        if (taskEvent.ScreenshotDataUrl.Length > 0)
        {
            var screenshot = TryDecodeScreenshot(taskEvent.ScreenshotDataUrl);
            if (screenshot is not null)
            {
                LatestScreenshot = screenshot;
            }
            ScreenshotCount++;
        }
        if (taskEvent.Action.Length > 0 && taskEvent.Action != "开始")
        {
            ActionCount++;
        }
        if (taskEvent.Kind is "agent_step" or "agent_step_retry" ||
            taskEvent.Detail.Contains("grounding", StringComparison.OrdinalIgnoreCase))
        {
            GroundingCount++;
        }
        UpdateTaskDuration();
        OnPropertyChanged(nameof(HasTrace));
        NotifyCommandStates();
    }

    private void ClearTraceCore()
    {
        Trace.Clear();
        SelectedTrace = null;
        LatestScreenshot = null;
        ScreenshotCount = 0;
        ActionCount = 0;
        GroundingCount = 0;
        TaskDurationText = "0 秒";
        OnPropertyChanged(nameof(HasTrace));
        NotifyCommandStates();
    }

    private void UpdateTaskDuration()
    {
        TaskDurationText = $"{Math.Round(_taskStopwatch?.Elapsed.TotalSeconds ?? 0):0} 秒";
    }
}
