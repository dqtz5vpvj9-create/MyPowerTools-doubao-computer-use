using Avalonia.Input;
using DoubaoAgent.Surface.Views;

namespace MyPowerTools.Tests;

public sealed class DoubaoAgentKeyboardShortcutTests
{
    [Theory]
    [InlineData(KeyModifiers.Control)]
    [InlineData(KeyModifiers.Meta)]
    public void Run_shortcut_supports_windows_linux_and_macos(KeyModifiers modifiers)
    {
        Assert.Equal(
            DoubaoAgentKeyboardAction.RunTask,
            DoubaoAgentKeyboardShortcut.Resolve(Key.Enter, modifiers));
    }

    [Fact]
    public void Escape_maps_to_task_cancellation()
    {
        Assert.Equal(
            DoubaoAgentKeyboardAction.CancelTask,
            DoubaoAgentKeyboardShortcut.Resolve(Key.Escape, KeyModifiers.None));
    }

    [Fact]
    public void Plain_enter_remains_available_for_multiline_instructions()
    {
        Assert.Equal(
            DoubaoAgentKeyboardAction.None,
            DoubaoAgentKeyboardShortcut.Resolve(Key.Enter, KeyModifiers.None));
    }
}
