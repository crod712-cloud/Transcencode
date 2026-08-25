using Transcencode.CliGui;

var tests = new List<(string Name, Action Run)>
{
    ("Hidden engine process", TestHiddenProcess),
    ("Progress and ETA parsing", TestProgress),
    ("HandBrake JSON source tracks", TestScanJson),
    ("NVENC high-fidelity quality", TestQuality),
    ("Same as source crop", TestSameAsSource),
    ("Upscaling command", TestUpscaling),
    ("Argument quoting preview", TestCommandPreview)
};

int failures = 0;
foreach ((string name, Action run) in tests)
{
    try
    {
        run();
        Console.WriteLine("PASS: " + name);
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine("FAIL: " + name + " — " + ex.Message);
    }
}

if (failures > 0)
{
    Console.Error.WriteLine($"TRANSCENCODE_CLI_UNIT_TESTS_FAILED: {failures}");
    return 1;
}

Console.WriteLine("TRANSCENCODE_CLI_UNIT_TESTS_PASSED");
return 0;

static void TestHiddenProcess()
{
    var info = HandBrakeCliService.CreateHiddenStartInfo("C:\\Tools\\HandBrakeCLI.exe", ["--version"]);
    Assert(!info.UseShellExecute, "UseShellExecute must be false.");
    Assert(info.CreateNoWindow, "CreateNoWindow must be true.");
    Assert(info.WindowStyle == System.Diagnostics.ProcessWindowStyle.Hidden, "WindowStyle must be Hidden.");
    Assert(info.RedirectStandardOutput && info.RedirectStandardError, "Both streams must be redirected.");
    Assert(info.FileName.EndsWith("HandBrakeCLI.exe", StringComparison.OrdinalIgnoreCase), "The wrapper must launch HandBrakeCLI directly.");
}

static void TestProgress()
{
    bool parsed = HandBrakeCliService.TryParseProgress(
        "Encoding: task 1 of 1, 63.50 % (88.20 fps, avg 84.10 fps, ETA 00h01m09s)",
        out EngineProgress? progress);
    Assert(parsed && progress is not null, "Progress line was not recognized.");
    Assert(Math.Abs(progress!.Percent - 63.5) < 0.01, "Percent was wrong.");
    Assert(Math.Abs(progress.AverageFps - 84.1) < 0.01, "Average FPS was wrong.");
    Assert(progress.Eta == TimeSpan.FromSeconds(69), "ETA was wrong.");
}

static void TestScanJson()
{
    string json = "noise before JSON Title Set: {\"MainFeature\":1,\"TitleList\":[{\"Name\":\"Sample\",\"Geometry\":{\"Width\":1920,\"Height\":1080},\"Duration\":{\"Hours\":1,\"Minutes\":2,\"Seconds\":3},\"Crop\":[140,140,0,0],\"VideoCodec\":\"hevc\",\"AudioList\":[{\"TrackNumber\":1,\"Language\":\"English\",\"LanguageCode\":\"eng\",\"CodecName\":\"AAC\"},{\"TrackNumber\":2,\"Language\":\"Spanish\",\"LanguageCode\":\"spa\",\"CodecName\":\"AC3\"}],\"SubtitleList\":[{\"TrackNumber\":1,\"Language\":\"English\",\"LanguageCode\":\"eng\",\"SourceName\":\"PGS\"},{\"TrackNumber\":2,\"Language\":\"Spanish\",\"LanguageCode\":\"spa\",\"SourceName\":\"PGS\"}]}]} trailing noise";
    ScanResult scan = HandBrakeCliService.ParseScan("sample.mkv", json);
    Assert(scan.Width == 1920 && scan.Height == 1080, "Dimensions were not parsed.");
    Assert(scan.AudioTracks.Count == 2 && scan.SubtitleTracks.Count == 2, "Track counts were not parsed.");
    Assert(scan.AudioTracks[1].Language == "Spanish", "Spanish audio was not visible.");
    Assert(scan.SubtitleTracks[1].Code == "spa", "Spanish subtitle code was not visible.");
    Assert(scan.CropTop == 140 && scan.CropBottom == 140, "Crop estimate was not parsed.");
}

static void TestQuality()
{
    Assert(QualityMapping.ForTarget("nvenc_h265_10bit", "high-fidelity") == 16, "NVENC high-fidelity must use CQ 16.");
    Assert(QualityMapping.ForTarget("x265_10bit", "high-fidelity") == 18, "x265 high-fidelity must use RF 18.");
    Assert(QualityMapping.SliderToQuality(100) == 12, "Moving fully right must increase quality/lower CQ.");
    Assert(QualityMapping.SliderToQuality(0) == 30, "Moving fully left must lower quality/raise CQ.");
}

static void TestSameAsSource()
{
    var plan = BasePlan() with { };
    IReadOnlyList<string> args = HandBrakeCliService.BuildEncodeArguments(plan);
    Assert(ContainsPair(args, "--crop-mode", "none"), "Same as source must map to --crop-mode none.");
    Assert(!args.Contains("--crop"), "Same as source must not inject custom crop values.");
}

static void TestUpscaling()
{
    var plan = new EncodePlan
    {
        SourcePath = "C:\\Video\\source.mkv",
        OutputPath = "C:\\Video\\output.mkv",
        EncoderId = "nvenc_h265_10bit",
        EncoderPreset = "slowest",
        Quality = 16,
        CropMode = "none",
        ScaleMode = "2160p",
        SourceWidth = 1920,
        SourceHeight = 1080,
        TargetWidth = 3840,
        TargetHeight = 2160,
        AudioTracks = [1],
        SubtitleTracks = []
    };
    IReadOnlyList<string> args = HandBrakeCliService.BuildEncodeArguments(plan);
    Assert(args.Contains("--upscaling"), "An output larger than the source must explicitly allow upscaling.");
    Assert(ContainsPair(args, "-w", "3840") && ContainsPair(args, "-l", "2160"), "Target size was not passed.");
}

static void TestCommandPreview()
{
    string command = HandBrakeCliService.FormatCommand("C:\\Program Files\\Transcencode\\HandBrakeCLI.exe", ["-i", "C:\\My Videos\\source.mkv"]);
    Assert(command.Contains("\"C:\\Program Files\\Transcencode\\HandBrakeCLI.exe\""), "Executable path was not quoted.");
    Assert(command.Contains("\"C:\\My Videos\\source.mkv\""), "Source path was not quoted.");
}

static EncodePlan BasePlan() => new()
{
    SourcePath = "C:\\Video\\source.mkv",
    OutputPath = "C:\\Video\\output.mkv",
    EncoderId = "nvenc_h265_10bit",
    EncoderPreset = "slowest",
    Quality = 16,
    CropMode = "none",
    ScaleMode = "source",
    SourceWidth = 1920,
    SourceHeight = 1080,
    AudioTracks = [1],
    SubtitleTracks = []
};

static bool ContainsPair(IReadOnlyList<string> arguments, string first, string second)
{
    for (int i = 0; i < arguments.Count - 1; i++)
    {
        if (arguments[i] == first && arguments[i + 1] == second) return true;
    }
    return false;
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
