using Avalonia.Controls;
using DoubaoAgent.Surface.Services;
using DoubaoAgent.Surface.ViewModels;
using DoubaoAgent.Surface.Views;
using MyPowerTools.AvaloniaSdk;

namespace DoubaoAgent.Surface;

/// <summary>
/// Dotnet-surface factory for the Doubao Computer Use agent tool. Loaded by the Shell's
/// DotnetSurfaceLoader from this assembly via the route's <c>assembly</c>+<c>type</c> manifest
/// fields. Builds the DoubaoAgentViewModel from the shared <see cref="DoubaoAgentToolService"/>
/// snapshot, mirroring the Shell controller's load path but operating independently through
/// <see cref="MptAvaloniaSurfaceContext"/>. The ViewModel refreshes service/model/runtime state in
/// the background after construction, so the surface returns immediately.
/// </summary>
public sealed class DoubaoAgentSurfaceFactory : IMptAvaloniaSurfaceFactory
{
    public Control CreateSurface(MptAvaloniaSurfaceContext context)
    {
        var tools = new DoubaoAgentToolService();
        var snapshot = tools.CurrentSnapshot;
        var viewModel = new DoubaoAgentViewModel(snapshot, tools);

        Info(context, "豆包 Computer Use 已打开，状态正在后台更新。");
        return new DoubaoAgentView { DataContext = viewModel };
    }

    private static void Info(MptAvaloniaSurfaceContext context, string message)
    {
        context.Log(new MptSurfaceLogEntry("info", message, DateTimeOffset.Now));
    }
}
