using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace Transcencode;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        CrashReporter.Install(this);
        base.OnStartup(e);
    }

    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        try
        {
            string? selfTestReport = GetArgumentValue(e.Args, "--self-test-report=");
            string? selfTestSource = GetArgumentValue(e.Args, "--self-test-source=");

            if (!string.IsNullOrWhiteSpace(selfTestReport))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                int exitCode = await SelfTestRunner.RunAsync(selfTestReport, selfTestSource);
                Shutdown(exitCode);
                return;
            }

            MainWindow window = new();
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            string logPath = CrashReporter.Write("startup", exception);
            MessageBox.Show(
                "Transcencode could not start.\n\n" +
                exception.Message +
                (string.IsNullOrWhiteSpace(logPath) ? string.Empty : "\n\nDiagnostic log:\n" + logPath),
                "Transcencode startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static string? GetArgumentValue(IEnumerable<string> args, string prefix)
    {
        string? value = args.FirstOrDefault(
            argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return value?[prefix.Length..].Trim().Trim('"');
    }
}

internal static class CrashReporter
{
    private static int installed;

    internal static void Install(Application application)
    {
        if (Interlocked.Exchange(ref installed, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Exception exception = args.ExceptionObject as Exception
                                  ?? new Exception(args.ExceptionObject?.ToString() ?? "Unknown exception");
            Write("appdomain-unhandled", exception);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Write("unobserved-task", args.Exception);
        };

        application.DispatcherUnhandledException += (_, args) =>
        {
            string path = Write("dispatcher-unhandled", args.Exception);
            MessageBox.Show(
                "Transcencode encountered an error.\n\n" + args.Exception.Message +
                (string.IsNullOrWhiteSpace(path) ? string.Empty : "\n\nDiagnostic log:\n" + path),
                "Transcencode error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        };
    }

    internal static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Transcencode",
        "logs");

    internal static string Write(string category, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            string path = Path.Combine(
                LogDirectory,
                $"{category}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-pid{Environment.ProcessId}.log");

            StringBuilder report = new();
            report.AppendLine("Transcencode diagnostic report");
            report.AppendLine("Analyze. Encode. Verify.");
            report.AppendLine();
            report.AppendLine($"UTC: {DateTime.UtcNow:O}");
            report.AppendLine($"Category: {category}");
            report.AppendLine($"Version: {typeof(App).Assembly.GetName().Version}");
            report.AppendLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
            report.AppendLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
            report.AppendLine($"Process path: {Environment.ProcessPath}");
            report.AppendLine($"Base directory: {AppContext.BaseDirectory}");
            report.AppendLine($"Current directory: {Environment.CurrentDirectory}");
            report.AppendLine();
            report.AppendLine(exception.ToString());
            File.WriteAllText(path, report.ToString(), new UTF8Encoding(true));
            return path;
        }
        catch
        {
            return string.Empty;
        }
    }
}
