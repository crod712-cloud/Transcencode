using System.Text;
using Transcencode;

List<string> failures = [];

void Check(bool condition, string message)
{
    if (!condition)
    {
        failures.Add(message);
    }
}

EncoderChoice nvenc = new()
{
    DisplayName = "H.265 10-bit (NVIDIA NVENC)",
    CliName = "nvenc_h265_10bit",
    IsNvidia = true,
    IsTenBit = true
};

EncodeOptions options = new()
{
    InputPath = @"C:\Input Files\movie.mkv",
    OutputPath = @"C:\Output Files\movie-transcencode.mkv",
    Encoder = nvenc,
    Quality = 16,
    Crop = CropChoice.PreserveOriginal,
    Scale = ScaleChoice.UltraHd2160,
    PreserveAllAudio = true,
    PreserveAllSubtitles = true,
    ChapterMarkers = true
};

IReadOnlyList<string> arguments = HandBrakeCommandBuilder.BuildEncodeArguments(options);
Check(ContainsPair(arguments, "--encoder", "nvenc_h265_10bit"), "NVENC encoder was not emitted.");
Check(ContainsPair(arguments, "--quality", "16"), "Quality value was not emitted.");
Check(ContainsPair(arguments, "--crop", "0:0:0:0"), "Same as source did not emit zero crop.");
Check(ContainsPair(arguments, "--width", "3840") && ContainsPair(arguments, "--height", "2160"), "4K target was not emitted.");
Check(arguments.Contains("--allow-upscaling"), "Upscaling permission was not emitted.");
Check(arguments.Contains("--all-audio"), "All-audio preservation was not emitted.");
Check(arguments.Contains("--all-subtitles"), "All-subtitle preservation was not emitted.");
Check(arguments.Contains("--markers"), "Chapter markers were not emitted.");

string display = HandBrakeCommandBuilder.ToDisplayCommand(@"C:\Program Files\Transcencode\HandBrakeCLI.exe", arguments);
Check(display.StartsWith("\"C:\\Program Files\\Transcencode\\HandBrakeCLI.exe\"", StringComparison.Ordinal), "Display command did not quote the executable path.");
Check(display.Contains("\"C:\\Input Files\\movie.mkv\"", StringComparison.Ordinal), "Display command did not quote the source path.");

ProgressInfo? progress = HandBrakeCliService.TryParseProgress(
    "Encoding: task 1 of 1, 47.25 % (83.12 fps, avg 78.40 fps, ETA 00h01m23s)");
Check(progress is not null, "Progress line was not recognized.");
Check(progress is not null && Math.Abs(progress.Percent - 47.25) < 0.001, "Progress percent was parsed incorrectly.");
Check(progress?.Eta == new TimeSpan(0, 1, 23), "ETA was parsed incorrectly.");

string scanJson = """
[00:00:00] scan: test noise before JSON
{
  "MainFeature": 1,
  "TitleList": [
    {
      "Index": 1,
      "Duration": { "Hours": 0, "Minutes": 1, "Seconds": 12, "Ticks": 6480000 },
      "Geometry": { "Width": 1920, "Height": 1080 },
      "FrameRate": { "Num": 24000, "Den": 1001 },
      "VideoCodec": "H.265 (libavcodec)",
      "Crop": [0, 140, 0, 140],
      "AudioList": [
        {
          "TrackNumber": 1,
          "Language": "English",
          "LanguageCode": "eng",
          "CodecName": "E-AC3",
          "ChannelLayoutName": "5.1 ch",
          "BitRate": 640000,
          "SampleRate": 48000,
          "Name": "Main"
        },
        {
          "TrackNumber": 2,
          "Language": "Spanish",
          "LanguageCode": "spa",
          "CodecName": "AAC",
          "ChannelLayoutName": "stereo",
          "BitRate": 192000,
          "SampleRate": 48000,
          "Name": "Dub"
        }
      ],
      "SubtitleList": [
        { "TrackNumber": 1, "Language": "English", "LanguageCode": "eng", "SourceName": "UTF-8", "Name": "English" },
        { "TrackNumber": 2, "Language": "Spanish", "LanguageCode": "spa", "SourceName": "UTF-8", "Name": "Español" }
      ],
      "Color": { "Transfer": 16 },
      "HDR10Plus": 1
    }
  ]
}
[00:00:01] scan: test noise after JSON
""";

SourceInfo source = SourceScanParser.Parse("test.mkv", scanJson);
Check(source.Width == 1920 && source.Height == 1080, "Source dimensions were parsed incorrectly.");
Check(source.Duration == TimeSpan.FromSeconds(72), "Source duration was parsed incorrectly.");
Check(source.AudioTracks.Count == 2 && source.AudioTracks[1].Language == "Spanish", "Audio languages were not parsed.");
Check(source.SubtitleTracks.Count == 2 && source.SubtitleTracks[1].LanguageCode == "spa", "Subtitle languages were not parsed.");
Check(source.HdrSummary.Contains("HDR10", StringComparison.OrdinalIgnoreCase), "HDR metadata was not surfaced.");

if (failures.Count > 0)
{
    Console.Error.WriteLine("TRANSCENCODE_COMMAND_TESTS_FAILED");
    foreach (string failure in failures)
    {
        Console.Error.WriteLine("- " + failure);
    }

    return 1;
}

Console.WriteLine("TRANSCENCODE_COMMAND_TESTS_PASSED");
return 0;

static bool ContainsPair(IReadOnlyList<string> values, string key, string value)
{
    for (int index = 0; index + 1 < values.Count; index++)
    {
        if (values[index] == key && values[index + 1] == value)
        {
            return true;
        }
    }

    return false;
}
