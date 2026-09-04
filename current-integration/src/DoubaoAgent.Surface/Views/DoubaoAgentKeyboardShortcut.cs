using Avalonia.Input;

namespace DoubaoAgent.Surface.Views;

public enum DoubaoAgentKeyboardAction
{
    None,
    RunTask,
    CancelTask
}

public static class DoubaoAgentKeyboardShortcut
{
    public static DoubaoAgentKeyboardAction Resolve(Key key, KeyModifiers modifiers)
    {
        if (key == Key.Enter &&
            (modifiers == KeyModifiers.Control || modifiers == KeyModifiers.Meta))
        {
            return DoubaoAgentKeyboardAction.RunTask;
        }

        if (key == Key.Escape && modifiers == KeyModifiers.None)
        {
            return DoubaoAgentKeyboardAction.CancelTask;
        }

        return DoubaoAgentKeyboardAction.None;
    }
}
