using MyPowerTools.AvaloniaSdk;
using DoubaoAgent.Surface.ViewModels;

namespace DoubaoAgent.Surface.Views;

public partial class DoubaoAgentView : IMptShortcutCommandSource
{
    public string ShortcutToolId => "doubao-agent";
    public string ShortcutContext => DataContext is DoubaoAgentViewModel vm ? "overview" : "";

    public IReadOnlyList<MptShortcutCommand> GetShortcutCommands()
    {
        if (DataContext is not DoubaoAgentViewModel vm) return [];
        return
        [
            MptShortcutCommand.FromCommand("doubao-agent.ui.refresh", vm.RefreshCommand),
            MptShortcutCommand.FromCommand("doubao-agent.ui.start-runtime", vm.StartRuntimeCommand),
            MptShortcutCommand.FromCommand("doubao-agent.ui.stop-runtime", vm.StopRuntimeCommand),
            MptShortcutCommand.FromCommand("doubao-agent.ui.restart-runtime", vm.RestartRuntimeCommand),
            MptShortcutCommand.FromCommand("doubao-agent.ui.run-task", vm.RunTaskCommand),
            MptShortcutCommand.FromCommand("doubao-agent.ui.stop-task", vm.StopTaskCommand),
            MptShortcutCommand.FromCommand("doubao-agent.ui.clear-trace", vm.ClearTraceCommand),
            MptShortcutCommand.FromCommand("doubao-agent.ui.show-overlay", vm.ShowOverlayCommand),
            MptShortcutCommand.FromCommand("doubao-agent.ui.hide-overlay", vm.HideOverlayCommand),
            MptShortcutCommand.FromCommand("doubao-agent.ui.overlay-self-test", vm.OverlaySelfTestCommand),
            MptShortcutCommand.FromCommand("doubao-agent.ui.save-configuration", vm.SaveConfigurationCommand),
            MptShortcutCommand.FromCommand("doubao-agent.ui.test-configuration", vm.TestConfigurationCommand),
            new("doubao-agent.ui.copy-report", CopyReportAsync, () => vm.HasRunReport),
            new("doubao-agent.ui.export-report", ExportReportAsync, () => vm.HasRunReport),
        ];
    }
}
