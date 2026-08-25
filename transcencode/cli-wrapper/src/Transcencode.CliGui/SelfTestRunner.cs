using System.Text;

namespace Transcencode.CliGui;

public static class SelfTestRunner
{
    public static async Task<int> RunAsync(string sourcePath, string reportPath)
    {
        var report = new StringBuilder();
        report.AppendLine("Transcencode CLI Wrapper 0.2.9 self-test");
        report.AppendLine("UTC: " + DateTime.UtcNow.ToString("O"));
        report.AppendLine();

        string temporaryDirectory = Path.Combine(Path.GetTempPath(), "Transcencode-SelfTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        string outputPath = Path.Combine(temporaryDirectory, "self-test-output.mkv");

        try
        {
            using var cli = new HandBrakeCliService();
            var startInfo = HandBrakeCliService.CreateHiddenStartInfo(cli.ExecutablePath, ["--version"]);
            Assert(!startInfo.UseShellExecute, "HandBrakeCLI uses direct process creation, not a shell.");
            Assert(startInfo.CreateNoWindow, "HandBrakeCLI is configured with CreateNoWindow=true.");
            Assert(startInfo.WindowStyle == System.Diagnostics.ProcessWindowStyle.Hidden, "HandBrakeCLI process window style is Hidden.");
            Assert(startInfo.RedirectStandardOutput && startInfo.RedirectStandardError, "stdout and stderr are redirected into Transcencode.");
            report.AppendLine("PASS: Engine process is hidden and both output streams are redirected.");

            Assert(HandBrakeCliService.TryParseProgress(
                "Encoding: task 1 of 1, 47.25 % (125.20 fps, avg 119.80 fps, ETA 00h00m42s)",
                out EngineProgress? parsedProgress),
                "Progress parser accepted a normal HandBrake line.");
            Assert(parsedProgress is not null && Math.Abs(parsedProgress.Percent - 47.25) < 0.01, "Progress percentage parsed correctly.");
            Assert(parsedProgress!.Eta == TimeSpan.FromSeconds(42), "ETA parsed correctly.");
            report.AppendLine("PASS: Progress, speed, and ETA parser accepted a normal HandBrake status line.");

            ScanResult source = await cli.ScanAsync(sourcePath);
            Assert(source.Width > 0 && source.Height > 0, "Source scan returned valid dimensions.");
            report.AppendLine($"PASS: Real source scan completed: {source.Width}x{source.Height}, {source.AudioTracks.Count} audio, {source.SubtitleTracks.Count} subtitle tracks.");

            var analyzer = new MediaAnalysisService();
            AnalysisResult analysis = await analyzer.AnalyzeAsync(sourcePath, 8, "x264");
            Assert(analysis.Samples.Count > 0, "Deep Analyze returned representative samples.");
            Assert(analysis.RecommendedQuality is >= 14 and <= 24, "Deep Analyze returned a bounded quality recommendation.");
            report.AppendLine($"PASS: Deep Analyze decoded picture content and recommended CQ/RF {analysis.RecommendedQuality:0.0}.");

            var nvencPlan = new EncodePlan
            {
                SourcePath = sourcePath,
                OutputPath = Path.Combine(temporaryDirectory, "nvenc-command-only.mkv"),
                EncoderId = "nvenc_h265_10bit",
                EncoderPreset = "slowest",
                Quality = 16,
                CropMode = "none",
                ScaleMode = "source",
                SourceWidth = source.Width,
                SourceHeight = source.Height,
                AudioTracks = source.AudioTracks.Take(1).Select(track => track.Number).ToArray(),
                SubtitleTracks = []
            };
            IReadOnlyList<string> nvencArguments = HandBrakeCliService.BuildEncodeArguments(nvencPlan);
            Assert(ContainsPair(nvencArguments, "-e", "nvenc_h265_10bit"), "NVENC encoder ID reached HandBrakeCLI arguments.");
            Assert(ContainsPair(nvencArguments, "--crop-mode", "none"), "Same as source maps to --crop-mode none.");
            Assert(!nvencArguments.Any(arg => arg.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase) || arg.Contains("powershell", StringComparison.OrdinalIgnoreCase)), "No shell is introduced into the encode command.");
            report.AppendLine("PASS: NVENC H.265 10-bit command generation, Same as source crop, and shell suppression are correct.");

            var softwarePlan = new EncodePlan
            {
                SourcePath = sourcePath,
                OutputPath = outputPath,
                EncoderId = "x264",
                EncoderPreset = "fast",
                Quality = 24,
                CropMode = "none",
                ScaleMode = "source",
                SourceWidth = source.Width,
                SourceHeight = source.Height,
                AudioTracks = source.AudioTracks.Take(1).Select(track => track.Number).ToArray(),
                SubtitleTracks = []
            };
            IReadOnlyList<string> softwareArguments = HandBrakeCliService.BuildEncodeArguments(softwarePlan);
            ProcessRunResult encode = await cli.RunAsync(softwareArguments, null, null, "self-test-encode");
            Assert(encode.ExitCode == 0, "Software smoke encode returned exit code 0. Log: " + encode.LogPath);
            Assert(File.Exists(outputPath) && new FileInfo(outputPath).Length > 1024, "Software smoke encode produced a non-empty output.");
            report.AppendLine($"PASS: Hidden HandBrakeCLI software smoke encode completed in {encode.Elapsed}.");

            ScanResult output = await cli.ScanAsync(outputPath);
            Assert(output.Width > 0 && output.Height > 0, "Encoded output reopened successfully.");
            report.AppendLine($"PASS: Encoded output reopened through HandBrake: {output.Width}x{output.Height}.");

            var verifier = new VerificationService(cli, analyzer);
            VerificationResult verification = await verifier.VerifyAsync(source, outputPath);
            Assert(verification.Items.All(item => item.Status != "FAIL"), "Verification did not report a structural failure.");
            report.AppendLine($"PASS: Structural and sampled visual verification completed; average similarity {verification.AverageSimilarity:0.0}%.");

            report.AppendLine();
            report.AppendLine("TRANSCENCODE_CLI_WRAPPER_SELF_TEST_PASSED");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, report.ToString(), new UTF8Encoding(true));
            return 0;
        }
        catch (Exception ex)
        {
            report.AppendLine();
            report.AppendLine("FAIL: " + ex);
            report.AppendLine("TRANSCENCODE_CLI_WRAPPER_SELF_TEST_FAILED");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, report.ToString(), new UTF8Encoding(true));
            CrashReporter.Write("CLI wrapper self-test failed", ex);
            return 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, true);
            }
            catch
            {
                // Test cleanup must not replace the actual test result.
            }
        }
    }

    private static bool ContainsPair(IReadOnlyList<string> arguments, string first, string second)
    {
        for (int i = 0; i < arguments.Count - 1; i++)
        {
            if (arguments[i] == first && arguments[i + 1] == second) return true;
        }
        return false;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
