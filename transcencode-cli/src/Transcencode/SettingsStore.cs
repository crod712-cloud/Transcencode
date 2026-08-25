using System.Text.Json;

namespace Transcencode;

internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    internal string SettingsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Transcencode");

    internal string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    internal AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(SettingsPath);
            AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (settings is null)
            {
                return new AppSettings();
            }

            settings.InterfaceScale = Math.Clamp(settings.InterfaceScale, 1.0, 2.0);
            settings.ManualQuality = Math.Clamp(settings.ManualQuality, 0, 51);
            return settings;
        }
        catch (Exception exception)
        {
            CrashReporter.Write("settings-load", exception);
            return new AppSettings();
        }
    }

    internal void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            string temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, SettingsPath, true);
        }
        catch (Exception exception)
        {
            CrashReporter.Write("settings-save", exception);
        }
    }
}
