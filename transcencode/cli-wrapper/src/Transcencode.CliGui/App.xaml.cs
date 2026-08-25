using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace Transcencode.CliGui;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);

        if (e.Args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunSelfTestAsync(e.Args);
            return;
        }

        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            ShowFatalError("Transcencode could not start.", ex);
            Shutdown(1);
        }
    }

    private async Task RunSelfTestAsync(string[] args)
    {
        string? source = GetArgument(args, "--source");
        string? report = GetArgument(args, "--report");

        try
        {
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            {
                throw new ArgumentException("--self-test requires --source <existing media file>.");
            }

            report ??= Path.Combine(Path.GetTempPath(), "Transcencode-self-test.txt");
            int exitCode = await SelfTestRunner.RunAsync(source, report);
            Shutdown(exitCode);
        }
        catch (Exception ex)
        {
            string path = CrashReporter.Write("Self-test startup failure", ex);
            if (!string.IsNullOrWhiteSpace(report))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
                await File.WriteAllTextAsync(report, "TRANSCENCODE_SELF_TEST_FAILED\r\n" + ex + "\r\nCrash log: " + path, Encoding.UTF8);
            }

            Shutdown(1);
        }
    }

    private static string? GetArgument(IReadOnlyList<string> args, string name)
    {
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1].Trim('"');
            }
        }

        return null;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowFatalError("Transcencode encountered an unexpected error.", e.Exception);
        e.Handled = true;
        Shutdown(1);
    }

    private static void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown fatal error.");
        CrashReporter.Write("AppDomain unhandled exception", ex);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        CrashReporter.Write("Unobserved task exception", e.Exception);
        e.SetObserved();
    }

    private static void ShowFatalError(string heading, Exception ex)
    {
        string path = CrashReporter.Write(heading, ex);
        string details = ex.ToString();
        if (details.Length > 10000)
        {
            details = details[..10000] + "\r\n[Details truncated here; the full exception is in the log.]";
        }

        MessageBox.Show(
            heading + "\r\n\r\n" + details + "\r\n\r\nDiagnostic log:\r\n" + path,
            "Transcencode error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
