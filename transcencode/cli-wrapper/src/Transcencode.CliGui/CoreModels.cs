using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Transcencode.CliGui;

public sealed class OptionItem
{
    public OptionItem(string id, string label, string? description = null)
    {
        Id = id;
        Label = label;
        Description = description ?? string.Empty;
    }

    public string Id { get; }
    public string Label { get; }
    public string Description { get; }
    public override string ToString() => Label;
}

public sealed class TrackItem : INotifyPropertyChanged
{
    private bool selected;

    public int Number { get; init; }
    public string Language { get; init; } = "Unknown";
    public string Code { get; init; } = "und";
    public string Codec { get; init; } = "Unknown";
    public string Details { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsSubtitle { get; init; }

    public bool Selected
    {
        get => selected;
        set
        {
            if (selected == value) return;
            selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ScanResult
{
    public string SourcePath { get; init; } = string.Empty;
    public string TitleName { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public TimeSpan Duration { get; init; }
    public string VideoCodec { get; init; } = "Unknown";
    public double FrameRate { get; init; }
    public int CropTop { get; init; }
    public int CropBottom { get; init; }
    public int CropLeft { get; init; }
    public int CropRight { get; init; }
    public bool IsHdr { get; init; }
    public bool IsHdr10Plus { get; init; }
    public bool IsDolbyVision { get; init; }
    public List<TrackItem> AudioTracks { get; init; } = [];
    public List<TrackItem> SubtitleTracks { get; init; } = [];
    public string RawScanText { get; init; } = string.Empty;

    public string HdrLabel
    {
        get
        {
            var parts = new List<string>();
            if (IsDolbyVision) parts.Add("Dolby Vision");
            if (IsHdr10Plus) parts.Add("HDR10+");
            if (IsHdr && parts.Count == 0) parts.Add("HDR");
            return parts.Count == 0 ? "SDR / not reported as HDR" : string.Join(" + ", parts);
        }
    }

    public string Summary =>
        $"{Width} × {Height} | {Duration:hh\\:mm\\:ss} | {VideoCodec} | {FrameRate:0.###} fps | {HdrLabel}\n" +
        $"Detected crop: top {CropTop}, bottom {CropBottom}, left {CropLeft}, right {CropRight}\n" +
        $"Audio tracks: {AudioTracks.Count} | Subtitle tracks: {SubtitleTracks.Count}";
}

public sealed class EncodePlan
{
    public string SourcePath { get; init; } = string.Empty;
    public string OutputPath { get; init; } = string.Empty;
    public string EncoderId { get; init; } = "nvenc_h265_10bit";
    public string EncoderPreset { get; init; } = "slowest";
    public double Quality { get; init; } = 18;
    public bool EnableNvdec { get; init; }
    public string CropMode { get; init; } = "none";
    public int CropTop { get; init; }
    public int CropBottom { get; init; }
    public int CropLeft { get; init; }
    public int CropRight { get; init; }
    public string ScaleMode { get; init; } = "source";
    public int TargetWidth { get; init; }
    public int TargetHeight { get; init; }
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }
    public IReadOnlyList<int> AudioTracks { get; init; } = [];
    public IReadOnlyList<int> SubtitleTracks { get; init; } = [];
}

public sealed class EngineProgress
{
    public string Phase { get; init; } = "Working";
    public double Percent { get; init; }
    public double CurrentFps { get; init; }
    public double AverageFps { get; init; }
    public TimeSpan? Eta { get; init; }
    public string RawLine { get; init; } = string.Empty;
}

public sealed class ProcessRunResult
{
    public int ExitCode { get; init; }
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;
    public string CombinedOutput { get; init; } = string.Empty;
    public TimeSpan Elapsed { get; init; }
    public string LogPath { get; init; } = string.Empty;
}

public sealed class AnalysisSample
{
    public int Number { get; init; }
    public TimeSpan Timestamp { get; init; }
    public double Brightness { get; init; }
    public double DarkPixelPercent { get; init; }
    public double Contrast { get; init; }
    public double Detail { get; init; }
    public double Motion { get; init; }
    public double SceneChange { get; init; }
    public double Difficulty { get; init; }

    public string TimeText => Timestamp.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    public string BrightnessText => Brightness.ToString("0.0", CultureInfo.InvariantCulture);
    public string DarkText => DarkPixelPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%";
    public string ContrastText => Contrast.ToString("0.0", CultureInfo.InvariantCulture);
    public string DetailText => Detail.ToString("0.0", CultureInfo.InvariantCulture);
    public string MotionText => Motion.ToString("0.0", CultureInfo.InvariantCulture);
    public string DifficultyText => Difficulty.ToString("0.0", CultureInfo.InvariantCulture);
}

public sealed class AnalysisResult
{
    public IReadOnlyList<AnalysisSample> Samples { get; init; } = [];
    public double AverageBrightness { get; init; }
    public double AverageDarkPixels { get; init; }
    public double AverageContrast { get; init; }
    public double AverageDetail { get; init; }
    public double AverageMotion { get; init; }
    public double AverageDifficulty { get; init; }
    public double RecommendedQuality { get; init; }
    public string Explanation { get; init; } = string.Empty;
}

public sealed class VerificationItem
{
    public string Check { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Details { get; init; } = string.Empty;
}

public sealed class VerificationResult
{
    public bool Passed { get; init; }
    public double AverageSimilarity { get; init; }
    public double MinimumSimilarity { get; init; }
    public IReadOnlyList<VerificationItem> Items { get; init; } = [];
}

public sealed class AppSettings
{
    public double InterfaceScale { get; set; } = 1.0;
    public string EncoderId { get; set; } = string.Empty;
    public string QualityTarget { get; set; } = "high-fidelity";
    public double CustomQuality { get; set; } = 18;
    public string CropMode { get; set; } = "none";
    public bool EnableNvdec { get; set; }
    public string OutputExtension { get; set; } = ".mkv";
}

public static class QualityMapping
{
    public static double ForTarget(string encoderId, string target)
    {
        bool hardware = encoderId.StartsWith("nvenc_", StringComparison.OrdinalIgnoreCase);
        return target switch
        {
            "high-fidelity" => hardware ? 16 : 18,
            "high" => hardware ? 18 : 20,
            "balanced" => hardware ? 21 : 22,
            "smaller" => hardware ? 24 : 25,
            _ => hardware ? 18 : 20
        };
    }

    public static double SliderToQuality(double sliderValue)
    {
        double clamped = Math.Clamp(sliderValue, 0, 100);
        return Math.Round(30.0 - (clamped / 100.0 * 18.0), 1);
    }

    public static double QualityToSlider(double quality)
    {
        double clamped = Math.Clamp(quality, 12, 30);
        return Math.Round((30.0 - clamped) / 18.0 * 100.0, 1);
    }
}

public static class PropertyChange
{
    public static bool Set<T>(ref T field, T value, PropertyChangedEventHandler? handler, object owner, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        handler?.Invoke(owner, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
