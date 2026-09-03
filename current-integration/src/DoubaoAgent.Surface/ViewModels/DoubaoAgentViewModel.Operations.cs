using System.Diagnostics;
using System.Net.Http;
using Avalonia.Threading;
using DoubaoAgent.Surface.Services;

namespace DoubaoAgent.Surface.ViewModels;

public sealed partial class DoubaoAgentViewModel
{
    public void Activate()
    {
        if (Interlocked.Exchange(ref _isActivated, 1) != 0 || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _activationCancellation = new CancellationTokenSource();
        _service.SnapshotChanged += OnSnapshotChanged;
        _ = ActivateCoreAsync(_activationCancellation.Token);
    }

    private async Task ActivateCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _service.RefreshAsync(cancellationToken).ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            ApplySnapshot(snapshot);
            if (AutoStartEnabled && !AllServicesOnline)
            {
                await EnsureRuntimeReadyAsync().ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            HasActionError = true;
            ActionMessage = $"刷新失败：{LocalizeError(ex)}";
        }
    }

    private void OnSnapshotChanged(DoubaoAgentSnapshot snapshot)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplySnapshot(snapshot);
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                ApplySnapshot(snapshot);
            }
        });
    }

    public async Task InitializeAsync()
    {
        if (AutoStartEnabled && !AllServicesOnline)
        {
            await EnsureRuntimeReadyAsync().ConfigureAwait(true);
        }
    }

    private async Task RefreshAsync()
    {
        HasActionError = false;
        ActionMessage = "正在刷新状态…";
        try
        {
            var cancellationToken = _activationCancellation?.Token ?? CancellationToken.None;
            ApplySnapshot(await _service.RefreshAsync(cancellationToken));
            ActionMessage = "状态已刷新";
        }
        catch (Exception ex)
        {
            HasActionError = true;
            ActionMessage = $"刷新失败：{LocalizeError(ex)}";
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
            var cancellationToken = _activationCancellation?.Token ?? CancellationToken.None;
            var result = await operation(cancellationToken);
            HasActionError = !result.Success;
            ActionMessage = result.Message;
            _lastOperationDiagnostic = result.TechnicalDetails;
            ApplySnapshot(await _service.LoadAsync());
        }
        catch (Exception ex)
        {
            HasActionError = true;
            ActionMessage = $"运行时操作失败：{LocalizeError(ex)}";
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
            ActionMessage = $"任务失败：{LocalizeError(ex)}";
            await AddTraceOnUiAsync(CreateMessageEvent("error", "任务执行失败", LocalizeError(ex), isError: true));
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
                _lastOperationDiagnostic = $"任务结束后刷新状态失败：{LocalizeError(ex)}";
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
            var cancellationToken = _activationCancellation?.Token ?? CancellationToken.None;
            var result = await operation(cancellationToken);
            HasActionError = !result.Success;
            ActionMessage = result.Message;
            _lastOperationDiagnostic = result.TechnicalDetails;
            await AddTraceOnUiAsync(CreateOverlayEvent(command, result));
            ApplySnapshot(await _service.LoadAsync());
        }
        catch (Exception ex)
        {
            HasActionError = true;
            ActionMessage = $"定位标记操作失败：{LocalizeError(ex)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveConfigurationAsync()
    {
        IsBusy = true;
        HasSettingsError = false;
        SettingsMessage = "正在保存配置…";
        try
        {
            var saved = await _service.SaveConfigurationAsync(
                new DoubaoAgentConfigurationUpdate(
                    new DoubaoSecretUpdate(ArkApiKeyInput, AuthKeyInput, AuthApiKeyInput),
                    PlannerApiBaseUrl),
                CancellationToken.None);
            if (!saved.Success)
            {
                HasSettingsError = true;
                SettingsMessage = saved.Message;
                _lastOperationDiagnostic = saved.TechnicalDetails;
                return;
            }

            ArkApiKeyInput = "";
            AuthKeyInput = "";
            AuthApiKeyInput = "";

            var current = await _service.LoadAsync();
            DoubaoAgentOperationResult runtimeResult;
            if (current.HasOwnedProcesses)
            {
                SettingsMessage = "配置已保存，正在重新启动服务…";
                runtimeResult = await _service.RestartAsync();
            }
            else if (!current.AnyServiceOnline && current.RuntimeInstalled)
            {
                SettingsMessage = "配置已保存，正在启动服务…";
                runtimeResult = await _service.StartAsync();
            }
            else
            {
                runtimeResult = new DoubaoAgentOperationResult(
                    false,
                    "配置已保存，但当前服务由外部进程托管，尚未应用新配置。请停止外部服务后再从 MyPowerTools 启动。",
                    "external-runtime-not-restarted");
            }

            HasSettingsError = !runtimeResult.Success;
            SettingsMessage = runtimeResult.Success
                ? "配置已保存，服务状态已刷新。"
                : runtimeResult.Message;
            _lastOperationDiagnostic = runtimeResult.TechnicalDetails;
            ApplySnapshot(await _service.LoadAsync());
        }
        catch (Exception ex)
        {
            HasSettingsError = true;
            SettingsMessage = $"保存配置失败：{LocalizeError(ex)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task TestConfigurationAsync()
    {
        IsBusy = true;
        HasSettingsError = false;
        SettingsMessage = "正在测试 Planner 与模型 API…";
        try
        {
            var result = await _service.TestConfigurationAsync();
            HasSettingsError = !result.Success;
            SettingsMessage = result.Message;
            _lastOperationDiagnostic = result.TechnicalDetails;
            ApplySnapshot(await _service.LoadAsync());
        }
        catch (Exception ex)
        {
            HasSettingsError = true;
            SettingsMessage = $"连接测试失败：{LocalizeError(ex)}";
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

    private static string LocalizeError(Exception ex) => ex switch
    {
        HttpRequestException => "无法连接到运行时服务，请检查服务是否已启动。",
        TimeoutException or TaskCanceledException => "操作超时，请重试。",
        _ when ex.Message.Contains("refused") => "运行时服务拒绝连接。",
        _ => ex.Message
    };
}
