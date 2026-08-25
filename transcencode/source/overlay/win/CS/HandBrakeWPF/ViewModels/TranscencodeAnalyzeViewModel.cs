// Transcencode additions to the HandBrake Windows GUI.
// Licensed under GPLv2 as part of the combined HandBrake build.

namespace HandBrakeWPF.ViewModels
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Linq;
    using System.Threading.Tasks;
    using System.Windows.Input;
    using System.Windows.Media;
    using System.Windows.Media.Imaging;

    using HandBrakeWPF.Commands;
    using HandBrakeWPF.EventArgs;
    using HandBrakeWPF.Model;
    using HandBrakeWPF.Model.Transcencode;
    using HandBrakeWPF.Model.Video;
    using HandBrakeWPF.Services.Encode.Model;
    using HandBrakeWPF.Services.Interfaces;
    using HandBrakeWPF.Services.Presets.Model;
    using HandBrakeWPF.Services.Scan.Interfaces;
    using HandBrakeWPF.Services.Scan.Model;
    using HandBrakeWPF.ViewModels.Interfaces;

    public sealed class TranscencodeAnalyzeViewModel : ViewModelBase, ITranscencodeAnalyzeViewModel
    {
        private readonly IScan scanService;
        private readonly IUserSettingService userSettingService;
        private readonly IVideoViewModel videoViewModel;

        private Title currentTitle;
        private EncodeTask task;
        private bool isAnalyzing;
        private bool hasRecommendation;
        private int recommendedQuality;
        private string status = "Open a source, then run Deep Analyze.";
        private string coverage = "No picture-content analysis has been run.";
        private string findings = string.Empty;
        private string recommendation = string.Empty;

        public TranscencodeAnalyzeViewModel(
            IScan scanService,
            IUserSettingService userSettingService,
            IVideoViewModel videoViewModel)
            : base(userSettingService)
        {
            this.scanService = scanService;
            this.userSettingService = userSettingService;
            this.videoViewModel = videoViewModel;
            this.Samples = new ObservableCollection<AnalysisSampleRow>();
            this.AnalyzeCommand = new SimpleRelayCommand<object>(_ => this.StartAnalysis());
            this.ApplyRecommendationCommand = new SimpleRelayCommand<object>(_ => this.ApplyRecommendation());
        }

        public event EventHandler<TabStatusEventArgs> TabStatusChanged;

        public ObservableCollection<AnalysisSampleRow> Samples { get; }

        public ICommand AnalyzeCommand { get; }

        public ICommand ApplyRecommendationCommand { get; }

        public bool IsAnalyzing
        {
            get => this.isAnalyzing;
            private set
            {
                if (value == this.isAnalyzing)
                {
                    return;
                }

                this.isAnalyzing = value;
                this.NotifyOfPropertyChange(() => this.IsAnalyzing);
                this.NotifyOfPropertyChange(() => this.CanAnalyze);
            }
        }

        public bool CanAnalyze => !this.IsAnalyzing && this.currentTitle != null && this.task != null;

        public bool HasRecommendation
        {
            get => this.hasRecommendation;
            private set
            {
                if (value == this.hasRecommendation)
                {
                    return;
                }

                this.hasRecommendation = value;
                this.NotifyOfPropertyChange(() => this.HasRecommendation);
            }
        }

        public string Status
        {
            get => this.status;
            private set
            {
                if (value == this.status)
                {
                    return;
                }

                this.status = value;
                this.NotifyOfPropertyChange(() => this.Status);
            }
        }

        public string Coverage
        {
            get => this.coverage;
            private set
            {
                if (value == this.coverage)
                {
                    return;
                }

                this.coverage = value;
                this.NotifyOfPropertyChange(() => this.Coverage);
            }
        }

        public string Findings
        {
            get => this.findings;
            private set
            {
                if (value == this.findings)
                {
                    return;
                }

                this.findings = value;
                this.NotifyOfPropertyChange(() => this.Findings);
            }
        }

        public string Recommendation
        {
            get => this.recommendation;
            private set
            {
                if (value == this.recommendation)
                {
                    return;
                }

                this.recommendation = value;
                this.NotifyOfPropertyChange(() => this.Recommendation);
            }
        }

        public void SetSource(Source source, Title selectedTitle, Preset currentPreset, EncodeTask encodeTask)
        {
            this.currentTitle = selectedTitle;
            this.task = encodeTask;
            this.Samples.Clear();
            this.HasRecommendation = false;
            this.Findings = string.Empty;
            this.Recommendation = string.Empty;

            if (selectedTitle == null)
            {
                this.Status = "Open a source, then run Deep Analyze.";
                this.Coverage = "No source loaded.";
            }
            else
            {
                this.Status = "Ready to inspect the source's representative picture samples.";
                this.Coverage = string.Format(
                    "HandBrake retained up to {0} preview samples across {1}. Deep Analyze measures brightness, shadow load, contrast, edge detail, and scene-to-scene variation in those pictures.",
                    this.GetPreviewCount(),
                    FormatTime(selectedTitle.Duration));
            }

            this.NotifyOfPropertyChange(() => this.CanAnalyze);
        }

        public void SetPreset(Preset preset, EncodeTask encodeTask)
        {
            this.task = encodeTask;
            this.NotifyOfPropertyChange(() => this.CanAnalyze);
        }

        public void UpdateTask(EncodeTask encodeTask)
        {
            this.task = encodeTask;
            this.NotifyOfPropertyChange(() => this.CanAnalyze);
        }

        public bool MatchesPreset(Preset preset) => true;

        private void StartAnalysis()
        {
            if (!this.CanAnalyze)
            {
                this.Status = "Load a valid source before running Deep Analyze.";
                return;
            }

            _ = this.AnalyzeAsync();
        }

        private async Task AnalyzeAsync()
        {
            this.IsAnalyzing = true;
            this.HasRecommendation = false;
            this.Samples.Clear();
            this.Findings = string.Empty;
            this.Recommendation = string.Empty;

            try
            {
                int requested = this.GetPreviewCount();
                List<FrameMetrics> metrics = new List<FrameMetrics>();
                double[] previousHistogram = null;

                for (int index = 0; index < requested; index++)
                {
                    this.Status = string.Format("Analyzing picture sample {0} of {1}...", index + 1, requested);

                    BitmapSource image = this.scanService.GetPreview(this.task, index, false);
                    if (image == null)
                    {
                        continue;
                    }

                    if (image.CanFreeze && !image.IsFrozen)
                    {
                        image.Freeze();
                    }

                    FrameMetrics frame = await Task.Run(
                        () => AnalyzeBitmap(image, index, requested, this.currentTitle.Duration, previousHistogram));
                    previousHistogram = frame.Histogram;
                    metrics.Add(frame);

                    this.Samples.Add(
                        new AnalysisSampleRow
                        {
                            Sample = index + 1,
                            ApproximateTime = frame.ApproximateTime,
                            Brightness = Percent(frame.Brightness),
                            DarkPixels = Percent(frame.DarkFraction),
                            Contrast = Percent(frame.Contrast),
                            Detail = Percent(frame.Detail),
                            SceneVariation = Percent(frame.SceneVariation),
                            Difficulty = frame.DifficultyLabel
                        });
                }

                if (metrics.Count == 0)
                {
                    this.Status = "Deep Analyze could not read any preview pictures.";
                    this.Coverage = "No encoding settings were changed.";
                    return;
                }

                double averageBrightness = metrics.Average(item => item.Brightness);
                double averageDark = metrics.Average(item => item.DarkFraction);
                double averageDetail = metrics.Average(item => item.Detail);
                double averageContrast = metrics.Average(item => item.Contrast);
                double averageDifficulty = metrics.Average(item => item.DifficultyScore);
                FrameMetrics hardest = metrics.OrderByDescending(item => item.DifficultyScore).First();
                int shadowHeavy = metrics.Count(item => item.DarkFraction >= 0.35 || item.Brightness <= 0.30);
                int highDetail = metrics.Count(item => item.Detail >= 0.20);

                this.Coverage = string.Format(
                    "Analyzed {0} representative picture samples distributed across approximately {1}. This is picture-content analysis, not the ordinary metadata/source scan.",
                    metrics.Count,
                    FormatTime(this.currentTitle.Duration));

                this.Findings = string.Format(
                    "Average brightness {0}. Shadow-heavy samples {1} of {2}. Average contrast {3}. High-detail samples {4} of {2}. The most difficult sampled area was near {5} and rated {6}.",
                    Percent(averageBrightness),
                    shadowHeavy,
                    metrics.Count,
                    Percent(averageContrast),
                    highDetail,
                    hardest.ApproximateTime,
                    hardest.DifficultyLabel.ToLowerInvariant());

                this.BuildRecommendation(averageDifficulty, averageDark, averageDetail, hardest);
                this.Status = "Deep Analyze complete.";
                this.HasRecommendation = true;
            }
            catch (Exception exc)
            {
                this.Status = "Deep Analyze failed: " + exc.Message;
                this.Coverage = "No encoding settings were changed.";
            }
            finally
            {
                this.IsAnalyzing = false;
            }
        }

        private void BuildRecommendation(double averageDifficulty, double averageDark, double averageDetail, FrameMetrics hardest)
        {
            bool nvenc = this.task.VideoEncoder?.IsNVEnc == true;
            bool x265 = this.task.VideoEncoder?.IsX265 == true;

            if (nvenc)
            {
                this.recommendedQuality = averageDifficulty >= 0.62 || averageDark >= 0.42 || hardest.DifficultyScore >= 0.82
                    ? 15
                    : averageDifficulty >= 0.46 || averageDetail >= 0.22
                        ? 17
                        : 19;
            }
            else if (x265)
            {
                this.recommendedQuality = averageDifficulty >= 0.62 || averageDark >= 0.42 || hardest.DifficultyScore >= 0.82
                    ? 17
                    : averageDifficulty >= 0.46 || averageDetail >= 0.22
                        ? 19
                        : 21;
            }
            else
            {
                this.recommendedQuality = averageDifficulty >= 0.55 ? 18 : 20;
            }

            string encoderName = this.task.VideoEncoder?.DisplayName ?? "the selected encoder";
            string qualifier = nvenc
                ? "NVENC will vary bitrate automatically from frame to frame. This is a high-fidelity starting point, not mathematically lossless and not yet verified by test encodes."
                : "This is a source-specific starting point, not a guarantee of visual losslessness.";

            this.Recommendation = string.Format(
                "Recommended starting point for {0}: Constant Quality {1}. {2}",
                encoderName,
                this.recommendedQuality,
                qualifier);
        }

        private void ApplyRecommendation()
        {
            if (!this.HasRecommendation || this.task == null)
            {
                return;
            }

            this.task.VideoEncodeRateType = VideoEncodeRateType.ConstantQuality;
            this.task.Quality = this.recommendedQuality;
            this.videoViewModel.RefreshTask();
            this.TabStatusChanged?.Invoke(this, new TabStatusEventArgs("TranscencodeAnalyze", ChangedOption.Quality));
            this.Status = string.Format("Applied Constant Quality {0} to HandBrake's Video tab.", this.recommendedQuality);
        }

        private int GetPreviewCount()
        {
            int count = this.userSettingService.GetUserSetting<int>(UserSettingConstants.PreviewScanCount);
            return Math.Max(1, count);
        }

        private static string Percent(double value)
        {
            return string.Format("{0:N0}%", Math.Max(0, Math.Min(1, value)) * 100);
        }

        private static string FormatTime(TimeSpan value)
        {
            return value.Days >= 1
                ? string.Format("{0:d\\:hh\\:mm\\:ss}", value)
                : string.Format("{0:hh\\:mm\\:ss}", value);
        }

        private static FrameMetrics AnalyzeBitmap(
            BitmapSource source,
            int index,
            int previewCount,
            TimeSpan duration,
            double[] previousHistogram)
        {
            BitmapSource converted = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            if (converted.CanFreeze && !converted.IsFrozen)
            {
                converted.Freeze();
            }

            int width = converted.PixelWidth;
            int height = converted.PixelHeight;
            int stride = width * 4;
            byte[] pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);

            int step = Math.Max(1, Math.Max(width / 320, height / 180));
            long sampleCount = 0;
            double sum = 0;
            double sumSquared = 0;
            long dark = 0;
            long highlights = 0;
            double detailSum = 0;
            long detailCount = 0;
            double[] histogram = new double[16];

            for (int y = 0; y < height; y += step)
            {
                int row = y * stride;
                for (int x = 0; x < width; x += step)
                {
                    int offset = row + (x * 4);
                    double luma = Luma(pixels[offset], pixels[offset + 1], pixels[offset + 2]);

                    sampleCount++;
                    sum += luma;
                    sumSquared += luma * luma;
                    if (luma < 48)
                    {
                        dark++;
                    }
                    if (luma > 235)
                    {
                        highlights++;
                    }

                    histogram[Math.Min(15, (int)(luma / 16.0))]++;

                    if (x + step < width)
                    {
                        int right = row + ((x + step) * 4);
                        detailSum += Math.Abs(luma - Luma(pixels[right], pixels[right + 1], pixels[right + 2]));
                        detailCount++;
                    }

                    if (y + step < height)
                    {
                        int down = ((y + step) * stride) + (x * 4);
                        detailSum += Math.Abs(luma - Luma(pixels[down], pixels[down + 1], pixels[down + 2]));
                        detailCount++;
                    }
                }
            }

            if (sampleCount == 0)
            {
                throw new InvalidOperationException("The preview picture contained no pixels.");
            }

            for (int i = 0; i < histogram.Length; i++)
            {
                histogram[i] /= sampleCount;
            }

            double mean = sum / sampleCount;
            double variance = Math.Max(0, (sumSquared / sampleCount) - (mean * mean));
            double brightness = mean / 255.0;
            double contrast = Math.Min(1, Math.Sqrt(variance) / 80.0);
            double darkFraction = dark / (double)sampleCount;
            double highlightFraction = highlights / (double)sampleCount;
            double detail = detailCount == 0 ? 0 : Math.Min(1, (detailSum / detailCount) / 60.0);
            double sceneVariation = 0;

            if (previousHistogram != null && previousHistogram.Length == histogram.Length)
            {
                for (int i = 0; i < histogram.Length; i++)
                {
                    sceneVariation += Math.Abs(histogram[i] - previousHistogram[i]);
                }

                sceneVariation = Math.Min(1, sceneVariation / 1.4);
            }

            double shadowDifficulty = Math.Min(1, darkFraction * 1.45);
            double difficulty = Math.Min(
                1,
                (detail * 0.38) +
                (contrast * 0.22) +
                (shadowDifficulty * 0.28) +
                (sceneVariation * 0.09) +
                (highlightFraction * 0.03));

            string difficultyLabel = difficulty >= 0.72
                ? "Very difficult"
                : difficulty >= 0.55
                    ? "Difficult"
                    : difficulty >= 0.38
                        ? "Moderate"
                        : "Easy";

            double fraction = (index + 1.0) / (previewCount + 1.0);
            TimeSpan approximateTime = TimeSpan.FromTicks((long)(duration.Ticks * fraction));

            return new FrameMetrics
            {
                Brightness = brightness,
                DarkFraction = darkFraction,
                Contrast = contrast,
                Detail = detail,
                SceneVariation = sceneVariation,
                DifficultyScore = difficulty,
                DifficultyLabel = difficultyLabel,
                Histogram = histogram,
                ApproximateTime = FormatTime(approximateTime)
            };
        }

        private static double Luma(byte blue, byte green, byte red)
        {
            return (0.0722 * blue) + (0.7152 * green) + (0.2126 * red);
        }

        private sealed class FrameMetrics
        {
            public double Brightness { get; set; }
            public double DarkFraction { get; set; }
            public double Contrast { get; set; }
            public double Detail { get; set; }
            public double SceneVariation { get; set; }
            public double DifficultyScore { get; set; }
            public string DifficultyLabel { get; set; }
            public double[] Histogram { get; set; }
            public string ApproximateTime { get; set; }
        }
    }
}
