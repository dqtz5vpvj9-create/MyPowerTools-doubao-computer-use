using System.Text;
using Avalonia;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Controls;
using DoubaoAgent.Surface.ViewModels;

namespace DoubaoAgent.Surface.Views;

public sealed partial class DoubaoAgentView : UserControl
{
    public DoubaoAgentView()
    {
        InitializeComponent();
        SizeChanged += (_, eventArgs) => UpdateResponsiveLayout(eventArgs.NewSize.Width);
        Loaded += (_, _) =>
        {
            UpdateResponsiveLayout(Bounds.Width);
            (DataContext as DoubaoAgentViewModel)?.Activate();
        };
        DetachedFromVisualTree += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    private async void OnCopyReportClick(object? sender, RoutedEventArgs e) => await CopyReportAsync();

    private async Task CopyReportAsync()
    {
        if (DataContext is not DoubaoAgentViewModel vm || !vm.HasRunReport) return;
        var report = vm.CreateRunReport();
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) throw new InvalidOperationException("剪贴板暂不可用");
            await ClipboardExtensions.SetTextAsync(clipboard, report);
            vm.SetRunReportFeedback("任务记录已复制（事件文本最多保留约 419 万字符；超出部分会标注截断）。");
        }
        catch (Exception ex) { vm.SetRunReportFeedback($"复制失败：{ex.Message}"); }
    }

    private async void OnExportReportClick(object? sender, RoutedEventArgs e) => await ExportReportAsync();

    private async Task ExportReportAsync()
    {
        if (DataContext is not DoubaoAgentViewModel vm || !vm.HasRunReport) return;
        var report = vm.CreateRunReport();
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null || !storage.CanSave) throw new InvalidOperationException("文件选择器暂不可用");
            using var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出豆包任务记录",
                SuggestedFileName = $"doubao-task-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                DefaultExtension = "txt",
                FileTypeChoices = [new FilePickerFileType("Text report") { Patterns = ["*.txt"] }]
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek) stream.SetLength(0);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(report);
            await writer.FlushAsync();
            vm.SetRunReportFeedback($"任务记录已导出：{file.Name}");
        }
        catch (Exception ex) { vm.SetRunReportFeedback($"导出失败：{ex.Message}"); }
    }

    private void UpdateResponsiveLayout(double width)
    {
        if (width <= 0)
        {
            return;
        }

        WorkspaceGrid.ColumnDefinitions.Clear();
        WorkspaceGrid.RowDefinitions.Clear();
        if (width < 920)
        {
            WorkspaceGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            WorkspaceGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            WorkspaceGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            WorkspaceGrid.ColumnSpacing = 0;
            WorkspaceGrid.RowSpacing = 16;
            Grid.SetColumn(ControlPanel, 0);
            Grid.SetRow(ControlPanel, 0);
            Grid.SetColumn(ActivityPanel, 0);
            Grid.SetRow(ActivityPanel, 1);
        }
        else
        {
            WorkspaceGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(width >= 1320 ? 410 : 370)));
            WorkspaceGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            WorkspaceGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            WorkspaceGrid.ColumnSpacing = 16;
            WorkspaceGrid.RowSpacing = 0;
            Grid.SetColumn(ControlPanel, 0);
            Grid.SetRow(ControlPanel, 0);
            Grid.SetColumn(ActivityPanel, 1);
            Grid.SetRow(ActivityPanel, 0);
        }

        TraceDetailsGrid.ColumnDefinitions.Clear();
        TraceDetailsGrid.RowDefinitions.Clear();
        if (width < 760)
        {
            TraceDetailsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            TraceDetailsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            TraceDetailsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            TraceDetailsGrid.ColumnSpacing = 0;
            TraceDetailsGrid.RowSpacing = 16;
            Grid.SetColumn(TraceCard, 0);
            Grid.SetRow(TraceCard, 0);
            Grid.SetColumn(DetailsCard, 0);
            Grid.SetRow(DetailsCard, 1);
        }
        else
        {
            TraceDetailsGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1.05, GridUnitType.Star)));
            TraceDetailsGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(0.95, GridUnitType.Star)));
            TraceDetailsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            TraceDetailsGrid.ColumnSpacing = 16;
            TraceDetailsGrid.RowSpacing = 0;
            Grid.SetColumn(TraceCard, 0);
            Grid.SetRow(TraceCard, 0);
            Grid.SetColumn(DetailsCard, 1);
            Grid.SetRow(DetailsCard, 0);
        }
    }
}
