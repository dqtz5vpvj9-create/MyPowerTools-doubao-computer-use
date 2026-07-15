using System.Text.Json;
using Avalonia.Media.Imaging;
using DoubaoAgent.Surface.Services;

using MyPowerTools.AvaloniaSdk;
namespace DoubaoAgent.Surface.ViewModels;

public sealed partial class DoubaoAgentViewModel
{
    private void ApplySnapshot(DoubaoAgentSnapshot snapshot)
    {
        if (snapshot.Revision > 0 && snapshot.Revision < _lastSnapshotRevision)
        {
            return;
        }
        if (snapshot.Revision > 0)
        {
            _lastSnapshotRevision = snapshot.Revision;
        }
        var selectedName = SelectedModel?.Name;
        _snapshot = snapshot;
        if (!snapshot.IsRefreshing && !snapshot.RuntimeSecurity.IsSafe)
        {
            _autoStartEnabled = false;
            _service.Session.AutoStartEnabled = false;
            OnPropertyChanged(nameof(AutoStartEnabled));
        }
        SelectedModel = snapshot.Models.FirstOrDefault(model => model.Name == selectedName)
            ?? snapshot.Models.FirstOrDefault(model =>
                string.Equals(model.Name, DoubaoAgentToolService.PreferredDefaultModelName, StringComparison.OrdinalIgnoreCase))
            ?? snapshot.Models.FirstOrDefault();
        SetProductState(MyPowerTools.AvaloniaSdk.ToolSurfaceState.Ready);
        NotifyRuntimeProperties();
        NotifyCommandStates();
    }

    private void NotifyRuntimeProperties()
    {
        foreach (var property in new[]
        {
            nameof(Services), nameof(Models), nameof(Logs), nameof(RuntimeProcesses), nameof(Configuration),
            nameof(RuntimeInstalled), nameof(SecretConfigured), nameof(IsRefreshing), nameof(HasServiceError),
            nameof(ShowRuntimeMissingWarning), nameof(ShowSecretMissingWarning),
            nameof(AllServicesOnline), nameof(AnyServiceOnline),
            nameof(RuntimeSecurity), nameof(HasUnsafeListeners), nameof(HasOwnedProcesses), nameof(CanEnableAutoStart),
            nameof(ToolServerOnline), nameof(PlannerOnline), nameof(McpServerOnline), nameof(ArkApiKeyConfigured),
            nameof(AuthKeyConfigured), nameof(AuthApiKeyConfigured), nameof(ArkApiKeyStatus), nameof(AuthKeyStatus),
            nameof(AuthApiKeyStatus), nameof(HasLogs),
            nameof(HasRuntimeProcesses), nameof(RuntimeRoot), nameof(CheckedAtText), nameof(RuntimeStartedText),
            nameof(ServiceSummary), nameof(RuntimeStatusText), nameof(RuntimeStatusDetail), nameof(OverlayStateText),
            nameof(OverlayDiagnosticText), nameof(ListenerSecurityText),
            nameof(ConfigurationSummary), nameof(IsRuntimeReady), nameof(CanStartRuntime), nameof(CanStopRuntime),
            nameof(CanRestartRuntime), nameof(CanRunTask), nameof(CanUseOverlay), nameof(DiagnosticText)
        })
        {
            OnPropertyChanged(property);
        }
    }

    private void NotifyCommandStates()
    {
        foreach (var command in new[]
        {
            RefreshCommand, StartRuntimeCommand, StopRuntimeCommand, RestartRuntimeCommand,
            RunTaskCommand, StopTaskCommand, ClearTraceCommand, ShowOverlayCommand, HideOverlayCommand,
            OverlaySelfTestCommand, SaveConfigurationCommand, TestConfigurationCommand
        }.OfType<MptAsyncRelayCommand>())
        {
            command.NotifyCanExecuteChanged();
        }
        OnPropertyChanged(nameof(CanStartRuntime));
        OnPropertyChanged(nameof(CanStopRuntime));
        OnPropertyChanged(nameof(CanRestartRuntime));
        OnPropertyChanged(nameof(CanRunTask));
        OnPropertyChanged(nameof(CanUseOverlay));
    }

    private static Bitmap? TryDecodeScreenshot(string dataUrl)
    {
        try
        {
            var comma = dataUrl.IndexOf(',');
            var payload = comma >= 0 ? dataUrl[(comma + 1)..] : dataUrl;
            if (payload.Length > 28_000_000)
            {
                return null;
            }
            var bytes = Convert.FromBase64String(payload);
            using var stream = new MemoryStream(bytes, writable: false);
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IOException)
        {
            return null;
        }
    }

    private static DoubaoAgentTaskEvent CreateMessageEvent(
        string kind,
        string title,
        string detail,
        bool isError = false)
    {
        var raw = JsonSerializer.Serialize(
            new { event_name = kind, message = detail },
            new JsonSerializerOptions { WriteIndented = true });
        return new DoubaoAgentTaskEvent(
            DateTimeOffset.Now,
            kind,
            title,
            detail,
            "",
            "",
            isError,
            raw);
    }

    private static DoubaoAgentTaskEvent CreateOverlayEvent(
        string command,
        DoubaoAgentOperationResult result)
    {
        var raw = JsonSerializer.Serialize(
            new
            {
                @event = "overlay",
                command,
                ok = result.Success,
                data = result.TechnicalDetails
            },
            new JsonSerializerOptions { WriteIndented = true });
        return new DoubaoAgentTaskEvent(
            DateTimeOffset.Now,
            "overlay",
            command switch
            {
                "show" => "显示屏幕标记",
                "hide" => "隐藏屏幕标记",
                "self-test" => "屏幕标记自检",
                _ => "屏幕标记操作"
            },
            result.Message,
            "",
            "",
            !result.Success,
            raw);
    }

    private static string YesNo(bool value) => value ? "已找到" : "缺失";

    private static string CaptureProtectionSuffix(bool? captureProtectionOk)
    {
        return captureProtectionOk switch
        {
            true => " · 已启用",
            false => " · 启用失败",
            null => ""
        };
    }

    private async Task<bool> EnsureRuntimeReadyAsync()
    {
        if (!RuntimeSecurity.IsSafe)
        {
            AutoStartEnabled = false;
            HasActionError = true;
            ActionMessage = $"无法读取本机服务端口状态：{RuntimeSecurity.Detail}";
            return false;
        }
        if (AllServicesOnline)
        {
            return true;
        }
        if (!AutoStartEnabled)
        {
            HasActionError = true;
            ActionMessage = "请先启动本地服务。";
            return false;
        }

        await RunRuntimeOperationAsync(AnyServiceOnline ? _service.RestartAsync : _service.StartAsync);
        return AllServicesOnline;
    }

    private string BuildDiagnosticText()
    {
        var lines = new List<string>
        {
            $"运行目录: {RuntimeRoot}",
            $"密钥文件: {Configuration.SecretFilePath} ({(SecretConfigured ? "已配置" : "缺失或为空")})",
            $"日志目录: {DoubaoSecureRuntimeController.ResolveLogsDirectory(RuntimeRoot)}",
            $"配置: {ConfigurationSummary}",
            $"端口状态: {(RuntimeSecurity.InspectionAvailable ? "已读取" : "读取失败")} / {ListenerSecurityText}",
            $"受控进程: {(HasOwnedProcesses ? "已验证" : "无")}"
        };
        lines.AddRange(Services.Select(service =>
            $"{service.DisplayName}: {service.Endpoint} / {service.Detail}"));
        lines.AddRange(RuntimeProcesses.Select(process =>
            $"{process.DisplayName}: PID {process.ProcessIdText} / port {process.PortText} / {process.StateText}"));
        lines.Add($"Overlay: {OverlayStateText} / {OverlayDiagnosticText}");
        if (!string.IsNullOrWhiteSpace(_snapshot.Overlay.RawJson))
        {
            lines.Add($"Overlay JSON:{Environment.NewLine}{_snapshot.Overlay.RawJson}");
        }
        if (!string.IsNullOrWhiteSpace(_lastOperationDiagnostic))
        {
            lines.Add($"最近一次控制操作:{Environment.NewLine}{_lastOperationDiagnostic}");
        }
        return string.Join(Environment.NewLine, lines);
    }
}

public sealed class DoubaoAgentTraceItemViewModel
{
    public DoubaoAgentTraceItemViewModel(DoubaoAgentTaskEvent taskEvent)
    {
        Kind = taskEvent.Kind;
        Title = taskEvent.Title;
        Detail = taskEvent.Detail;
        Action = taskEvent.Action;
        RawJson = taskEvent.RawJson;
        IsError = taskEvent.IsError;
        HasDetail = Detail.Length > 0;
        HasAction = Action.Length > 0 && Action != Title;
        TimeText = taskEvent.ReceivedAt.ToString("HH:mm:ss");
    }

    public string Kind { get; }
    public string Title { get; }
    public string Detail { get; }
    public string Action { get; }
    public string RawJson { get; }
    public bool IsError { get; }
    public bool HasDetail { get; }
    public bool HasAction { get; }
    public string TimeText { get; }
}
