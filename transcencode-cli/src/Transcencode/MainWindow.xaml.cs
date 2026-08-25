using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Transcencode;

public partial class MainWindow : Window
{
    private readonly HandBrakeCliService service = new();
    private readonly SettingsStore settingsStore = new();
    private readonly ObservableCollection<AnalysisSampleResult> analysisResults = [];
    private readonly ObservableCollection<VerificationCheck> verificationChecks = [];
    private readonly List<EncoderChoice> allEncoders =
    [
        new() { DisplayName = "H.265 10-bit (NVIDIA NVENC) — recommended for RTX 2080 Ti", CliName = "nvenc_h265_10bit", IsNvidia = true, IsTenBit = true },
        new() { DisplayName = "H.265 (NVIDIA NVENC)", CliName = "nvenc_h265", IsNvidia = true },
        new() { DisplayName = "H.264 (NVIDIA NVENC)", CliName = "nvenc_h264", IsNvidia = true },
        new() { DisplayName = "H.265 10-bit (x265 software)", CliName = "x265_10bit", IsTenBit = true },
        new() { DisplayName = "H.265 (x265 software)", CliName = "x265" },
        new() { DisplayName = "H.264 (x264 software)", CliName = "x264" }
    ];

    private readonly List<QualityProfile> qualityProfiles =
    [
        new() { Name = "Efficient / good quality", Explanation = "Good everyday quality with a smaller output. Lower the number if difficult scenes look compressed.", NvidiaValue = 22, SoftwareValue = 22 },
        new() { Name = "High quality", Explanation = "Higher quality and a larger file. A practical choice for material you care about.", NvidiaValue = 18, SoftwareValue = 18 },
        new() { Name = "Visually transparent", Explanation = "Designed to look extremely close to the source in normal viewing. This is not a claim of mathematical losslessness.", NvidiaValue = 16, SoftwareValue = 16 },
        new() { Name = "Near-lossless appearance (very large)", Explanation = "Uses an unusually high quality target. Files can be very large, and the result is still not bit-for-bit lossless.", NvidiaValue = 12, SoftwareValue = 12 },
        new() { Name = "Manual", Explanation = "Use the CQ/RF number entered below. Lower numbers increase quality and file size.", NvidiaValue = 16, SoftwareValue = 16 }
    ];

    private AppSettings settings;
    private SourceInfo? sourceInfo;
    private CancellationTokenSource? operationCancellation;
    private bool initializing = true;

    public MainWindow()
    {
        InitializeComponent();
        settings = settingsStore.Load();

        AnalysisGrid.ItemsSource = analysisResults;
        VerificationGrid.ItemsSource = verificationChecks;
        EnginePathText.Text = service.CliPath;
        DiagnosticPathText.Text = CrashReporter.LogDirectory;

        EncoderCombo.ItemsSource = allEncoders;
        QualityProfileCombo.ItemsSource = qualityProfiles;
        ApplyLoadedSettings();
        initializing = false;
        UpdateCropUi();
        UpdateScaleUi();
        UpdateQualityExplanation();
        UpdateCommandPreview();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await CheckEngineAsync();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        operationCancellation?.Cancel();
        SaveSettings();
    }

    private void ApplyLoadedSettings()
    {
        EncoderCombo.SelectedItem = allEncoders.FirstOrDefault(
            item => item.CliName.Equals(settings.EncoderCliName, StringComparison.OrdinalIgnoreCase))
                                    ?? allEncoders[0];

        QualityProfileCombo.SelectedItem = qualityProfiles.FirstOrDefault(
            item => item.Name.Equals(settings.QualityProfile, StringComparison.OrdinalIgnoreCase))
                                           ?? qualityProfiles[2];

        QualityValueTextBox.Text = settings.ManualQuality.ToString("0.##", CultureInfo.InvariantCulture);
        ShowConsoleCheckBox.IsChecked = settings.ShowConsoleWhenEncoding;
        VerifyAfterEncodeCheckBox.IsChecked = settings.VerifyAfterEncoding;
        OpenFolderCheckBox.IsChecked = settings.OpenOutputFolderWhenFinished;

        ComboBoxItem? scaleItem = InterfaceScaleCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item =>
                double.TryParse(item.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double factor) &&
                Math.Abs(factor - settings.InterfaceScale) < 0.001);
        InterfaceScaleCombo.SelectedItem = scaleItem ?? InterfaceScaleCombo.Items[0];
        ApplyInterfaceScale(settings.InterfaceScale);
    }

    private async Task CheckEngineAsync()
    {
        EngineStatusLight.Fill = (Brush)FindResource("WarningBrush");
        EngineStatusText.Text = "Checking HandBrakeCLI…";

        try
        {
            if (!service.Exists)
            {
                throw new FileNotFoundException(
                    "HandBrakeCLI.exe is missing from the Transcencode application folder.",
                    service.CliPath);
            }

            string version = await service.GetVersionAsync();
            string firstLine = version.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                               ?? "HandBrakeCLI found";
            EngineVersionText.Text = firstLine;
            EngineStatusText.Text = "Encoding engine ready";
            EngineStatusLight.Fill = (Brush)FindResource("SuccessBrush");
            AppendConsole("[Transcencode] " + firstLine);

            ProcessResult help = await service.RunAsync(["--help"], null, null);
            if (help.Success)
            {
                List<EncoderChoice> detected = allEncoders
                    .Where(encoder => help.CombinedOutput.Contains(encoder.CliName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (detected.Count > 0)
                {
                    EncoderChoice? selected = EncoderCombo.SelectedItem as EncoderChoice;
                    EncoderCombo.ItemsSource = detected;
                    EncoderCombo.SelectedItem = detected.FirstOrDefault(
                        encoder => encoder.CliName.Equals(selected?.CliName, StringComparison.OrdinalIgnoreCase))
                                                ?? detected[0];
                }
            }
        }
        catch (Exception exception)
        {
            EngineStatusText.Text = "Encoding engine unavailable";
            EngineVersionText.Text = exception.Message;
            EngineStatusLight.Fill = (Brush)FindResource("ErrorBrush");
            string path = CrashReporter.Write("engine-check", exception);
            AppendConsole("[ERROR] " + exception);
            MessageBox.Show(
                exception.Message +
                (string.IsNullOrWhiteSpace(path) ? string.Empty : "\n\nDiagnostic log:\n" + path),
                "Transcencode engine error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = "Choose a video source",
            Filter = "Video files|*.mkv;*.mp4;*.m4v;*.mov;*.avi;*.webm;*.ts;*.m2ts;*.mts;*.wmv;*.mpg;*.mpeg|All files|*.*",
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(settings.LastSourceFolder) ? settings.LastSourceFolder : null
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        SourcePathTextBox.Text = dialog.FileName;
        settings.LastSourceFolder = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
        SuggestOutputPath(dialog.FileName);
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new()
        {
            Title = "Choose output file",
            Filter = "Matroska video|*.mkv|MP4 video|*.mp4",
            AddExtension = true,
            DefaultExt = ".mkv",
            OverwritePrompt = true,
            InitialDirectory = Directory.Exists(settings.LastOutputFolder) ? settings.LastOutputFolder : null,
            FileName = string.IsNullOrWhiteSpace(OutputPathTextBox.Text)
                ? "encoded.mkv"
                : Path.GetFileName(OutputPathTextBox.Text)
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        OutputPathTextBox.Text = dialog.FileName;
        settings.LastOutputFolder = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        await ScanCurrentSourceAsync();
    }

    private async Task<bool> ScanCurrentSourceAsync()
    {
        string input = SourcePathTextBox.Text.Trim();
        if (!File.Exists(input))
        {
            MessageBox.Show("Choose a source video that exists.", "Transcencode", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        BeginOperation("Scanning file details…", showConsole: false);
        AppendConsole("\n[Transcencode] Scanning file details: " + input);

        try
        {
            sourceInfo = await service.ScanAsync(input, AppendConsole, operationCancellation!.Token);
            DisplaySourceInfo(sourceInfo);
            StatusText.Text = "Source scan complete";
            AnalysisStatusText.Text = "Ready to sample representative points through the source.";
            UpdateCommandPreview();
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Source scan canceled";
            return false;
        }
        catch (Exception exception)
        {
            ShowOperationError("Source scan failed", exception);
            return false;
        }
        finally
        {
            EndOperation();
        }
    }

    private void DisplaySourceInfo(SourceInfo info)
    {
        SourceFileText.Text = info.Path;
        SourceDurationText.Text = info.Duration.ToString(@"hh\:mm\:ss");
        SourceResolutionText.Text = $"{info.Width} × {info.Height}";
        SourceCodecText.Text = info.VideoCodec;
        SourceFrameRateText.Text = info.FrameRate;
        SourceHdrText.Text = info.HdrSummary;
        SourceCropText.Text = info.CropSummary;
        AudioGrid.ItemsSource = info.AudioTracks;
        SubtitleGrid.ItemsSource = info.SubtitleTracks;
        TrackSummaryText.Text =
            $"Found {info.AudioTracks.Count} audio track(s) and {info.SubtitleTracks.Count} subtitle track(s). See the Tracks tab for languages and formats.";
    }

    private async void Encode_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureCurrentSourceIsScannedAsync())
        {
            return;
        }

        EncodeOptions? options = TryBuildOptions(showErrors: true);
        if (options is null)
        {
            return;
        }

        string? outputDirectory = Path.GetDirectoryName(options.OutputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            MessageBox.Show("Choose a complete output path.", "Transcencode", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Directory.CreateDirectory(outputDirectory);
        if (File.Exists(options.OutputPath))
        {
            MessageBoxResult overwrite = MessageBox.Show(
                "The output file already exists. Replace it?",
                "Replace output file",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (overwrite != MessageBoxResult.Yes)
            {
                return;
            }

            File.Delete(options.OutputPath);
        }

        BeginOperation("Encoding…", showConsole: settings.ShowConsoleWhenEncoding);
        ResetProgress();
        IReadOnlyList<string> arguments = HandBrakeCommandBuilder.BuildEncodeArguments(options);
        AppendConsole("\n[Transcencode] Starting encode");
        AppendConsole(HandBrakeCommandBuilder.ToDisplayCommand(service.CliPath, arguments));

        try
        {
            ProcessResult result = await service.RunAsync(
                arguments,
                AppendConsole,
                UpdateProgress,
                operationCancellation!.Token);

            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"HandBrakeCLI stopped with exit code {result.ExitCode}.\n\n" +
                    HandBrakeCliService.Tail(result.CombinedOutput, 7000));
            }

            if (!File.Exists(options.OutputPath) || new FileInfo(options.OutputPath).Length < 1024)
            {
                throw new InvalidDataException("The engine reported success, but the output file is missing or empty.");
            }

            EncodeProgressBar.Value = 100;
            PercentText.Text = "100.0%";
            EtaText.Text = "Remaining: 00:00:00";
            FinishText.Text = "Finish: now";
            StatusText.Text = "Encode completed";
            AppendConsole($"[Transcencode] Encode completed: {options.OutputPath}");

            if (settings.VerifyAfterEncoding)
            {
                await VerifyOutputAsync(options.OutputPath, showMessage: false);
            }

            if (settings.OpenOutputFolderWhenFinished)
            {
                OpenOutputInExplorer(options.OutputPath);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Encode canceled";
            AppendConsole("[Transcencode] Encode canceled.");
        }
        catch (Exception exception)
        {
            ShowOperationError("Encoding failed", exception);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task<bool> EnsureCurrentSourceIsScannedAsync()
    {
        string path = SourcePathTextBox.Text.Trim();
        if (sourceInfo is not null && sourceInfo.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return await ScanCurrentSourceAsync();
    }

    private async void RunSampleAnalysis_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureCurrentSourceIsScannedAsync() || sourceInfo is null)
        {
            return;
        }

        EncodeOptions? baseOptions = TryBuildOptions(showErrors: true);
        if (baseOptions is null)
        {
            return;
        }

        int durationSeconds = Math.Max(0, (int)Math.Floor(sourceInfo.Duration.TotalSeconds));
        if (durationSeconds < 8)
        {
            MessageBox.Show(
                "The source is too short for representative compression sampling.",
                "Transcencode analysis",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        const int sampleCount = 5;
        int sampleLength = Math.Clamp(durationSeconds / 20, 2, 5);
        int latestStart = Math.Max(0, durationSeconds - sampleLength - 1);
        int[] starts = Enumerable.Range(0, sampleCount)
            .Select(index => (int)Math.Round(latestStart * ((index + 1.0) / (sampleCount + 1.0))))
            .Distinct()
            .ToArray();

        analysisResults.Clear();
        BeginOperation("Running compression sample analysis…", showConsole: false);
        CancelAnalysisButton.IsEnabled = true;
        AnalysisStatusText.Text = $"Encoding {starts.Length} short samples…";
        AppendConsole("\n[Transcencode] Compression sample analysis started.");

        string tempDirectory = Path.Combine(Path.GetTempPath(), "Transcencode", "analysis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            int sampleNumber = 0;
            foreach (int start in starts)
            {
                operationCancellation!.Token.ThrowIfCancellationRequested();
                sampleNumber++;
                string sampleOutput = Path.Combine(tempDirectory, $"sample-{sampleNumber}.mkv");
                EncodeOptions sampleOptions = CloneForSample(baseOptions, sampleOutput, start, sampleLength);
                IReadOnlyList<string> arguments = HandBrakeCommandBuilder.BuildEncodeArguments(sampleOptions);

                AnalysisStatusText.Text = $"Sample {sampleNumber} of {starts.Length} at {TimeSpan.FromSeconds(start):hh\:mm\:ss}";
                AppendConsole($"[Analyze] Sample {sampleNumber}/{starts.Length} at {TimeSpan.FromSeconds(start):hh\:mm\:ss}");

                ProcessResult result = await service.RunAsync(
                    arguments,
                    AppendConsole,
                    progress => Dispatcher.Invoke(() =>
                    {
                        double overall = ((sampleNumber - 1) + (progress.Percent / 100.0)) / starts.Length * 100.0;
                        EncodeProgressBar.Value = overall;
                        PercentText.Text = overall.ToString("0.0", CultureInfo.InvariantCulture) + "%";
                    }),
                    operationCancellation.Token);

                if (!result.Success || !File.Exists(sampleOutput))
                {
                    throw new InvalidOperationException(
                        $"Compression sample {sampleNumber} failed with exit code {result.ExitCode}.\n" +
                        HandBrakeCliService.Tail(result.CombinedOutput, 5000));
                }

                double megabytes = new FileInfo(sampleOutput).Length / 1_000_000.0;
                double megabitsPerSecond = new FileInfo(sampleOutput).Length * 8.0 / sampleLength / 1_000_000.0;
                analysisResults.Add(new AnalysisSampleResult
                {
                    Sample = sampleNumber,
                    Start = TimeSpan.FromSeconds(start),
                    Megabytes = megabytes,
                    MegabitsPerSecond = megabitsPerSecond,
                    AverageFps = result.LastProgress?.AverageFps ?? result.LastProgress?.CurrentFps
                });
            }

            ApplyRelativeDifficultyLabels();
            double minimum = analysisResults.Min(item => item.MegabitsPerSecond);
            double maximum = analysisResults.Max(item => item.MegabitsPerSecond);
            double ratio = minimum > 0 ? maximum / minimum : 0;
            string guidance = ratio >= 2.0
                ? "The source varies substantially in compression difficulty. Keep constant quality enabled; consider lowering CQ/RF by 1 if the hardest sample shows artifacts."
                : "The sampled points have fairly consistent compression demands at the current settings.";
            AnalysisStatusText.Text = $"Analysis complete. Hardest/easiest bitrate ratio: {ratio:0.00}×. {guidance}";
            StatusText.Text = "Compression sample analysis complete";
        }
        catch (OperationCanceledException)
        {
            AnalysisStatusText.Text = "Analysis canceled.";
            StatusText.Text = "Analysis canceled";
        }
        catch (Exception exception)
        {
            AnalysisStatusText.Text = "Analysis failed. See Live Engine and the diagnostic log.";
            ShowOperationError("Compression sample analysis failed", exception);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, true);
                }
            }
            catch (Exception cleanupException)
            {
                CrashReporter.Write("analysis-cleanup", cleanupException);
            }

            CancelAnalysisButton.IsEnabled = false;
            EndOperation();
        }
    }

    private void ApplyRelativeDifficultyLabels()
    {
        if (analysisResults.Count == 0)
        {
            return;
        }

        double average = analysisResults.Average(item => item.MegabitsPerSecond);
        foreach (AnalysisSampleResult sample in analysisResults)
        {
            double relative = average <= 0 ? 1 : sample.MegabitsPerSecond / average;
            sample.RelativeDifficulty = relative switch
            {
                < 0.75 => "Low",
                < 1.25 => "Typical",
                < 1.75 => "High",
                _ => "Very high"
            };
        }

        AnalysisGrid.Items.Refresh();
    }

    private static EncodeOptions CloneForSample(EncodeOptions source, string output, int start, int length)
    {
        return new EncodeOptions
        {
            InputPath = source.InputPath,
            OutputPath = output,
            Encoder = source.Encoder,
            Quality = source.Quality,
            EncoderPreset = source.EncoderPreset,
            Crop = source.Crop,
            CropTop = source.CropTop,
            CropBottom = source.CropBottom,
            CropLeft = source.CropLeft,
            CropRight = source.CropRight,
            Scale = source.Scale,
            CustomWidth = source.CustomWidth,
            CustomHeight = source.CustomHeight,
            PreserveAllAudio = false,
            PreserveAllSubtitles = false,
            ChapterMarkers = false,
            StartSeconds = start,
            StopAfterSeconds = length,
            IsAnalysisSample = true
        };
    }

    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        await VerifyOutputAsync(OutputPathTextBox.Text.Trim(), showMessage: true);
    }

    private async Task<VerificationResult?> VerifyOutputAsync(string outputPath, bool showMessage)
    {
        verificationChecks.Clear();
        VerificationSummaryText.Text = "Verifying…";

        try
        {
            if (!File.Exists(outputPath))
            {
                throw new FileNotFoundException("The output file does not exist.", outputPath);
            }

            FileInfo file = new(outputPath);
            AddVerification("Output exists", file.Length > 1024, $"{file.Length:N0} bytes");

            SourceInfo output = await service.ScanAsync(outputPath, AppendConsole, operationCancellation?.Token ?? CancellationToken.None);
            AddVerification("Readable media", true, $"HandBrakeCLI re-opened the output as {output.VideoCodec}.");
            AddVerification("Duration", sourceInfo is null || Math.Abs((output.Duration - sourceInfo.Duration).TotalSeconds) <= 2.0,
                sourceInfo is null ? output.Duration.ToString() : $"Source {sourceInfo.Duration}; output {output.Duration}");

            bool sameFrameExpected = sourceInfo is not null &&
                                     CropModeCombo.SelectedIndex == 0 &&
                                     ScaleModeCombo.SelectedIndex == 0;
            AddVerification(
                "Frame size",
                !sameFrameExpected || (output.Width == sourceInfo!.Width && output.Height == sourceInfo.Height),
                sameFrameExpected
                    ? $"Source {sourceInfo!.Width}×{sourceInfo.Height}; output {output.Width}×{output.Height}"
                    : $"Output {output.Width}×{output.Height}");

            bool audioExpected = sourceInfo is not null && sourceInfo.AudioTracks.Count > 0 && PreserveAudioCheckBox.IsChecked == true;
            AddVerification(
                "Audio tracks",
                !audioExpected || output.AudioTracks.Count > 0,
                $"Output contains {output.AudioTracks.Count} audio track(s).");

            AddVerification(
                "Subtitle tracks",
                true,
                $"Output contains {output.SubtitleTracks.Count} subtitle track(s). Container compatibility can affect which source subtitles are retained.");

            bool passed = verificationChecks.All(check => check.Result == "PASS");
            VerificationSummaryText.Text = passed
                ? "Structural verification passed. This confirms readability and expected structure, not a finished VMAF/SSIM visual comparison."
                : "Verification found a structural problem. Review the checks below.";
            StatusText.Text = passed ? "Verification passed" : "Verification found a problem";

            VerificationResult result = new()
            {
                Passed = passed,
                Summary = VerificationSummaryText.Text,
                Checks = verificationChecks.ToArray()
            };

            if (showMessage)
            {
                MessageBox.Show(
                    result.Summary,
                    "Transcencode verification",
                    MessageBoxButton.OK,
                    passed ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            VerificationSummaryText.Text = "Verification canceled.";
            return null;
        }
        catch (Exception exception)
        {
            VerificationSummaryText.Text = "Verification failed.";
            ShowOperationError("Verification failed", exception);
            return null;
        }
    }

    private void AddVerification(string check, bool passed, string details)
    {
        verificationChecks.Add(new VerificationCheck
        {
            Check = check,
            Result = passed ? "PASS" : "FAIL",
            Details = details
        });
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        operationCancellation?.Cancel();
    }

    private void ClearConsole_Click(object sender, RoutedEventArgs e)
    {
        ConsoleTextBox.Clear();
    }

    private void CopyConsole_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(ConsoleTextBox.Text))
        {
            Clipboard.SetText(ConsoleTextBox.Text);
        }
    }

    private void SourcePathTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (initializing)
        {
            return;
        }

        if (sourceInfo is not null &&
            !sourceInfo.Path.Equals(SourcePathTextBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            sourceInfo = null;
            SourceFileText.Text = "Source changed; scan again.";
            AudioGrid.ItemsSource = null;
            SubtitleGrid.ItemsSource = null;
        }

        UpdateCommandPreview();
    }

    private void EncoderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializing)
        {
            return;
        }

        ApplySelectedQualityProfile();
        UpdateQualityExplanation();
        UpdateCommandPreview();
        SaveSettings();
    }

    private void QualityProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializing)
        {
            return;
        }

        ApplySelectedQualityProfile();
        UpdateQualityExplanation();
        UpdateCommandPreview();
        SaveSettings();
    }

    private void ApplySelectedQualityProfile()
    {
        if (QualityProfileCombo.SelectedItem is not QualityProfile profile ||
            EncoderCombo.SelectedItem is not EncoderChoice encoder ||
            profile.Name.Equals("Manual", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        QualityValueTextBox.Text = (encoder.IsNvidia ? profile.NvidiaValue : profile.SoftwareValue)
            .ToString("0.##", CultureInfo.InvariantCulture);
    }

    private void UpdateQualityExplanation()
    {
        if (QualityProfileCombo.SelectedItem is not QualityProfile profile ||
            EncoderCombo.SelectedItem is not EncoderChoice encoder)
        {
            return;
        }

        QualityExplanationText.Text =
            $"{profile.Explanation} Current encoder: {encoder.DisplayName}. " +
            "Lower CQ/RF numbers increase quality and file size; higher numbers reduce both.";
    }

    private void CropModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializing)
        {
            return;
        }

        UpdateCropUi();
        UpdateCommandPreview();
    }

    private void UpdateCropUi()
    {
        CustomCropPanel.IsEnabled = CropModeCombo.SelectedIndex == 3;
        CropExplanationText.Text = CropModeCombo.SelectedIndex switch
        {
            0 => "No pixels are cropped. The encoded frame keeps the same black bars as the source.",
            1 => "HandBrake removes only black bars it can identify conservatively. This is the least aggressive automatic option.",
            2 => "HandBrake automatically removes detected black bars. Review a preview when framing is important.",
            3 => "Enter the exact pixels to remove from the top, bottom, left, and right.",
            _ => string.Empty
        };
    }

    private void ScaleModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializing)
        {
            return;
        }

        UpdateScaleUi();
        UpdateCommandPreview();
    }

    private void UpdateScaleUi()
    {
        CustomScalePanel.IsEnabled = ScaleModeCombo.SelectedIndex == 4;
        ScaleExplanationText.Text = ScaleModeCombo.SelectedIndex switch
        {
            0 => "Keeps the source resolution. No upscaling or downscaling is requested.",
            1 => "Targets a 1920 × 1080 frame while retaining display aspect ratio.",
            2 => "Targets a 2560 × 1440 frame while retaining display aspect ratio.",
            3 => "Targets a 3840 × 2160 frame while retaining display aspect ratio.",
            4 => "Uses the custom target while retaining display aspect ratio.",
            _ => string.Empty
        };
    }

    private void OptionChanged(object sender, RoutedEventArgs e)
    {
        if (initializing)
        {
            return;
        }

        if (QualityProfileCombo.SelectedItem is QualityProfile profile &&
            !profile.Name.Equals("Manual", StringComparison.OrdinalIgnoreCase) &&
            sender == QualityValueTextBox)
        {
            QualityProfileCombo.SelectedItem = qualityProfiles[^1];
        }

        UpdateCommandPreview();
        SaveSettings();
    }

    private void SettingChanged(object sender, RoutedEventArgs e)
    {
        if (!initializing)
        {
            SaveSettings();
        }
    }

    private void InterfaceScaleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (InterfaceScaleCombo.SelectedItem is not ComboBoxItem item ||
            !double.TryParse(item.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double factor))
        {
            return;
        }

        factor = Math.Clamp(factor, 1.0, 2.0);
        ApplyInterfaceScale(factor);
        if (!initializing)
        {
            settings.InterfaceScale = factor;
            SaveSettings();
        }
    }

    private void ApplyInterfaceScale(double factor)
    {
        ScaleRoot.LayoutTransform = new ScaleTransform(factor, factor);
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Deliberately retained as a stable hook for future tab-specific refresh logic.
    }

    private EncodeOptions? TryBuildOptions(bool showErrors)
    {
        try
        {
            string input = SourcePathTextBox.Text.Trim();
            string output = OutputPathTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(input)) throw new InvalidOperationException("Choose a source file.");
            if (string.IsNullOrWhiteSpace(output)) throw new InvalidOperationException("Choose an output file.");
            if (EncoderCombo.SelectedItem is not EncoderChoice encoder) throw new InvalidOperationException("Choose a video encoder.");
            if (!double.TryParse(QualityValueTextBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double quality) || quality < 0 || quality > 51)
            {
                throw new InvalidOperationException("CQ/RF must be a number from 0 through 51. Lower numbers increase quality and file size.");
            }

            string preset = (EncoderPresetCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Default";
            return new EncodeOptions
            {
                InputPath = input,
                OutputPath = output,
                Encoder = encoder,
                Quality = quality,
                EncoderPreset = preset,
                Crop = (CropChoice)Math.Clamp(CropModeCombo.SelectedIndex, 0, 3),
                CropTop = ParseNonNegative(CropTopTextBox.Text, "Top crop"),
                CropBottom = ParseNonNegative(CropBottomTextBox.Text, "Bottom crop"),
                CropLeft = ParseNonNegative(CropLeftTextBox.Text, "Left crop"),
                CropRight = ParseNonNegative(CropRightTextBox.Text, "Right crop"),
                Scale = (ScaleChoice)Math.Clamp(ScaleModeCombo.SelectedIndex, 0, 4),
                CustomWidth = ParseNonNegative(CustomWidthTextBox.Text, "Custom width"),
                CustomHeight = ParseNonNegative(CustomHeightTextBox.Text, "Custom height"),
                PreserveAllAudio = PreserveAudioCheckBox.IsChecked == true,
                PreserveAllSubtitles = PreserveSubtitlesCheckBox.IsChecked == true,
                ChapterMarkers = ChapterMarkersCheckBox.IsChecked == true,
                WebOptimizeMp4 = WebOptimizeCheckBox.IsChecked == true
            };
        }
        catch (Exception exception)
        {
            if (showErrors)
            {
                MessageBox.Show(exception.Message, "Transcencode settings", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            return null;
        }
    }

    private static int ParseNonNegative(string value, string name)
    {
        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed < 0)
        {
            throw new InvalidOperationException(name + " must be a whole number of zero or greater.");
        }

        return parsed;
    }

    private void UpdateCommandPreview()
    {
        if (initializing || CommandPreviewTextBox is null)
        {
            return;
        }

        EncodeOptions? options = TryBuildOptions(showErrors: false);
        CommandPreviewTextBox.Text = options is null
            ? "Choose a source, output, and valid settings to see the exact HandBrakeCLI command."
            : HandBrakeCommandBuilder.ToDisplayCommand(
                service.CliPath,
                HandBrakeCommandBuilder.BuildEncodeArguments(options));
    }

    private void SuggestOutputPath(string sourcePath)
    {
        string directory = Path.GetDirectoryName(sourcePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        string fileName = Path.GetFileNameWithoutExtension(sourcePath) + "-transcencode.mkv";
        OutputPathTextBox.Text = Path.Combine(directory, fileName);
        settings.LastOutputFolder = directory;
    }

    private void BeginOperation(string status, bool showConsole)
    {
        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        StatusText.Text = status;
        ScanButton.IsEnabled = false;
        EncodeButton.IsEnabled = false;
        RunSampleAnalysisButton.IsEnabled = false;
        VerifyButton.IsEnabled = false;
        CancelButton.IsEnabled = true;

        if (showConsole)
        {
            SelectTab("Live Engine");
        }
    }

    private void EndOperation()
    {
        operationCancellation?.Dispose();
        operationCancellation = null;
        ScanButton.IsEnabled = true;
        EncodeButton.IsEnabled = true;
        RunSampleAnalysisButton.IsEnabled = true;
        VerifyButton.IsEnabled = true;
        CancelButton.IsEnabled = false;
        CancelAnalysisButton.IsEnabled = false;
    }

    private void SelectTab(string header)
    {
        TabItem? tab = MainTabs.Items.OfType<TabItem>()
            .FirstOrDefault(item => string.Equals(item.Header?.ToString(), header, StringComparison.Ordinal));
        if (tab is not null)
        {
            MainTabs.SelectedItem = tab;
        }
    }

    private void ResetProgress()
    {
        EncodeProgressBar.Value = 0;
        PercentText.Text = "0.0%";
        FpsText.Text = "FPS: —";
        EtaText.Text = "Remaining: —";
        FinishText.Text = "Finish: —";
    }

    private void UpdateProgress(ProgressInfo progress)
    {
        Dispatcher.Invoke(() =>
        {
            EncodeProgressBar.Value = progress.Percent;
            PercentText.Text = progress.Percent.ToString("0.0", CultureInfo.InvariantCulture) + "%";
            FpsText.Text = progress.AverageFps.HasValue
                ? $"FPS: {progress.CurrentFps:0.0} (avg {progress.AverageFps:0.0})"
                : progress.CurrentFps.HasValue ? $"FPS: {progress.CurrentFps:0.0}" : "FPS: —";

            if (progress.Eta.HasValue)
            {
                EtaText.Text = "Remaining: " + progress.Eta.Value.ToString(@"hh\:mm\:ss");
                FinishText.Text = "Finish: " + DateTime.Now.Add(progress.Eta.Value).ToString("h:mm:ss tt", CultureInfo.CurrentCulture);
            }
            else
            {
                EtaText.Text = "Remaining: calculating…";
                FinishText.Text = "Finish: calculating…";
            }
        });
    }

    private void AppendConsole(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            ConsoleTextBox.AppendText(line + Environment.NewLine);
            if (ConsoleTextBox.Text.Length > 2_000_000)
            {
                ConsoleTextBox.Text = ConsoleTextBox.Text[^1_500_000..];
                ConsoleTextBox.CaretIndex = ConsoleTextBox.Text.Length;
            }

            ConsoleTextBox.ScrollToEnd();
        });
    }

    private void ShowOperationError(string title, Exception exception)
    {
        StatusText.Text = title;
        AppendConsole("[ERROR] " + exception);
        string logPath = CrashReporter.Write(title.Replace(' ', '-').ToLowerInvariant(), exception);
        MessageBox.Show(
            exception.Message +
            (string.IsNullOrWhiteSpace(logPath) ? string.Empty : "\n\nDiagnostic log:\n" + logPath),
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void SaveSettings()
    {
        if (initializing)
        {
            return;
        }

        if (EncoderCombo.SelectedItem is EncoderChoice encoder)
        {
            settings.EncoderCliName = encoder.CliName;
        }

        if (QualityProfileCombo.SelectedItem is QualityProfile profile)
        {
            settings.QualityProfile = profile.Name;
        }

        if (double.TryParse(QualityValueTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double quality))
        {
            settings.ManualQuality = Math.Clamp(quality, 0, 51);
        }

        settings.ShowConsoleWhenEncoding = ShowConsoleCheckBox.IsChecked == true;
        settings.VerifyAfterEncoding = VerifyAfterEncodeCheckBox.IsChecked == true;
        settings.OpenOutputFolderWhenFinished = OpenFolderCheckBox.IsChecked == true;
        settingsStore.Save(settings);
    }

    private static void OpenOutputInExplorer(string outputPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select,\"" + outputPath + "\"",
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            CrashReporter.Write("open-output-folder", exception);
        }
    }
}
