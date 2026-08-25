using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Transcencode.CliGui;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<TrackItem> audioTracks = [];
    private readonly ObservableCollection<TrackItem> subtitleTracks = [];
    private readonly ObservableCollection<AnalysisSample> analysisSamples = [];
    private readonly ObservableCollection<VerificationItem> verificationItems = [];
    private readonly SettingsService settingsService = new();
    private readonly MediaAnalysisService mediaAnalysisService = new();
    private readonly HandBrakeCliService cliService;
    private readonly VerificationService verificationService;
    private readonly DispatcherTimer operationTimer;

    private AppSettings settings;
    private ScanResult? currentScan;
    private AnalysisResult? currentAnalysis;
    private CancellationTokenSource? operationCancellation;
    private DateTime operationStarted;
    private DateTime etaUpdatedAt;
    private TimeSpan? etaAtUpdate;
    private double latestPercent;
    private bool initializing = true;
    private bool busy;

    public MainWindow()
    {
        InitializeComponent();
        cliService = new HandBrakeCliService();
        verificationService = new VerificationService(cliService, mediaAnalysisService);
        settings = settingsService.Load();

        AudioTracksGrid.ItemsSource = audioTracks;
        SubtitleTracksGrid.ItemsSource = subtitleTracks;
        AnalysisGrid.ItemsSource = analysisSamples;
        VerificationGrid.ItemsSource = verificationItems;

        PopulateOptions();
        ApplySavedSettings();
        UpdateQualityUi();
        UpdateCropUi();
        UpdateScaleUi();
        UpdateHdrGuidance();

        operationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        operationTimer.Tick += OperationTimer_Tick;
        initializing = false;
    }

    private void PopulateOptions()
    {
        EncoderComboBox.ItemsSource = new List<OptionItem>
        {
            new("nvenc_h265_10bit", "H.265 10-bit (NVIDIA NVENC)", "Recommended first choice for the RTX 2080 Ti and HDR sources."),
            new("nvenc_h265", "H.265 (NVIDIA NVENC)", "Fast GPU encoding for 8-bit sources."),
            new("nvenc_h264", "H.264 (NVIDIA NVENC)", "Fast GPU encoding with broad playback compatibility."),
            new("x265_10bit", "H.265 10-bit (x265 software)", "Slower CPU encoding with strong compression efficiency."),
            new("x265", "H.265 (x265 software)", "Slower CPU encoding for 8-bit sources."),
            new("x264", "H.264 (x264 software)", "CPU encoding with broad compatibility.")
        };

        QualityTargetComboBox.ItemsSource = new List<OptionItem>
        {
            new("high-fidelity", "Match source visually (high-fidelity target)", "A conservative starting point intended to make compression difficult to notice."),
            new("high", "High quality", "High quality with a somewhat smaller file."),
            new("balanced", "Balanced quality and size", "A moderate quality/file-size compromise."),
            new("smaller", "Smaller file", "More compression and a greater chance of visible loss."),
            new("custom", "Custom quality", "Use the custom slider below.")
        };

        CropModeComboBox.ItemsSource = new List<OptionItem>
        {
            new("none", "Same as source (preserve original black bars)", "No pixels are cropped. The complete source frame is retained."),
            new("conservative", "Safe auto-crop (least aggressive)", "Remove only consistently detected borders while minimizing the risk of cutting picture content."),
            new("auto", "Automatic crop (remove detected black bars)", "Use HandBrake's normal automatic crop estimate."),
            new("custom", "Custom crop", "Enter exact top, bottom, left, and right values.")
        };

        ScaleModeComboBox.ItemsSource = new List<OptionItem>
        {
            new("source", "Same as source", "Do not request a different output size."),
            new("1080p", "1920 × 1080", "Scale to a 1080p bounding size while preserving aspect ratio."),
            new("1440p", "2560 × 1440", "Scale to a 1440p bounding size while preserving aspect ratio."),
            new("2160p", "3840 × 2160 (4K)", "Scale to a 4K bounding size while preserving aspect ratio."),
            new("custom", "Custom size", "Enter a custom width and height.")
        };

        InterfaceScaleComboBox.ItemsSource = new List<OptionItem>
        {
            new("1", "100%"),
            new("1.1", "110%"),
            new("1.25", "125%"),
            new("1.5", "150%"),
            new("1.75", "175%"),
            new("2", "200%")
        };
    }

    private void ApplySavedSettings()
    {
        bool nvidia = DetectNvidiaGpu();
        string defaultEncoder = nvidia ? "nvenc_h265_10bit" : "x265_10bit";
        SelectOption(EncoderComboBox, string.IsNullOrWhiteSpace(settings.EncoderId) ? defaultEncoder : settings.EncoderId, defaultEncoder);
        EncoderAvailabilityTextBlock.Text = nvidia
            ? "NVIDIA display adapter detected. NVENC will still be verified by the first actual encode."
            : "No NVIDIA adapter was detected by Windows. Software encoding is selected by default.";

        SelectOption(QualityTargetComboBox, settings.QualityTarget, "high-fidelity");
        SelectOption(CropModeComboBox, settings.CropMode, "none");
        SelectOption(ScaleModeComboBox, "source", "source");
        SelectOption(InterfaceScaleComboBox, settings.InterfaceScale.ToString(CultureInfo.InvariantCulture), "1");
        QualitySlider.Value = QualityMapping.QualityToSlider(settings.CustomQuality);
        NvdecCheckBox.IsChecked = settings.EnableNvdec;
        ApplyInterfaceScale(settings.InterfaceScale);
    }

    private static bool DetectNvidiaGpu()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (ManagementObject item in searcher.Get())
            {
                string name = item["Name"]?.ToString() ?? string.Empty;
                if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch
        {
            // Fall through to the driver-runtime signal.
        }

        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return File.Exists(Path.Combine(system, "nvEncodeAPI64.dll"));
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        StatusTextBlock.Text = "Ready. Choose a source file. HandBrakeCLI will run hidden when used.";
        string[] args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        string? droppedFile = args.FirstOrDefault(File.Exists);
        if (!string.IsNullOrWhiteSpace(droppedFile))
        {
            SourcePathTextBox.Text = droppedFile;
            SetDefaultOutputPath(droppedFile);
            await ScanSourceAsync();
        }
    }

    private async void BrowseSourceButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a video source",
            Filter = "Video files|*.mkv;*.mp4;*.m4v;*.mov;*.avi;*.ts;*.m2ts;*.webm;*.wmv;*.mpg;*.mpeg|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            SourcePathTextBox.Text = dialog.FileName;
            SetDefaultOutputPath(dialog.FileName);
            await ScanSourceAsync();
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e) => await ScanSourceAsync();

    private async Task ScanSourceAsync()
    {
        string source = SourcePathTextBox.Text.Trim();
        if (!File.Exists(source))
        {
            MessageBox.Show(this, "Choose an existing source file first.", "Transcencode", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryBeginOperation("Scanning source details")) return;
        try
        {
            MainTabs.SelectedItem = SourceTracksTab;
            AppendConsole("\r\n=== SOURCE SCAN ===\r\n");
            ScanResult result = await cliService.ScanAsync(
                source,
                (line, isError) => AppendConsole((isError ? "[stderr] " : "") + line + "\r\n"),
                operationCancellation!.Token);

            currentScan = result;
            currentAnalysis = null;
            ApplyAnalysisButton.IsEnabled = false;
            analysisSamples.Clear();
            verificationItems.Clear();
            VerifySummaryTextBlock.Text = "No output has been verified.";

            audioTracks.Clear();
            foreach (TrackItem track in result.AudioTracks) audioTracks.Add(track);
            subtitleTracks.Clear();
            foreach (TrackItem track in result.SubtitleTracks) subtitleTracks.Add(track);
            if (audioTracks.Count > 0) audioTracks[0].Selected = true;

            SourceSummaryTextBlock.Text = result.Summary;
            UpdateHdrGuidance();
            StatusTextBlock.Text = $"Source scan complete: {result.AudioTracks.Count} audio and {result.SubtitleTracks.Count} subtitle tracks found.";
            EngineStatusTextBlock.Text = "Scan complete";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Source scan canceled.";
            EngineStatusTextBlock.Text = "Canceled";
        }
        catch (Exception ex)
        {
            ShowOperationError("Source scan failed", ex);
        }
        finally
        {
            EndOperation();
        }
    }

    private void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Choose encoded output",
            Filter = "Matroska video|*.mkv|MP4 video|*.mp4|All files|*.*",
            AddExtension = true,
            DefaultExt = settings.OutputExtension.TrimStart('.'),
            FileName = Path.GetFileName(OutputPathTextBox.Text)
        };

        string currentDirectory = Path.GetDirectoryName(OutputPathTextBox.Text) ?? string.Empty;
        if (Directory.Exists(currentDirectory)) dialog.InitialDirectory = currentDirectory;
        if (dialog.ShowDialog(this) == true)
        {
            OutputPathTextBox.Text = dialog.FileName;
            settings.OutputExtension = Path.GetExtension(dialog.FileName);
        }
    }

    private void SetDefaultOutputPath(string source)
    {
        string directory = Path.GetDirectoryName(source) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        string baseName = Path.GetFileNameWithoutExtension(source);
        string extension = settings.OutputExtension is ".mp4" or ".mkv" ? settings.OutputExtension : ".mkv";
        OutputPathTextBox.Text = Path.Combine(directory, baseName + ".transcoded" + extension);
    }

    private void OutputPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (initializing) return;
        string extension = Path.GetExtension(OutputPathTextBox.Text);
        if (extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            settings.OutputExtension = extension.ToLowerInvariant();
        }
    }

    private void SelectAllAudio_Click(object sender, RoutedEventArgs e)
    {
        foreach (TrackItem track in audioTracks) track.Selected = true;
    }

    private void SelectNoAudio_Click(object sender, RoutedEventArgs e)
    {
        foreach (TrackItem track in audioTracks) track.Selected = false;
    }

    private void SelectAllSubtitles_Click(object sender, RoutedEventArgs e)
    {
        foreach (TrackItem track in subtitleTracks) track.Selected = true;
    }

    private void SelectNoSubtitles_Click(object sender, RoutedEventArgs e)
    {
        foreach (TrackItem track in subtitleTracks) track.Selected = false;
    }

    private void EncoderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializing) return;
        settings.EncoderId = SelectedOption(EncoderComboBox)?.Id ?? string.Empty;
        UpdateQualityUi();
        UpdateHdrGuidance();
    }

    private void QualityTargetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializing) return;
        settings.QualityTarget = SelectedOption(QualityTargetComboBox)?.Id ?? "high-fidelity";
        UpdateQualityUi();
    }

    private void QualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (initializing || QualitySlider is null) return;
        if (SelectedOption(QualityTargetComboBox)?.Id == "custom")
        {
            settings.CustomQuality = QualityMapping.SliderToQuality(QualitySlider.Value);
        }
        UpdateQualityUi();
    }

    private double CurrentQuality()
    {
        string encoder = SelectedOption(EncoderComboBox)?.Id ?? "nvenc_h265_10bit";
        string target = SelectedOption(QualityTargetComboBox)?.Id ?? "high-fidelity";
        return target == "custom"
            ? QualityMapping.SliderToQuality(QualitySlider.Value)
            : QualityMapping.ForTarget(encoder, target);
    }

    private void UpdateQualityUi()
    {
        if (QualitySlider is null || QualityTargetComboBox is null || EncoderComboBox is null) return;
        string target = SelectedOption(QualityTargetComboBox)?.Id ?? "high-fidelity";
        bool custom = target == "custom";
        QualitySlider.IsEnabled = custom;
        double quality = CurrentQuality();
        if (!custom) QualitySlider.Value = QualityMapping.QualityToSlider(quality);
        ActualQualityTextBlock.Text = $"Actual setting: CQ/RF {quality:0.0}";

        string targetText = target switch
        {
            "high-fidelity" => "This is a high-fidelity starting target intended to look like the source in normal viewing. It is not mathematical losslessness and is not an automatic frame-by-frame guarantee.",
            "high" => "High quality with a somewhat smaller file than the high-fidelity target.",
            "balanced" => "A moderate tradeoff between quality and file size.",
            "smaller" => "More compression; visible loss is more likely in dark, grainy, or high-motion scenes.",
            _ => "Move the slider right for higher quality. The displayed CQ/RF number becomes lower as quality increases."
        };
        QualityExplanationTextBlock.Text = targetText + $" Current CQ/RF: {quality:0.0}. Lower CQ/RF generally means more bitrate and a larger file.";
    }

    private void CropModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializing) return;
        settings.CropMode = SelectedOption(CropModeComboBox)?.Id ?? "none";
        UpdateCropUi();
    }

    private void UpdateCropUi()
    {
        if (CropModeComboBox is null) return;
        OptionItem? option = SelectedOption(CropModeComboBox);
        CropExplanationTextBlock.Text = option?.Description ?? string.Empty;
        bool custom = option?.Id == "custom";
        CustomCropPanel.IsEnabled = custom;
    }

    private void ScaleModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateScaleUi();

    private void UpdateScaleUi()
    {
        if (ScaleModeComboBox is null) return;
        CustomScalePanel.IsEnabled = SelectedOption(ScaleModeComboBox)?.Id == "custom";
    }

    private async void DeepAnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        if (currentScan is null)
        {
            MessageBox.Show(this, "Load and scan a source file first.", "Transcencode", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!TryBeginOperation("Deep analyzing representative picture samples")) return;

        try
        {
            AnalysisProgressBar.Value = 0;
            MainTabs.SelectedItem = AnalyzeTab;
            string encoder = SelectedOption(EncoderComboBox)?.Id ?? "nvenc_h265_10bit";
            var progress = new Progress<int>(value => AnalysisProgressBar.Value = value);
            currentAnalysis = await mediaAnalysisService.AnalyzeAsync(
                currentScan.SourcePath,
                48,
                encoder,
                progress,
                operationCancellation!.Token);

            analysisSamples.Clear();
            foreach (AnalysisSample sample in currentAnalysis.Samples) analysisSamples.Add(sample);
            AnalysisSummaryTextBlock.Text = currentAnalysis.Explanation;
            ApplyAnalysisButton.IsEnabled = true;
            StatusTextBlock.Text = $"Deep Analyze complete. Recommended CQ/RF {currentAnalysis.RecommendedQuality:0.0}.";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Deep Analyze canceled.";
        }
        catch (Exception ex)
        {
            ShowOperationError("Deep Analyze failed", ex);
        }
        finally
        {
            EndOperation();
        }
    }

    private void ApplyAnalysisButton_Click(object sender, RoutedEventArgs e)
    {
        if (currentAnalysis is null) return;
        SelectOption(QualityTargetComboBox, "custom", "custom");
        QualitySlider.Value = QualityMapping.QualityToSlider(currentAnalysis.RecommendedQuality);
        settings.CustomQuality = currentAnalysis.RecommendedQuality;
        UpdateQualityUi();
        MainTabs.SelectedIndex = 1;
        StatusTextBlock.Text = $"Applied Deep Analyze recommendation: CQ/RF {currentAnalysis.RecommendedQuality:0.0}.";
    }

    private async void EncodeNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (currentScan is null)
        {
            MessageBox.Show(this, "Load and scan a source file before encoding.", "Transcencode", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string output = OutputPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(output))
        {
            MessageBox.Show(this, "Choose an output path.", "Transcencode", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (Path.GetFullPath(output).Equals(Path.GetFullPath(currentScan.SourcePath), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "The output cannot overwrite the source file.", "Transcencode", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        EncodePlan plan;
        try
        {
            plan = BuildEncodePlan(currentScan, output);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Encoding settings need attention", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (currentScan.IsDolbyVision && plan.EncoderId.StartsWith("nvenc_", StringComparison.OrdinalIgnoreCase))
        {
            MessageBoxResult choice = MessageBox.Show(
                this,
                "This source appears to contain Dolby Vision. NVIDIA NVENC remains available, but complete Dolby Vision dynamic-metadata preservation is not guaranteed in this path. The output may retain only a compatible HDR base layer. Continue with this NVENC test encode?",
                "Dolby Vision compatibility warning",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (choice != MessageBoxResult.Yes) return;
        }

        if (!TryBeginOperation("Encoding")) return;
        try
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(output));
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            IReadOnlyList<string> arguments = HandBrakeCliService.BuildEncodeArguments(plan);
            string command = HandBrakeCliService.FormatCommand(cliService.ExecutablePath, arguments);
            CommandPreviewTextBlock.Text = command;
            MainTabs.SelectedItem = LiveEngineTab;
            AppendConsole("\r\n=== ENCODE START ===\r\n" + command + "\r\n\r\n");

            ResetEngineProgress("Starting HandBrake encoder");
            operationStarted = DateTime.Now;
            operationTimer.Start();

            ProcessRunResult result = await cliService.RunAsync(
                arguments,
                (line, isError) => AppendConsole((isError ? "[stderr] " : "[stdout] ") + line + "\r\n"),
                progress => Dispatcher.BeginInvoke(() => ApplyEngineProgress(progress)),
                "encode",
                operationCancellation!.Token);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"HandBrake ended with exit code {result.ExitCode}.\n\nFull log: {result.LogPath}");
            }

            ApplyEngineProgress(new EngineProgress { Phase = "Complete", Percent = 100, RawLine = "Encode complete" });
            StatusTextBlock.Text = $"Encode complete: {output}";
            EngineStatusTextBlock.Text = "Complete";
            AppendConsole($"\r\n=== ENCODE COMPLETE ===\r\nExit code 0\r\nLog: {result.LogPath}\r\n");
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Encode canceled.";
            EngineStatusTextBlock.Text = "Canceled";
            AppendConsole("\r\n=== ENCODE CANCELED ===\r\n");
        }
        catch (Exception ex)
        {
            ShowOperationError("Encode failed", ex);
        }
        finally
        {
            operationTimer.Stop();
            EndOperation();
        }
    }

    private EncodePlan BuildEncodePlan(ScanResult scan, string output)
    {
        OptionItem encoder = SelectedOption(EncoderComboBox) ?? throw new InvalidOperationException("Choose an encoder.");
        OptionItem crop = SelectedOption(CropModeComboBox) ?? throw new InvalidOperationException("Choose black-bar handling.");
        OptionItem scale = SelectedOption(ScaleModeComboBox) ?? throw new InvalidOperationException("Choose output resolution.");

        int width = 0;
        int height = 0;
        switch (scale.Id)
        {
            case "1080p": width = 1920; height = 1080; break;
            case "1440p": width = 2560; height = 1440; break;
            case "2160p": width = 3840; height = 2160; break;
            case "custom":
                width = ParseNonNegative(CustomWidthTextBox.Text, "Custom width", allowZero: false);
                height = ParseNonNegative(CustomHeightTextBox.Text, "Custom height", allowZero: false);
                break;
        }

        string preset = encoder.Id.StartsWith("nvenc_", StringComparison.OrdinalIgnoreCase) ? "slowest" : "slow";
        return new EncodePlan
        {
            SourcePath = scan.SourcePath,
            OutputPath = output,
            EncoderId = encoder.Id,
            EncoderPreset = preset,
            Quality = CurrentQuality(),
            EnableNvdec = NvdecCheckBox.IsChecked == true,
            CropMode = crop.Id,
            CropTop = ParseNonNegative(CropTopTextBox.Text, "Top crop"),
            CropBottom = ParseNonNegative(CropBottomTextBox.Text, "Bottom crop"),
            CropLeft = ParseNonNegative(CropLeftTextBox.Text, "Left crop"),
            CropRight = ParseNonNegative(CropRightTextBox.Text, "Right crop"),
            ScaleMode = scale.Id,
            TargetWidth = width,
            TargetHeight = height,
            SourceWidth = scan.Width,
            SourceHeight = scan.Height,
            AudioTracks = audioTracks.Where(track => track.Selected).Select(track => track.Number).ToArray(),
            SubtitleTracks = subtitleTracks.Where(track => track.Selected).Select(track => track.Number).ToArray()
        };
    }

    private static int ParseNonNegative(string value, string label, bool allowZero = true)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed < 0 || (!allowZero && parsed == 0))
        {
            throw new InvalidOperationException(label + " must be " + (allowZero ? "zero or a positive whole number." : "a positive whole number."));
        }
        return parsed;
    }

    private void ApplyEngineProgress(EngineProgress progress)
    {
        latestPercent = progress.Percent;
        EngineStatusTextBlock.Text = progress.Phase;
        EnginePercentTextBlock.Text = progress.Percent.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        EngineProgressBar.Value = progress.Percent;
        if (progress.AverageFps > 0) AverageSpeedTextBlock.Text = progress.AverageFps.ToString("0.0", CultureInfo.InvariantCulture) + " fps";
        if (progress.Eta.HasValue)
        {
            etaAtUpdate = progress.Eta;
            etaUpdatedAt = DateTime.Now;
        }
        UpdateEtaDisplay();
    }

    private void ResetEngineProgress(string status)
    {
        latestPercent = 0;
        etaAtUpdate = null;
        EngineStatusTextBlock.Text = status;
        EnginePercentTextBlock.Text = "0.0%";
        EngineProgressBar.Value = 0;
        ElapsedTextBlock.Text = "00:00:00";
        EtaTextBlock.Text = "—";
        FinishTimeTextBlock.Text = "—";
        AverageSpeedTextBlock.Text = "—";
    }

    private void OperationTimer_Tick(object? sender, EventArgs e)
    {
        if (operationStarted == default) return;
        TimeSpan elapsed = DateTime.Now - operationStarted;
        ElapsedTextBlock.Text = elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        UpdateEtaDisplay();
    }

    private void UpdateEtaDisplay()
    {
        TimeSpan? remaining = null;
        if (etaAtUpdate.HasValue)
        {
            remaining = etaAtUpdate.Value - (DateTime.Now - etaUpdatedAt);
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        }
        else if (latestPercent > 0.5 && operationStarted != default)
        {
            TimeSpan elapsed = DateTime.Now - operationStarted;
            remaining = TimeSpan.FromSeconds(elapsed.TotalSeconds * (100.0 - latestPercent) / latestPercent);
        }

        if (!remaining.HasValue)
        {
            EtaTextBlock.Text = "Calculating…";
            FinishTimeTextBlock.Text = "Calculating…";
            return;
        }

        EtaTextBlock.Text = remaining.Value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        DateTime finish = DateTime.Now + remaining.Value;
        FinishTimeTextBlock.Text = finish.Date == DateTime.Today
            ? "Today " + finish.ToString("h:mm:ss tt", CultureInfo.CurrentCulture)
            : finish.Date == DateTime.Today.AddDays(1)
                ? "Tomorrow " + finish.ToString("h:mm:ss tt", CultureInfo.CurrentCulture)
                : finish.ToString("g", CultureInfo.CurrentCulture);
    }

    private async void VerifyOutputButton_Click(object sender, RoutedEventArgs e)
    {
        if (currentScan is null)
        {
            MessageBox.Show(this, "Load and scan the source first.", "Transcencode", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string output = OutputPathTextBox.Text.Trim();
        if (!File.Exists(output))
        {
            MessageBox.Show(this, "The output file does not exist yet.", "Transcencode", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!TryBeginOperation("Verifying encoded output")) return;

        try
        {
            MainTabs.SelectedItem = VerifyTab;
            VerifyProgressBar.Value = 0;
            var progress = new Progress<int>(value => VerifyProgressBar.Value = value);
            VerificationResult result = await verificationService.VerifyAsync(
                currentScan,
                output,
                progress,
                operationCancellation!.Token);

            verificationItems.Clear();
            foreach (VerificationItem item in result.Items) verificationItems.Add(item);
            VerifySummaryTextBlock.Text = result.Passed
                ? $"Verification completed without a structural failure. Average sampled visual similarity: {result.AverageSimilarity:0.0}%."
                : "Verification found at least one failure. Review the table below.";
            StatusTextBlock.Text = result.Passed ? "Output verification complete." : "Output verification found a failure.";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Verification canceled.";
        }
        catch (Exception ex)
        {
            ShowOperationError("Verification failed", ex);
        }
        finally
        {
            EndOperation();
        }
    }

    private void UpdateHdrGuidance()
    {
        if (HdrCompatibilityTextBlock is null) return;
        if (currentScan is null)
        {
            HdrCompatibilityTextBlock.Text = "Load a source to see HDR compatibility guidance.";
            return;
        }

        string encoder = SelectedOption(EncoderComboBox)?.Id ?? string.Empty;
        if (currentScan.IsDolbyVision)
        {
            HdrCompatibilityTextBlock.Text = encoder.StartsWith("nvenc_", StringComparison.OrdinalIgnoreCase)
                ? "Dolby Vision was detected. NVENC remains selectable, but full dynamic-metadata preservation is not guaranteed; expect to verify whether the output retains only an HDR-compatible base layer."
                : "Dolby Vision was detected. A 10-bit software encoder is selected, but metadata preservation still must be verified after encoding.";
        }
        else if (currentScan.IsHdr10Plus)
        {
            HdrCompatibilityTextBlock.Text = "HDR10+ was detected. Use a 10-bit encoder and verify the encoded output; dynamic metadata handling depends on the selected path and container.";
        }
        else if (currentScan.IsHdr)
        {
            HdrCompatibilityTextBlock.Text = "HDR was detected. H.265 10-bit is the safer starting choice. Verify the output after encoding.";
        }
        else
        {
            HdrCompatibilityTextBlock.Text = "The source scan did not report HDR. Standard NVENC and software options remain available.";
        }
    }

    private void InterfaceScaleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializing) return;
        OptionItem? option = SelectedOption(InterfaceScaleComboBox);
        if (option is null || !double.TryParse(option.Id, NumberStyles.Float, CultureInfo.InvariantCulture, out double factor)) return;
        settings.InterfaceScale = Math.Clamp(factor, 1.0, 2.0);
        ApplyInterfaceScale(settings.InterfaceScale);
        settingsService.Save(settings);
    }

    private void ApplyInterfaceScale(double factor)
    {
        factor = Math.Clamp(factor, 1.0, 2.0);
        InterfaceScaleTransform.ScaleX = factor;
        InterfaceScaleTransform.ScaleY = factor;
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        AppPaths.EnsureDirectories();
        OpenExplorer(AppPaths.LogDirectory);
    }

    private void OpenAppFolderButton_Click(object sender, RoutedEventArgs e) => OpenExplorer(AppContext.BaseDirectory);

    private static void OpenExplorer(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            ArgumentList = { path },
            UseShellExecute = true
        });
    }

    private void OpenLiveEngineButton_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedItem = LiveEngineTab;

    private void ClearConsoleButton_Click(object sender, RoutedEventArgs e) => EngineConsoleTextBox.Clear();

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        operationCancellation?.Cancel();
        cliService.CancelActiveOperation();
        StatusTextBlock.Text = "Cancel requested…";
    }

    private void AppendConsole(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AppendConsole(text));
            return;
        }

        EngineConsoleTextBox.AppendText(text);
        const int maximumCharacters = 750_000;
        if (EngineConsoleTextBox.Text.Length > maximumCharacters)
        {
            EngineConsoleTextBox.Text = "[Older console lines trimmed from this view; full logs remain on disk.]\r\n" +
                                        EngineConsoleTextBox.Text[^600_000..];
        }
        if (FollowOutputCheckBox.IsChecked == true) EngineConsoleTextBox.ScrollToEnd();
    }

    private bool TryBeginOperation(string status)
    {
        if (busy)
        {
            MessageBox.Show(this, "Another Transcencode operation is already running.", "Transcencode", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        busy = true;
        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        StatusTextBlock.Text = status + "…";
        CancelButton.IsEnabled = true;
        EncodeNowButton.IsEnabled = false;
        BrowseSourceButton.IsEnabled = false;
        ScanButton.IsEnabled = false;
        DeepAnalyzeButton.IsEnabled = false;
        VerifyOutputButton.IsEnabled = false;
        return true;
    }

    private void EndOperation()
    {
        busy = false;
        CancelButton.IsEnabled = false;
        EncodeNowButton.IsEnabled = true;
        BrowseSourceButton.IsEnabled = true;
        ScanButton.IsEnabled = true;
        DeepAnalyzeButton.IsEnabled = true;
        VerifyOutputButton.IsEnabled = true;
        operationCancellation?.Dispose();
        operationCancellation = null;
    }

    private void ShowOperationError(string heading, Exception ex)
    {
        string path = CrashReporter.Write(heading, ex);
        StatusTextBlock.Text = heading + ".";
        EngineStatusTextBlock.Text = "Failed";
        AppendConsole($"\r\n=== {heading.ToUpperInvariant()} ===\r\n{ex}\r\nDiagnostic log: {path}\r\n");
        MessageBox.Show(
            this,
            heading + ".\r\n\r\n" + ex.Message + "\r\n\r\nDiagnostic log:\r\n" + path,
            "Transcencode",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static OptionItem? SelectedOption(ComboBox comboBox) => comboBox.SelectedItem as OptionItem;

    private static void SelectOption(ComboBox comboBox, string id, string fallbackId)
    {
        if (comboBox.ItemsSource is not IEnumerable<OptionItem> options) return;
        OptionItem? selected = options.FirstOrDefault(option => option.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                               ?? options.FirstOrDefault(option => option.Id.Equals(fallbackId, StringComparison.OrdinalIgnoreCase))
                               ?? options.FirstOrDefault();
        comboBox.SelectedItem = selected;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (busy)
        {
            MessageBoxResult answer = MessageBox.Show(
                this,
                "An operation is still running. Cancel it and close Transcencode?",
                "Close Transcencode",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            operationCancellation?.Cancel();
            cliService.CancelActiveOperation();
        }

        settings.EncoderId = SelectedOption(EncoderComboBox)?.Id ?? settings.EncoderId;
        settings.QualityTarget = SelectedOption(QualityTargetComboBox)?.Id ?? settings.QualityTarget;
        settings.CustomQuality = QualityMapping.SliderToQuality(QualitySlider.Value);
        settings.CropMode = SelectedOption(CropModeComboBox)?.Id ?? settings.CropMode;
        settings.EnableNvdec = NvdecCheckBox.IsChecked == true;
        settingsService.Save(settings);
        operationTimer.Stop();
        cliService.Dispose();
    }
}
