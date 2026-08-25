using System.Text;

namespace Transcencode;

internal static class SelfTestRunner
{
    internal static async Task<int> RunAsync(string reportPath, string? sourcePath)
    {
        StringBuilder report = new();
        report.AppendLine("Transcencode packaged application self-test");
        report.AppendLine($"UTC: {DateTime.UtcNow:O}");
        report.AppendLine($"Base directory: {AppContext.BaseDirectory}");

        try
        {
            HandBrakeCliService service = new();
            if (!service.Exists)
            {
                throw new FileNotFoundException("HandBrakeCLI.exe is not beside Transcencode.exe.", service.CliPath);
            }

            string version = await service.GetVersionAsync();
            report.AppendLine("Engine version check: PASS");
            report.AppendLine(version.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? version);

            _ = new MainWindow();
            report.AppendLine("Main window construction: PASS");

            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                if (!File.Exists(sourcePath))
                {
                    throw new FileNotFoundException("Self-test source was not found.", sourcePath);
                }

                SourceInfo source = await service.ScanAsync(sourcePath, null);
                if (source.Width <= 0 || source.Height <= 0 || source.Duration <= TimeSpan.Zero)
                {
                    throw new InvalidDataException("Source scan did not return usable dimensions and duration.");
                }

                report.AppendLine($"Source scan: PASS ({source.Width}x{source.Height}, {source.Duration})");
                report.AppendLine($"Audio tracks: {source.AudioTracks.Count}");
                report.AppendLine($"Subtitle tracks: {source.SubtitleTracks.Count}");

                string outputPath = Path.Combine(
                    Path.GetDirectoryName(reportPath) ?? Path.GetTempPath(),
                    "transcencode-self-test-output.mkv");

                EncodeOptions options = new()
                {
                    InputPath = sourcePath,
                    OutputPath = outputPath,
                    Encoder = new EncoderChoice
                    {
                        DisplayName = "H.264 (software test)",
                        CliName = "x264"
                    },
                    Quality = 24,
                    Crop = CropChoice.PreserveOriginal,
                    Scale = ScaleChoice.SameAsSource,
                    PreserveAllAudio = true,
                    PreserveAllSubtitles = true,
                    ChapterMarkers = true
                };

                IReadOnlyList<string> arguments = HandBrakeCommandBuilder.BuildEncodeArguments(options);
                ProcessResult encode = await service.RunAsync(arguments, null, null);
                if (!encode.Success || !File.Exists(outputPath) || new FileInfo(outputPath).Length < 1024)
                {
                    throw new InvalidOperationException(
                        $"Self-test encode failed with exit code {encode.ExitCode}.\n{HandBrakeCliService.Tail(encode.CombinedOutput, 5000)}");
                }

                report.AppendLine($"Completed encode: PASS ({new FileInfo(outputPath).Length} bytes)");

                SourceInfo encoded = await service.ScanAsync(outputPath, null);
                if (encoded.Width != source.Width || encoded.Height != source.Height)
                {
                    throw new InvalidDataException(
                        $"Same as source failed. Input was {source.Width}x{source.Height}; output was {encoded.Width}x{encoded.Height}.");
                }

                if (Math.Abs((encoded.Duration - source.Duration).TotalSeconds) > 2.0)
                {
                    throw new InvalidDataException(
                        $"Encoded duration differs too much. Input {source.Duration}; output {encoded.Duration}.");
                }

                report.AppendLine("Same as source frame preservation: PASS");
                report.AppendLine("Encoded output re-scan: PASS");
                File.Delete(outputPath);
            }

            report.AppendLine("TRANSCENCODE_CLI_SELF_TEST_PASSED");
            WriteReport(reportPath, report.ToString());
            return 0;
        }
        catch (Exception exception)
        {
            report.AppendLine();
            report.AppendLine("TRANSCENCODE_CLI_SELF_TEST_FAILED");
            report.AppendLine(exception.ToString());
            WriteReport(reportPath, report.ToString());
            CrashReporter.Write("self-test", exception);
            return 1;
        }
    }

    private static void WriteReport(string reportPath, string content)
    {
        string? directory = Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(reportPath, content, new UTF8Encoding(true));
    }
}
