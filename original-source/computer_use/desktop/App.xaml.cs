using System.Windows;
using DoubaoComputerUse.Desktop.Services;
using DoubaoComputerUse.Desktop.ViewModels;

namespace DoubaoComputerUse.Desktop;

public partial class App : Application
{
    private LocalRuntimeService? _runtimeService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _runtimeService = new LocalRuntimeService();
        var viewModel = new MainViewModel(_runtimeService);
        if (e.Args.Contains("--no-auto-start", StringComparer.OrdinalIgnoreCase))
        {
            viewModel.AutoStartEnabled = false;
        }

        var window = new MainWindow(viewModel);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _runtimeService?.Dispose();
        base.OnExit(e);
    }
}
