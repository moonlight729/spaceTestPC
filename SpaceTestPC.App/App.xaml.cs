using System.IO;
using System.Windows;

namespace SpaceTestPC.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        RegisterGlobalExceptionHandlers();
        base.OnStartup(e);

        try
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            window.Activate();
            window.Focus();
        }
        catch (Exception ex)
        {
            WriteStartupError("startup", ex);
            MessageBox.Show(
                $"Application startup failed.{Environment.NewLine}{ex}",
                "SpaceTestPC.App",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            WriteStartupError("dispatcher", args.Exception);
            MessageBox.Show(
                $"Unhandled UI exception.{Environment.NewLine}{args.Exception}",
                "SpaceTestPC.App",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = false;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                WriteStartupError("appdomain", ex);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteStartupError("task", args.Exception);
        };
    }

    private static void WriteStartupError(string phase, Exception ex)
    {
        try
        {
            var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "startup-error.log");
            File.AppendAllText(
                logPath,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{phase}] {ex}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
