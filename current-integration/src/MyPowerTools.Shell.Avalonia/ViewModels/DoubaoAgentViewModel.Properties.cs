using Avalonia.Media.Imaging;
using MyPowerTools.Shell.Avalonia.Services;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed partial class DoubaoAgentViewModel
{
    public bool AutoStartEnabled
    {
        get => _autoStartEnabled;
        set
        {
            if (value && !RuntimeSecurity.IsSafe)
            {
                value = false;
            }
            if (SetProperty(ref _autoStartEnabled, value))
            {
                _service.Session.AutoStartEnabled = value;
                _service.PersistSessionBestEffort();
                NotifyCommandStates();
            }
        }
    }

    public string Instruction
    {
        get => _instruction;
        set
        {
            if (SetProperty(ref _instruction, value))
            {
                _service.Session.Instruction = value;
                _service.PersistSessionBestEffort();
                NotifyCommandStates();
            }
        }
    }

    public string SystemPrompt
    {
        get => _systemPrompt;
        set
        {
            if (SetProperty(ref _systemPrompt, value))
            {
                _service.Session.SystemPrompt = value;
                _service.PersistSessionBestEffort();
            }
        }
    }

    public DoubaoAgentModel? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (SetProperty(ref _selectedModel, value))
            {
                if (value is not null)
                {
                    _service.Session.SelectedModelName = value.Name;
                    _service.PersistSessionBestEffort();
                }
                NotifyCommandStates();
            }
        }
    }

    public bool ShowSystemPrompt
    {
        get => _showSystemPrompt;
        set
        {
            if (SetProperty(ref _showSystemPrompt, value))
            {
                _service.Session.ShowSystemPrompt = value;
                _service.PersistSessionBestEffort();
            }
        }
    }

    public DoubaoAgentTraceItemViewModel? SelectedTrace
    {
        get => _selectedTrace;
        set
        {
            if (SetProperty(ref _selectedTrace, value))
            {
                OnPropertyChanged(nameof(SelectedTraceDetail));
                OnPropertyChanged(nameof(SelectedTraceJson));
            }
        }
    }

    public string SelectedTraceDetail => SelectedTrace is null
        ? "选择一条运行记录查看详情"
        : $"{SelectedTrace.TimeText}  {SelectedTrace.Title}{Environment.NewLine}{Environment.NewLine}{(SelectedTrace.HasDetail ? SelectedTrace.Detail : "这条记录没有附加说明")}";

    public string SelectedTraceJson => SelectedTrace?.RawJson ?? "选择一条运行记录查看原始数据";

    public int OverlayX
    {
        get => _overlayX;
        set
        {
            var normalized = Math.Max(0, value);
            if (SetProperty(ref _overlayX, normalized))
            {
                _service.Session.OverlayX = normalized;
                _service.PersistSessionBestEffort();
            }
        }
    }

    public int OverlayY
    {
        get => _overlayY;
        set
        {
            var normalized = Math.Max(0, value);
            if (SetProperty(ref _overlayY, normalized))
            {
                _service.Session.OverlayY = normalized;
                _service.PersistSessionBestEffort();
            }
        }
    }

    public int OverlayDurationMs
    {
        get => _overlayDurationMs;
        set
        {
            var normalized = Math.Clamp(value, 200, 6000);
            if (SetProperty(ref _overlayDurationMs, normalized))
            {
                _service.Session.OverlayDurationMs = normalized;
                _service.PersistSessionBestEffort();
            }
        }
    }

    public int OverlayRadius
    {
        get => _overlayRadius;
        set
        {
            var normalized = Math.Clamp(value, 8, 120);
            if (SetProperty(ref _overlayRadius, normalized))
            {
                _service.Session.OverlayRadius = normalized;
                _service.PersistSessionBestEffort();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyRuntimeProperties();
                NotifyCommandStates();
            }
        }
    }

    public string ArkApiKeyInput
    {
        get => _arkApiKeyInput;
        set => SetProperty(ref _arkApiKeyInput, value ?? "");
    }

    public string AuthKeyInput
    {
        get => _authKeyInput;
        set => SetProperty(ref _authKeyInput, value ?? "");
    }

    public string AuthApiKeyInput
    {
        get => _authApiKeyInput;
        set => SetProperty(ref _authApiKeyInput, value ?? "");
    }

    public string PlannerApiBaseUrl
    {
        get => _plannerApiBaseUrl;
        set => SetProperty(ref _plannerApiBaseUrl, value ?? "");
    }

    public string SettingsMessage
    {
        get => _settingsMessage;
        private set => SetProperty(ref _settingsMessage, value);
    }

    public bool HasSettingsError
    {
        get => _hasSettingsError;
        private set => SetProperty(ref _hasSettingsError, value);
    }

    public bool IsTaskRunning
    {
        get => _isTaskRunning;
        private set
        {
            if (SetProperty(ref _isTaskRunning, value))
            {
                OnPropertyChanged(nameof(IsTaskStopped));
                NotifyRuntimeProperties();
                NotifyCommandStates();
            }
        }
    }

    public bool IsTaskStopped => !IsTaskRunning;

    public bool HasActionError
    {
        get => _hasActionError;
        private set => SetProperty(ref _hasActionError, value);
    }

    public string ActionMessage
    {
        get => _actionMessage;
        private set => SetProperty(ref _actionMessage, value);
    }

    public string TaskDurationText
    {
        get => _taskDurationText;
        private set => SetProperty(ref _taskDurationText, value);
    }

    public int ScreenshotCount
    {
        get => _screenshotCount;
        private set => SetProperty(ref _screenshotCount, value);
    }

    public int ActionCount
    {
        get => _actionCount;
        private set => SetProperty(ref _actionCount, value);
    }

    public int GroundingCount
    {
        get => _groundingCount;
        private set => SetProperty(ref _groundingCount, value);
    }

    public Bitmap? LatestScreenshot
    {
        get => _latestScreenshot;
        private set
        {
            var previous = _latestScreenshot;
            if (SetProperty(ref _latestScreenshot, value))
            {
                OnPropertyChanged(nameof(HasLatestScreenshot));
                previous?.Dispose();
                NotifyCommandStates();
            }
        }
    }

    public void Dispose()
    {
        _taskCancellation?.Cancel();
        _taskCancellation?.Dispose();
        LatestScreenshot = null;
        if (_ownsService)
        {
            _service.Dispose();
        }
    }
}
