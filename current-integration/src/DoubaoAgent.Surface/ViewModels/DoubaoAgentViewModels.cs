using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DoubaoAgent.Surface.Services;

using MyPowerTools.AvaloniaSdk;
namespace DoubaoAgent.Surface.ViewModels;

public sealed partial class DoubaoAgentViewModel : MyPowerTools.AvaloniaSdk.ToolSurfacePageViewModel, IDisposable
{
    public const int MaxTraceItems = 64;
    private readonly DoubaoAgentToolService _service;
    private readonly DoubaoRunReport _runReport = new();
    private string _runReportFeedback = "";
    private readonly bool _ownsService;
    private DoubaoAgentSnapshot _snapshot;
    private CancellationTokenSource? _taskCancellation;
    private string _instruction;
    private string _systemPrompt;
    private DoubaoAgentModel? _selectedModel;
    private DoubaoAgentTraceItemViewModel? _selectedTrace;
    private bool _autoStartEnabled;
    private bool _showSystemPrompt;
    private bool _isBusy;
    private bool _isTaskRunning;
    private bool _hasActionError;
    private string _actionMessage = "已就绪";
    private string _taskDurationText = "0 秒";
    private int _screenshotCount;
    private int _actionCount;
    private int _groundingCount;
    private Bitmap? _latestScreenshot;
    private Stopwatch? _taskStopwatch;
    private string _lastOperationDiagnostic = "";
    private int _overlayX;
    private int _overlayY;
    private int _overlayDurationMs;
    private int _overlayRadius;
    private string _arkApiKeyInput = "";
    private string _authKeyInput = "";
    private string _authApiKeyInput = "";
    private string _plannerApiBaseUrl;
    private string _settingsMessage = "输入新密钥后保存；已保存的密钥永远不会回显。";
    private bool _hasSettingsError;
    private CancellationTokenSource? _activationCancellation;
    private int _isActivated;
    private int _disposed;
    private long _lastSnapshotRevision;

    public DoubaoAgentViewModel(DoubaoAgentSnapshot snapshot)
        : this(snapshot, new DoubaoAgentToolService(), ownsService: true)
    {
    }

    public DoubaoAgentViewModel(DoubaoAgentSnapshot snapshot, DoubaoAgentToolService service)
        : this(snapshot, service, ownsService: false)
    {
    }

    public DoubaoAgentViewModel(
        DoubaoAgentSnapshot snapshot,
        DoubaoAgentToolService service,
        bool ownsService)
        : this(snapshot, service, ownsService, initialize: true)
    {
    }

    private DoubaoAgentViewModel(
        DoubaoAgentSnapshot snapshot,
        DoubaoAgentToolService service,
        bool ownsService,
        bool initialize = true)
        : base(
            "豆包 Computer Use",
            "用自然语言交给豆包视觉代理执行本机电脑任务",
            MyPowerTools.AvaloniaSdk.ToolSurfaceState.Ready)
    {
        _snapshot = snapshot;
        _service = service;
        _ownsService = ownsService;
        _instruction = service.Session.Instruction;
        _systemPrompt = service.Session.SystemPrompt;
        _autoStartEnabled = service.Session.AutoStartEnabled;
        _showSystemPrompt = service.Session.ShowSystemPrompt;
        _overlayX = service.Session.OverlayX;
        _overlayY = service.Session.OverlayY;
        _overlayDurationMs = service.Session.OverlayDurationMs;
        _overlayRadius = service.Session.OverlayRadius;
        _plannerApiBaseUrl = service.Session.PlannerApiBaseUrl;
        _selectedModel = snapshot.Models.FirstOrDefault(model =>
            string.Equals(model.Name, service.Session.SelectedModelName, StringComparison.OrdinalIgnoreCase))
            ?? snapshot.Models.FirstOrDefault(model =>
                string.Equals(model.Name, DoubaoAgentToolService.PreferredDefaultModelName, StringComparison.OrdinalIgnoreCase))
            ?? snapshot.Models.FirstOrDefault();
        Trace = new ObservableCollection<DoubaoAgentTraceItemViewModel>();

        RefreshCommand = new MptAsyncRelayCommand(RefreshAsync, () => !IsBusy && !IsTaskRunning);
        StartRuntimeCommand = new MptAsyncRelayCommand(
            () => RunRuntimeOperationAsync(_service.StartAsync),
            () => CanStartRuntime);
        StopRuntimeCommand = new MptAsyncRelayCommand(
            StopRuntimeAsync,
            () => CanStopRuntime);
        RestartRuntimeCommand = new MptAsyncRelayCommand(
            () => RunRuntimeOperationAsync(_service.RestartAsync),
            () => CanRestartRuntime);
        RunTaskCommand = new MptAsyncRelayCommand(RunTaskAsync, () => CanRunTask);
        StopTaskCommand = new MptAsyncRelayCommand(StopTaskAsync, () => IsTaskRunning);
        ClearTraceCommand = new MptAsyncRelayCommand(ClearTraceAsync, () => Trace.Count > 0 || LatestScreenshot is not null);
        ShowOverlayCommand = new MptAsyncRelayCommand(
            () => RunOverlayOperationAsync("show", token => _service.CallOverlayAsync(
                "show", OverlayX, OverlayY, OverlayDurationMs, OverlayRadius, token)),
            () => CanUseOverlay);
        HideOverlayCommand = new MptAsyncRelayCommand(
            () => RunOverlayOperationAsync("hide", _service.HideOverlayAsync),
            () => CanUseOverlay);
        OverlaySelfTestCommand = new MptAsyncRelayCommand(
            () => RunOverlayOperationAsync("self-test", token => _service.CallOverlayAsync(
                "self-test", OverlayX, OverlayY, OverlayDurationMs, OverlayRadius, token)),
            () => CanUseOverlay);
        SaveConfigurationCommand = new MptAsyncRelayCommand(
            SaveConfigurationAsync,
            () => !IsBusy && !IsTaskRunning);
        TestConfigurationCommand = new MptAsyncRelayCommand(
            TestConfigurationAsync,
            () => !IsBusy && !IsTaskRunning && ArkApiKeyConfigured);
        ApplySnapshot(snapshot);
    }

    public ObservableCollection<DoubaoAgentTraceItemViewModel> Trace { get; }
    public ICommand RefreshCommand { get; }
    public ICommand StartRuntimeCommand { get; }
    public ICommand StopRuntimeCommand { get; }
    public ICommand RestartRuntimeCommand { get; }
    public ICommand RunTaskCommand { get; }
    public ICommand StopTaskCommand { get; }
    public ICommand ClearTraceCommand { get; }
    public ICommand ShowOverlayCommand { get; }
    public ICommand HideOverlayCommand { get; }
    public ICommand OverlaySelfTestCommand { get; }
    public ICommand SaveConfigurationCommand { get; }
    public ICommand TestConfigurationCommand { get; }

    public IReadOnlyList<DoubaoAgentServiceStatus> Services => _snapshot.Services;
    public IReadOnlyList<DoubaoAgentModel> Models => _snapshot.Models;
    public IReadOnlyList<DoubaoAgentLogFile> Logs => _snapshot.Logs;
    public IReadOnlyList<DoubaoAgentRuntimeProcess> RuntimeProcesses => _snapshot.Runtime.Processes;
    public DoubaoAgentConfigurationState Configuration => _snapshot.Configuration;
    public bool RuntimeInstalled => _snapshot.RuntimeInstalled;
    public bool SecretConfigured => _snapshot.SecretConfigured;
    public bool IsRefreshing => _snapshot.IsRefreshing;
    public bool HasServiceError => !IsRefreshing && !AllServicesOnline;
    public bool ShowRuntimeMissingWarning => !IsRefreshing && !RuntimeInstalled;
    public bool ShowSecretMissingWarning => !IsRefreshing && !SecretConfigured;
    public bool AllServicesOnline => _snapshot.AllServicesOnline;
    public bool AnyServiceOnline => _snapshot.AnyServiceOnline;
    public DoubaoRuntimeSecurityState RuntimeSecurity => _snapshot.RuntimeSecurity;
    public bool HasUnsafeListeners => !IsRefreshing && !RuntimeSecurity.IsSafe;
    public bool HasOwnedProcesses => _snapshot.HasOwnedProcesses;
    public bool RestartExhausted => _snapshot.RestartExhausted;
    public bool CanEnableAutoStart => RuntimeSecurity.IsSafe;
    public bool ToolServerOnline => Services.FirstOrDefault(service => service.Id == "tool")?.IsOnline ?? false;
    public bool PlannerOnline => Services.FirstOrDefault(service => service.Id == "planner")?.IsOnline ?? false;
    public bool McpServerOnline => Services.FirstOrDefault(service => service.Id == "mcp")?.IsOnline ?? false;
    public bool ArkApiKeyConfigured => Configuration.ArkApiKeyConfigured;
    public bool AuthKeyConfigured => Configuration.AuthKeyConfigured;
    public bool AuthApiKeyConfigured => Configuration.AuthApiKeyConfigured;
    public string ArkApiKeyStatus => ArkApiKeyConfigured ? "已配置" : "未配置";
    public string AuthKeyStatus => AuthKeyConfigured ? "已配置" : "未配置";
    public string AuthApiKeyStatus => AuthApiKeyConfigured ? "已配置" : "未配置";
    public bool HasTrace => Trace.Count > 0;
    public bool HasRunReport => _runReport.EventCount > 0;
    public string RunReportFeedback => _runReportFeedback;
    public string CreateRunReport() => _runReport.CreateText(IsTaskRunning ? "Running" : ActionMessage, TaskDurationText);
    public void SetRunReportFeedback(string message)
    {
        SetProperty(ref _runReportFeedback, message, nameof(RunReportFeedback));
    }
    public bool HasLogs => Logs.Count > 0;
    public bool HasRuntimeProcesses => RuntimeProcesses.Count > 0;
    public bool HasLatestScreenshot => LatestScreenshot is not null;
    public string RuntimeRoot => _snapshot.RuntimeRoot;
    public string CheckedAtText => $"更新于 {_snapshot.CheckedAt:HH:mm:ss}";
    public string RuntimeStartedText => _snapshot.Runtime.StartedAt is { } startedAt
        ? $"上次启动 {startedAt:MM-dd HH:mm}"
        : "没有启动记录";
    public string ServiceSummary => $"{_snapshot.OnlineServiceCount} / {Services.Count} 个核心服务在线";
    public string RuntimeStatusText => IsRefreshing
        ? "正在检查状态"
        : HasUnsafeListeners
        ? "状态读取失败"
        : !RuntimeInstalled
        ? "未安装"
        : AllServicesOnline && SecretConfigured
            ? "可以执行任务"
            : AllServicesOnline
                ? "服务在线，等待密钥"
            : RestartExhausted
                ? "自动重启已耗尽"
            : AnyServiceOnline
                ? "部分服务异常"
                : "服务已停止";
    public string RuntimeStatusDetail => IsRefreshing
        ? "页面已经就绪，服务、模型和运行时状态正在后台更新。"
        : HasUnsafeListeners
        ? RuntimeSecurity.Detail
        : !RuntimeInstalled
        ? "本机未找到豆包 Computer Use 运行时目录。"
        : !SecretConfigured
            ? "运行时已就绪，请先配置 ARK_API_KEY。"
            : AllServicesOnline
                ? "Planner、Tool Server 与 MCP Server 均已连接。"
                : RestartExhausted
                    ? "自动重启已耗尽，请手动重启。"
                : AnyServiceOnline
                    ? "当前服务状态不完整，可重新启动整套运行时。"
                    : "启动运行时后即可提交电脑操作任务。";
    public string OverlayStateText => _snapshot.Overlay.IsVisible
        ? "定位标记正在显示"
        : _snapshot.Overlay.IsRunning && _snapshot.Overlay.IsReady
            ? "定位标记层已就绪"
            : _snapshot.Overlay.IsRunning
                ? "定位标记层正在启动"
                : "定位标记层未运行";
    public string OverlayDiagnosticText => string.IsNullOrWhiteSpace(_snapshot.Overlay.ExclusionMethod)
        ? "捕获排除方式未报告"
        : $"捕获排除：{_snapshot.Overlay.ExclusionMethod}{CaptureProtectionSuffix(_snapshot.Overlay.CaptureProtectionOk)}";
    public string ConfigurationSummary =>
        $"Tool 配置 {YesNo(Configuration.ToolConfigExists)} · Planner 配置 {YesNo(Configuration.PlannerConfigExists)} · " +
        $"ARK {ArkApiKeyStatus} · Tool 认证 {AuthKeyStatus} · MCP 认证 {AuthApiKeyStatus}";
    public string ListenerSecurityText => RuntimeSecurity.Listeners.Count == 0
        ? RuntimeSecurity.Detail
        : string.Join(" · ", RuntimeSecurity.Listeners.Select(listener =>
            $"{listener.Address}:{listener.Port} / PID {listener.ProcessId}"));
    public string DiagnosticText => BuildDiagnosticText();
    public bool IsRuntimeReady => RuntimeInstalled && SecretConfigured;
    public bool CanStartRuntime => RuntimeInstalled && RuntimeSecurity.IsSafe && !AnyServiceOnline && !IsBusy && !IsTaskRunning;
    public bool CanStopRuntime => HasOwnedProcesses && !IsBusy && !IsTaskRunning;
    public bool CanRestartRuntime => RuntimeInstalled && RuntimeSecurity.IsSafe && HasOwnedProcesses && !IsBusy && !IsTaskRunning;
    public bool CanRunTask => IsRuntimeReady &&
                              RuntimeSecurity.IsSafe &&
                              !IsBusy &&
                              !IsTaskRunning &&
                              SelectedModel is not null &&
                              !string.IsNullOrWhiteSpace(Instruction) &&
                              (AllServicesOnline || AutoStartEnabled);
    public bool CanUseOverlay => IsRuntimeReady &&
                                 RuntimeSecurity.IsSafe &&
                                 !IsBusy &&
                                 !IsTaskRunning &&
                                 (ToolServerOnline || AutoStartEnabled);

}
