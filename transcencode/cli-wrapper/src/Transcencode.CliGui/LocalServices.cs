using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Transcencode.CliGui;

public static class AppPaths
{
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Transcencode");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}

public static class CrashReporter
{
    private static readonly object Sync = new();

    public static string Write(string category, Exception exception)
    {
        try
        {
            lock (Sync)
            {
                AppPaths.EnsureDirectories();
                string path = Path.Combine(
                    AppPaths.LogDirectory,
                    $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-pid{Environment.ProcessId}.log");

                var report = new StringBuilder();
                report.AppendLine("Transcencode diagnostic report");
                report.AppendLine("Analyze. Encode. Verify.");
                report.AppendLine();
                report.AppendLine("UTC: " + DateTime.UtcNow.ToString("O"));
                report.AppendLine("Category: " + category);
                report.AppendLine("Version: " + (Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown"));
                report.AppendLine("Process: " + Environment.ProcessPath);
                report.AppendLine("Base directory: " + AppContext.BaseDirectory);
                report.AppendLine("Current directory: " + Environment.CurrentDirectory);
                report.AppendLine("Runtime: " + RuntimeInformation.FrameworkDescription);
                report.AppendLine("OS: " + RuntimeInformation.OSDescription);
                report.AppendLine("64-bit process: " + Environment.Is64BitProcess);
                report.AppendLine("Working set: " + Process.GetCurrentProcess().WorkingSet64);
                report.AppendLine();
                report.AppendLine(exception.ToString());

                File.WriteAllText(path, report.ToString(), new UTF8Encoding(true));
                return path;
            }
        }
        catch
        {
            return "Unable to write a diagnostic log.";
        }
    }
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public AppSettings Load()
    {
        try
        {
            AppPaths.EnsureDirectories();
            if (!File.Exists(AppPaths.SettingsPath)) return new AppSettings();
            string json = File.ReadAllText(AppPaths.SettingsPath, Encoding.UTF8);
            AppSettings settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            settings.InterfaceScale = Math.Clamp(settings.InterfaceScale, 1.0, 2.0);
            settings.CustomQuality = Math.Clamp(settings.CustomQuality, 12, 30);
            return settings;
        }
        catch (Exception ex)
        {
            CrashReporter.Write("Settings load failed; defaults were used", ex);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            AppPaths.EnsureDirectories();
            string temp = AppPaths.SettingsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions), new UTF8Encoding(true));
            File.Move(temp, AppPaths.SettingsPath, true);
        }
        catch (Exception ex)
        {
            CrashReporter.Write("Settings save failed", ex);
        }
    }
}
