using Avalonia;
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
