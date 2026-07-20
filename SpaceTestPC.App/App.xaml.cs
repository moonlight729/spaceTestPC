using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Data.Sqlite;

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
            if (ShowStartupEnvironmentWarning())
            {
                Shutdown(-2);
                return;
            }
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

    private static bool ShowStartupEnvironmentWarning()
    {
        try
        {
            var issues = new List<string>();
            var baseDir = AppContext.BaseDirectory;
            var appSettingsPath = Path.Combine(baseDir, "appsettings.json");
            var databasePath = Path.Combine(baseDir, "data", "box-test-records.db");
            var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
            var sameNameProcesses = System.Diagnostics.Process
                .GetProcessesByName(currentProcess.ProcessName)
                .Count(process => process.Id != currentProcess.Id);

            if (!File.Exists(appSettingsPath))
            {
                issues.Add($"缺少配置文件：{appSettingsPath}");
            }

            if (sameNameProcesses > 0)
            {
                issues.Add($"检测到另外 {sameNameProcesses} 个上位机进程正在运行，可能导致数据库锁定或界面异常。");
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
                var builder = new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false,
                    DefaultTimeout = 2
                };
                using var connection = new SqliteConnection(builder.ToString());
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=2000;";
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                issues.Add($"测试结果数据库不可用：{databasePath}。{ex.Message}");
            }

            if (issues.Count == 0)
            {
                return false;
            }

            var message = new StringBuilder()
                .AppendLine("检测到当前运行环境可能有问题：")
                .AppendLine()
                .AppendJoin(Environment.NewLine, issues.Select(issue => $"- {issue}"))
                .AppendLine()
                .AppendLine()
                .Append("建议先处理以上问题，再继续测试。")
                .ToString();

            WriteStartupError("startup-check", new InvalidOperationException(message));
            MessageBox.Show(
                message,
                "SpaceTestPC.App 启动检查",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return true;
        }
        catch
        {
            return false;
        }
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
