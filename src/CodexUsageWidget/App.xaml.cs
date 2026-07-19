using System.Threading;
using System.Windows;

namespace CodexUsageWidget;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        const string mutexName = @"Local\OpenAI.CodexUsageWidget";
        _singleInstanceMutex = new Mutex(initiallyOwned: true, mutexName, out var isFirstInstance);

        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"Codex 额度悬浮窗遇到错误：\n\n{args.Exception.Message}",
                "Codex 额度",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            args.Handled = true;
        };

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The mutex may already have been released during abnormal shutdown.
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
