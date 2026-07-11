using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using DoubaoComputerUse.Desktop.Infrastructure;
using DoubaoComputerUse.Desktop.Models;
using DoubaoComputerUse.Desktop.Services;

namespace DoubaoComputerUse.Desktop.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private const string DefaultSystemPrompt =
        "你是本机 Windows VLM computer use agent。每一步必须先观察截图，基于截图完成 grounding，并返回一个可执行动作。动作坐标必须来自本轮截图和执行时获取到的坐标转换。遇到账户、安全、支付、验证码、删除、安装器确认时调用用户。";

    private readonly LocalRuntimeService _runtimeService;
    private readonly List<ModelOption> _fallbackModels =
    [
        new() { Name = "doubao-seed-2-0-lite-260428", DisplayName = "Doubao Seed 2.0 Lite" },
        new() { Name = "doubao-seed-2-1-turbo-260628", DisplayName = "Doubao Seed 2.1 Turbo" },
        new() { Name = "doubao-seed-2-1-pro-260628", DisplayName = "Doubao Seed 2.1 Pro" }
    ];

    private CancellationTokenSource? _taskCancellation;
    private int _traceIndex;
    private bool _isBusy;
    private bool _isRunningTask;
    private bool _autoStartEnabled = true;
    private string _selectedModelName = "doubao-seed-2-0-lite-260428";
    private string _taskPrompt = "打开 Edge 浏览器访问 https://example.com，等待页面加载完成后 finished(content='edge_example_loaded')。";
    private string _systemPrompt = DefaultSystemPrompt;
    private string _serviceState = "unavailable";
    private string _serviceStateText = "未检查";
    private string _serviceSummary = "等待检查";
    private string _lastCheckedText = "";
    private string _diagnosticText = "";
    private string _lastStartupDiagnostic = "";
    private ImageSource? _latestScreenshot;
    private TraceEvent? _selectedTrace;
    private int _overlayX = 640;
    private int _overlayY = 360;
    private int _overlayDurationMs = 1800;
    private int _overlayRadius = 34;

    public MainViewModel(LocalRuntimeService runtimeService)
    {
        _runtimeService = runtimeService;

        Models = new ObservableCollection<ModelOption>(_fallbackModels);
        Services = [];
        Trace = [];

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy, HandleCommandError);
        StartServicesCommand = new AsyncRelayCommand(async () => { await StartServicesAsync(); }, () => !IsBusy && !IsRunningTask, HandleCommandError);
        RunTaskCommand = new AsyncRelayCommand(RunTaskAsync, CanRunTask, HandleCommandError);
        StopTaskCommand = new RelayCommand(StopTask, () => IsRunningTask);
        ShowOverlayCommand = new AsyncRelayCommand(() => OverlayAsync("show"), () => !IsBusy && !IsRunningTask, HandleCommandError);
        HideOverlayCommand = new AsyncRelayCommand(() => OverlayAsync("hide"), () => !IsBusy && !IsRunningTask, HandleCommandError);
        OverlaySelfTestCommand = new AsyncRelayCommand(() => OverlayAsync("self-test"), () => !IsBusy && !IsRunningTask, HandleCommandError);
        ClearTraceCommand = new RelayCommand(ClearTrace);
    }

    public ObservableCollection<ModelOption> Models { get; }
    public ObservableCollection<ServiceRow> Services { get; }
    public ObservableCollection<TraceEvent> Trace { get; }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand StartServicesCommand { get; }
    public AsyncRelayCommand RunTaskCommand { get; }
    public RelayCommand StopTaskCommand { get; }
    public AsyncRelayCommand ShowOverlayCommand { get; }
    public AsyncRelayCommand HideOverlayCommand { get; }
    public AsyncRelayCommand OverlaySelfTestCommand { get; }
    public RelayCommand ClearTraceCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommand.NotifyCanExecuteChanged();
                StartServicesCommand.NotifyCanExecuteChanged();
                RunTaskCommand.NotifyCanExecuteChanged();
                ShowOverlayCommand.NotifyCanExecuteChanged();
                HideOverlayCommand.NotifyCanExecuteChanged();
                OverlaySelfTestCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsRunningTask
    {
        get => _isRunningTask;
        private set
        {
            if (SetProperty(ref _isRunningTask, value))
            {
                RunTaskCommand.NotifyCanExecuteChanged();
                StopTaskCommand.NotifyCanExecuteChanged();
                RefreshCommand.NotifyCanExecuteChanged();
                StartServicesCommand.NotifyCanExecuteChanged();
                ShowOverlayCommand.NotifyCanExecuteChanged();
                HideOverlayCommand.NotifyCanExecuteChanged();
                OverlaySelfTestCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool AutoStartEnabled
    {
        get => _autoStartEnabled;
        set => SetProperty(ref _autoStartEnabled, value);
    }

    public string SelectedModelName
    {
        get => _selectedModelName;
        set => SetProperty(ref _selectedModelName, value);
    }

    public string TaskPrompt
    {
        get => _taskPrompt;
        set
        {
            if (SetProperty(ref _taskPrompt, value))
            {
                RunTaskCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string SystemPrompt
    {
        get => _systemPrompt;
        set => SetProperty(ref _systemPrompt, value);
    }

    public string ServiceState
    {
        get => _serviceState;
        private set => SetProperty(ref _serviceState, value);
    }

    public string ServiceStateText
    {
        get => _serviceStateText;
        private set => SetProperty(ref _serviceStateText, value);
    }

    public string ServiceSummary
    {
        get => _serviceSummary;
        private set => SetProperty(ref _serviceSummary, value);
    }

    public string LastCheckedText
    {
        get => _lastCheckedText;
        private set => SetProperty(ref _lastCheckedText, value);
    }

    public string DiagnosticText
    {
        get => _diagnosticText;
        private set => SetProperty(ref _diagnosticText, value);
    }

    public ImageSource? LatestScreenshot
    {
        get => _latestScreenshot;
        private set => SetProperty(ref _latestScreenshot, value);
    }

    public TraceEvent? SelectedTrace
    {
        get => _selectedTrace;
        set
        {
            if (SetProperty(ref _selectedTrace, value))
            {
                OnPropertyChanged(nameof(SelectedTraceJson));
                OnPropertyChanged(nameof(SelectedTraceDetail));
            }
        }
    }

    public string SelectedTraceJson => SelectedTrace?.RawJson ?? "选择一条运行记录查看原始数据";

    public string SelectedTraceDetail
    {
        get
        {
            if (SelectedTrace is null)
            {
                return "选择一条运行记录查看详情";
            }

            var detail = string.IsNullOrWhiteSpace(SelectedTrace.Detail)
                ? "这条记录没有附加说明"
                : SelectedTrace.Detail;
            return $"{SelectedTrace.TimeText}  {SelectedTrace.Title}{Environment.NewLine}{Environment.NewLine}{detail}";
        }
    }

    public int OverlayX
    {
        get => _overlayX;
        set => SetProperty(ref _overlayX, Math.Max(0, value));
    }

    public int OverlayY
    {
        get => _overlayY;
        set => SetProperty(ref _overlayY, Math.Max(0, value));
    }

    public int OverlayDurationMs
    {
        get => _overlayDurationMs;
        set => SetProperty(ref _overlayDurationMs, Math.Clamp(value, 200, 6000));
    }

    public int OverlayRadius
    {
        get => _overlayRadius;
        set => SetProperty(ref _overlayRadius, Math.Clamp(value, 8, 120));
    }

    public string ComputerUseRoot => _runtimeService.ComputerUseRoot.FullName;
    public string SecretFilePath => _runtimeService.SecretFile.FullName;
    public string LogDirectory => _runtimeService.LogDirectory;

    public async Task InitializeAsync()
    {
        if (AutoStartEnabled)
        {
            await StartServicesAsync();
            return;
        }

        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var snapshot = await _runtimeService.GetStatusAsync(CancellationToken.None);
            ApplySnapshot(snapshot);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> StartServicesAsync()
    {
        IsBusy = true;
        ServiceState = "starting";
        ServiceStateText = ToStateText(ServiceState);
        ServiceSummary = "正在检查本地运行时";
        try
        {
            var progress = new Progress<string>(message => ServiceSummary = message);
            var result = await _runtimeService.EnsureStartedAsync(progress, CancellationToken.None);
            _lastStartupDiagnostic = result.Diagnostic;
            var snapshot = await _runtimeService.GetStatusAsync(CancellationToken.None);
            ApplySnapshot(snapshot);

            if (!result.Success && !snapshot.IsReady)
            {
                ServiceState = "failed";
                ServiceStateText = ToStateText(ServiceState);
                ServiceSummary = result.Message;
                DiagnosticText = BuildDiagnosticText(snapshot, result.Diagnostic);
                AddTrace(TraceEvent.FromMessage("startup_failed", result.Message, NextTraceIndex(), isError: true));
                return false;
            }

            ServiceSummary = result.Message;
            return snapshot.IsReady;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunTaskAsync()
    {
        if (AutoStartEnabled && !await StartServicesAsync())
        {
            return;
        }

        ClearTrace();
        IsRunningTask = true;
        _taskCancellation = new CancellationTokenSource();
        AddTrace(TraceEvent.FromMessage("request", TaskPrompt.Trim(), NextTraceIndex()));

        try
        {
            await _runtimeService.RunTaskStreamAsync(
                TaskPrompt.Trim(),
                SelectedModelName,
                SystemPrompt,
                AddTrace,
                _taskCancellation.Token);
            AddTrace(TraceEvent.FromMessage("stream_closed", "任务流已结束", NextTraceIndex()));
        }
        catch (OperationCanceledException)
        {
            AddTrace(TraceEvent.FromMessage("stream_stopped", "任务流已停止", NextTraceIndex()));
        }
        finally
        {
            _taskCancellation.Dispose();
            _taskCancellation = null;
            IsRunningTask = false;
            await RefreshAsync();
        }
    }

    private void StopTask()
    {
        _taskCancellation?.Cancel();
    }

    private async Task OverlayAsync(string command)
    {
        if (AutoStartEnabled && !await StartServicesAsync())
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _runtimeService.CallOverlayAsync(
                command,
                new OverlayRequest
                {
                    X = OverlayX,
                    Y = OverlayY,
                    DurationMs = OverlayDurationMs,
                    Radius = OverlayRadius
                },
                CancellationToken.None);
            AddTrace(TraceEvent.FromPayload(
                $$"""
                {"event":"overlay","command":"{{command}}","ok":{{result.Ok.ToString().ToLowerInvariant()}},"data":{{(string.IsNullOrWhiteSpace(result.Data) ? "\"\"" : result.Data)}}}
                """,
                NextTraceIndex()));
            await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ClearTrace()
    {
        Trace.Clear();
        SelectedTrace = null;
        LatestScreenshot = null;
        _traceIndex = 0;
    }

    private bool CanRunTask()
    {
        return !IsBusy && !IsRunningTask && !string.IsNullOrWhiteSpace(TaskPrompt);
    }

    private int NextTraceIndex()
    {
        _traceIndex += 1;
        return _traceIndex;
    }

    private void AddTrace(TraceEvent traceEvent)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            Trace.Insert(0, traceEvent);
            SelectedTrace ??= traceEvent;
            if (traceEvent.Screenshot is not null)
            {
                LatestScreenshot = traceEvent.Screenshot;
            }

            while (Trace.Count > 160)
            {
                Trace.RemoveAt(Trace.Count - 1);
            }
        });
    }

    private void ApplySnapshot(ServiceSnapshot snapshot)
    {
        ServiceState = snapshot.AvailabilityState;
        ServiceStateText = ToStateText(ServiceState);
        ServiceSummary = snapshot.AvailabilityState switch
        {
            "ready" => "可以运行任务",
            "degraded" => "部分服务已启动，请查看本地服务列表",
            _ => "本地运行时未启动"
        };
        LastCheckedText = $"上次检查 {snapshot.CheckedAt:HH:mm:ss}";
        DiagnosticText = BuildDiagnosticText(snapshot, _lastStartupDiagnostic);

        Services.Clear();
        Services.Add(ToRow("控制服务", snapshot.Endpoints.ToolServerUrl, snapshot.ToolServer));
        Services.Add(ToRow("任务规划", snapshot.Endpoints.PlannerUrl, snapshot.Planner));
        Services.Add(ToRow("Codex 连接", snapshot.Endpoints.McpServerUrl, snapshot.McpServer));
        Services.Add(ToRow("屏幕标记", "desktop overlay", snapshot.Overlay));

        var selected = SelectedModelName;
        Models.Clear();
        foreach (var model in MergeModels(snapshot.ModelOptions))
        {
            Models.Add(model);
        }

        if (Models.All(model => model.Name != selected))
        {
            SelectedModelName = Models.FirstOrDefault()?.Name ?? selected;
        }
        else
        {
            SelectedModelName = selected;
        }
    }

    private IEnumerable<ModelOption> MergeModels(IReadOnlyList<ModelOption> runtimeModels)
    {
        var byName = new Dictionary<string, ModelOption>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in _fallbackModels.Concat(runtimeModels))
        {
            byName[model.Name] = model;
        }

        return byName.Values.OrderBy(model => model.DisplayLabel);
    }

    private string BuildDiagnosticText(ServiceSnapshot snapshot, string startupDiagnostic)
    {
        var parts = new List<string>
        {
            $"运行目录: {ComputerUseRoot}",
            $"密钥文件: {SecretFilePath}",
            $"日志目录: {LogDirectory}",
            $"启动脚本: {_runtimeService.StartScriptPath}",
            $"Tool Server: {snapshot.Endpoints.ToolServerUrl} / {snapshot.ToolServer.Detail}",
            $"Planner: {snapshot.Endpoints.PlannerUrl} / {snapshot.Planner.Detail}",
            $"Models: {snapshot.Models.Detail}",
            $"MCP SSE: {snapshot.Endpoints.McpServerUrl} / {snapshot.McpServer.Detail}",
            $"Overlay: {snapshot.Overlay.Detail}",
            $"Overlay JSON: {snapshot.OverlayJson}"
        };

        if (!string.IsNullOrWhiteSpace(startupDiagnostic))
        {
            parts.Add($"启动详情:{Environment.NewLine}{startupDiagnostic}");
        }

        return string.Join(Environment.NewLine, parts);
    }

    private static ServiceRow ToRow(string name, string endpoint, ProbeResult probe)
    {
        var state = probe.Ok ? "ready" : "unavailable";
        return new ServiceRow
        {
            Name = name,
            State = state,
            StateText = ToStateText(state),
            Detail = probe.Ok ? "已连接" : "等待启动或恢复",
            Diagnostic = $"{endpoint} / {probe.Detail}"
        };
    }

    private void HandleCommandError(Exception exception)
    {
        IsBusy = false;
        IsRunningTask = false;
        ServiceState = "failed";
        ServiceStateText = ToStateText(ServiceState);
        ServiceSummary = "操作失败，请打开诊断查看详情";
        DiagnosticText = string.IsNullOrWhiteSpace(DiagnosticText)
            ? exception.ToString()
            : $"{DiagnosticText}{Environment.NewLine}{Environment.NewLine}{exception}";
        AddTrace(TraceEvent.FromMessage("error", exception.Message, NextTraceIndex(), isError: true));
    }

    private static string ToStateText(string state)
    {
        return state switch
        {
            "ready" => "已就绪",
            "starting" => "启动中",
            "degraded" => "部分就绪",
            "failed" => "失败",
            "unavailable" => "未启动",
            _ => "未知"
        };
    }
}
