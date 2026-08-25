using System.Collections.ObjectModel;

namespace Transcencode;

public sealed class EncoderChoice
{
    public required string DisplayName { get; init; }
    public required string CliName { get; init; }
    public bool IsNvidia { get; init; }
    public bool IsTenBit { get; init; }
    public override string ToString() => DisplayName;
}

public sealed class QualityProfile
{
    public required string Name { get; init; }
    public required string Explanation { get; init; }
    public double NvidiaValue { get; init; }
    public double SoftwareValue { get; init; }
    public override string ToString() => Name;
}

public sealed class AudioTrackInfo
{
    public int Track { get; init; }
    public string Language { get; init; } = "Unknown";
    public string LanguageCode { get; init; } = string.Empty;
    public string Codec { get; init; } = string.Empty;
    public string Channels { get; init; } = string.Empty;
    public long BitRate { get; init; }
    public int SampleRate { get; init; }
    public string Name { get; init; } = string.Empty;
}

public sealed class SubtitleTrackInfo
{
    public int Track { get; init; }
    public string Language { get; init; } = "Unknown";
    public string LanguageCode { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Capabilities { get; init; } = string.Empty;
}

public sealed class SourceInfo
{
    public string Path { get; init; } = string.Empty;
    public int TitleIndex { get; init; }
    public TimeSpan Duration { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string VideoCodec { get; init; } = string.Empty;
    public string FrameRate { get; init; } = string.Empty;
    public string HdrSummary { get; init; } = "Standard dynamic range or undetermined";
    public string CropSummary { get; init; } = "Not reported";
    public ObservableCollection<AudioTrackInfo> AudioTracks { get; init; } = [];
    public ObservableCollection<SubtitleTrackInfo> SubtitleTracks { get; init; } = [];
}

public sealed class ProgressInfo
{
    public double Percent { get; init; }
    public double? CurrentFps { get; init; }
    public double? AverageFps { get; init; }
    public TimeSpan? Eta { get; init; }
    public string RawLine { get; init; } = string.Empty;
}

public sealed class ProcessResult
{
    public int ExitCode { get; init; }
    public string CombinedOutput { get; init; } = string.Empty;
    public ProgressInfo? LastProgress { get; init; }
    public bool Success => ExitCode == 0;
}

public enum CropChoice
{
    PreserveOriginal,
    SafeAutomatic,
    Automatic,
    Custom
}

public enum ScaleChoice
{
    SameAsSource,
    FullHd1080,
    QuadHd1440,
    UltraHd2160,
    Custom
}

public sealed class EncodeOptions
{
    public required string InputPath { get; init; }
    public required string OutputPath { get; init; }
    public required EncoderChoice Encoder { get; init; }
    public double Quality { get; init; }
    public string EncoderPreset { get; init; } = string.Empty;
    public CropChoice Crop { get; init; }
    public int CropTop { get; init; }
    public int CropBottom { get; init; }
    public int CropLeft { get; init; }
    public int CropRight { get; init; }
    public ScaleChoice Scale { get; init; }
    public int CustomWidth { get; init; }
    public int CustomHeight { get; init; }
    public bool PreserveAllAudio { get; init; } = true;
    public bool PreserveAllSubtitles { get; init; } = true;
    public bool ChapterMarkers { get; init; } = true;
    public bool WebOptimizeMp4 { get; init; }
    public int? StartSeconds { get; init; }
    public int? StopAfterSeconds { get; init; }
    public bool IsAnalysisSample { get; init; }
}

public sealed class VerificationResult
{
    public bool Passed { get; init; }
    public string Summary { get; init; } = string.Empty;
    public IReadOnlyList<VerificationCheck> Checks { get; init; } = [];
}

public sealed class VerificationCheck
{
    public string Check { get; init; } = string.Empty;
    public string Result { get; init; } = string.Empty;
    public string Details { get; init; } = string.Empty;
}

public sealed class AnalysisSampleResult
{
    public int Sample { get; init; }
    public TimeSpan Start { get; init; }
    public double Megabytes { get; init; }
    public double MegabitsPerSecond { get; init; }
    public double? AverageFps { get; init; }
    public string RelativeDifficulty { get; set; } = string.Empty;
}

public sealed class AppSettings
{
    public double InterfaceScale { get; set; } = 1.0;
    public bool ShowConsoleWhenEncoding { get; set; } = true;
    public bool VerifyAfterEncoding { get; set; } = true;
    public bool OpenOutputFolderWhenFinished { get; set; }
    public string LastSourceFolder { get; set; } = string.Empty;
    public string LastOutputFolder { get; set; } = string.Empty;
    public string EncoderCliName { get; set; } = "nvenc_h265_10bit";
    public string QualityProfile { get; set; } = "Visually transparent";
    public double ManualQuality { get; set; } = 16;
}
