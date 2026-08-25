// --------------------------------------------------------------------------------------------------------------------
// Transcencode native crash reporting.
// Writes local diagnostic text only; no telemetry or network transmission is performed.
// Licensed under GPL-2.0-or-later.
// --------------------------------------------------------------------------------------------------------------------

namespace HandBrakeWPF.Services.Transcencode
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Text;
    using System.Threading.Tasks;
    using System.Windows.Threading;

    internal static class TranscencodeCrashReporter
    {
        private static readonly object FileLock = new object();
        private static bool installed;

        [ModuleInitializer]
        internal static void Initialize()
        {
            if (installed)
            {
                return;
            }

            installed = true;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            Dispatcher.CurrentDispatcher.UnhandledException += Dispatcher_UnhandledException;
        }

        internal static string RecordHandledError(string category, object error)
        {
            Exception exception = error as Exception
                                  ?? new Exception(error?.ToString() ?? "No exception object was supplied.");
            return WriteCrashReport(category, exception, false, "handled-error");
        }

        internal static string GetDiagnosticDirectory()
        {
            string overrideDirectory = Environment.GetEnvironmentVariable("TRANSCENCODE_DIAGNOSTIC_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDirectory))
            {
                return overrideDirectory;
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Transcencode",
                "logs");
        }

        private static void Dispatcher_UnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            WriteCrashReport("WPF dispatcher unhandled exception", e.Exception, false, "crash");
            // Do not hide application failures. Existing HandBrake handling and Windows crash behavior still apply.
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception exception = e.ExceptionObject as Exception
                                  ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown unhandled exception object.");
            WriteCrashReport("AppDomain unhandled exception", exception, e.IsTerminating, "crash");
        }

        private static void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            WriteCrashReport("Unobserved task exception", e.Exception, false, "crash");
        }

        private static string WriteCrashReport(string category, Exception exception, bool terminating, string filePrefix)
        {
            try
            {
                lock (FileLock)
                {
                    string directory = GetDiagnosticDirectory();
                    Directory.CreateDirectory(directory);

                    string path = Path.Combine(
                        directory,
                        string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "{0}-{1:yyyyMMdd-HHmmss-fff}-pid{2}.log",
                            filePrefix,
                            DateTime.UtcNow,
                            Environment.ProcessId));

                    StringBuilder report = new StringBuilder();
                    report.AppendLine("Transcencode native WPF diagnostic report");
                    report.AppendLine("Analyze. Encode. Verify.");
                    report.AppendLine();
                    report.AppendLine("UTC time: " + DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                    report.AppendLine("Category: " + category);
                    report.AppendLine("Terminating: " + terminating);
                    report.AppendLine("Process ID: " + Environment.ProcessId);
                    report.AppendLine("Process path: " + Environment.ProcessPath);
                    report.AppendLine("Base directory: " + AppDomain.CurrentDomain.BaseDirectory);
                    report.AppendLine("Current directory: " + Environment.CurrentDirectory);
                    report.AppendLine("Application version: " + (Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown"));
                    report.AppendLine("Runtime: " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
                    report.AppendLine("OS: " + System.Runtime.InteropServices.RuntimeInformation.OSDescription);
                    report.AppendLine("64-bit process: " + Environment.Is64BitProcess);
                    report.AppendLine("Working set: " + Process.GetCurrentProcess().WorkingSet64);
                    report.AppendLine("APPDATA: " + Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
                    report.AppendLine("LOCALAPPDATA: " + Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
                    report.AppendLine();
                    report.AppendLine(exception.ToString());

                    File.WriteAllText(path, report.ToString(), new UTF8Encoding(true));
                    return path;
                }
            }
            catch
            {
                // A crash reporter must never replace the original failure with a secondary exception.
                return string.Empty;
            }
        }
    }
}
