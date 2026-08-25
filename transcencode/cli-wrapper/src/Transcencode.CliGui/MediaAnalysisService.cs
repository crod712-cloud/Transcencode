using OpenCvSharp;

namespace Transcencode.CliGui;

public sealed class MediaAnalysisService
{
    public Task<AnalysisResult> AnalyzeAsync(
        string sourcePath,
        int requestedSamples,
        string encoderId,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => AnalyzeCore(sourcePath, requestedSamples, encoderId, progress, cancellationToken), cancellationToken);
    }

    public Task<(double Average, double Minimum, int Compared)> CompareAsync(
        string sourcePath,
        string outputPath,
        int requestedSamples,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => CompareCore(sourcePath, outputPath, requestedSamples, progress, cancellationToken), cancellationToken);
    }

    private static AnalysisResult AnalyzeCore(
        string sourcePath,
        int requestedSamples,
        string encoderId,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("The source file does not exist.", sourcePath);

        using var capture = new VideoCapture(sourcePath);
        if (!capture.IsOpened())
        {
            throw new InvalidOperationException(
                "The picture analyzer could not open this source. Encoding can still work through HandBrakeCLI, but Deep Analyze requires a format readable by the bundled OpenCV decoder.");
        }

        long frameCount = (long)Math.Round(capture.Get(VideoCaptureProperties.FrameCount));
        double fps = capture.Get(VideoCaptureProperties.Fps);
        if (frameCount <= 1)
        {
            throw new InvalidOperationException("The picture analyzer could not determine the source frame count.");
        }
        if (!double.IsFinite(fps) || fps <= 0) fps = 24;

        int sampleCount = Math.Clamp(requestedSamples, 8, 96);
        sampleCount = (int)Math.Min(sampleCount, Math.Max(1, frameCount - 1));
        var raw = new List<RawSample>(sampleCount);
        Mat? previousHistogram = null;

        try
        {
            for (int i = 0; i < sampleCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double fraction = (i + 1.0) / (sampleCount + 1.0);
                long frameIndex = Math.Clamp((long)Math.Round(fraction * (frameCount - 1)), 0, frameCount - 1);

                using Mat frame = ReadFrame(capture, frameIndex);
                using Mat gray = PrepareGray(frame);
                Cv2.MeanStdDev(gray, out Scalar mean, out Scalar standardDeviation);
                double brightness = mean.Val0;
                double contrast = standardDeviation.Val0;

                using var darkMask = new Mat();
                Cv2.Compare(gray, new Scalar(40), darkMask, CmpType.LT);
                double darkPercent = Cv2.CountNonZero(darkMask) * 100.0 / (gray.Rows * gray.Cols);

                using var laplacian = new Mat();
                Cv2.Laplacian(gray, laplacian, MatType.CV_64F);
                Cv2.MeanStdDev(laplacian, out _, out Scalar laplacianStdDev);
                double detail = Math.Min(250, laplacianStdDev.Val0);

                using Mat histogram = BuildHistogram(gray);
                double sceneChange = previousHistogram is null
                    ? 0
                    : Cv2.CompareHist(previousHistogram, histogram, HistCompMethods.Bhattacharyya) * 100.0;
                previousHistogram?.Dispose();
                previousHistogram = histogram.Clone();

                long motionFrameIndex = Math.Min(frameCount - 1, frameIndex + Math.Max(1, (long)Math.Round(fps / 3.0)));
                using Mat secondFrame = ReadFrame(capture, motionFrameIndex);
                using Mat secondGray = PrepareGray(secondFrame);
                using var motionDifference = new Mat();
                Cv2.Absdiff(gray, secondGray, motionDifference);
                double motion = Cv2.Mean(motionDifference).Val0;

                raw.Add(new RawSample(
                    i + 1,
                    TimeSpan.FromSeconds(frameIndex / fps),
                    brightness,
                    darkPercent,
                    contrast,
                    detail,
                    motion,
                    sceneChange));

                progress?.Report((int)Math.Round((i + 1) * 100.0 / sampleCount));
            }
        }
        finally
        {
            previousHistogram?.Dispose();
        }

        double minDetail = raw.Min(x => x.Detail);
        double maxDetail = raw.Max(x => x.Detail);
        double minMotion = raw.Min(x => x.Motion);
        double maxMotion = raw.Max(x => x.Motion);
        double minContrast = raw.Min(x => x.Contrast);
        double maxContrast = raw.Max(x => x.Contrast);

        List<AnalysisSample> samples = raw.Select(x =>
        {
            double normalizedDetail = Normalize(x.Detail, minDetail, maxDetail);
            double normalizedMotion = Normalize(x.Motion, minMotion, maxMotion);
            double normalizedContrast = Normalize(x.Contrast, minContrast, maxContrast);
            double shadowPressure = Math.Clamp(x.DarkPercent / 70.0, 0, 1);
            double difficulty = 100.0 * (
                0.28 * normalizedDetail +
                0.28 * normalizedMotion +
                0.14 * normalizedContrast +
                0.18 * shadowPressure +
                0.12 * Math.Clamp(x.SceneChange / 70.0, 0, 1));

            return new AnalysisSample
            {
                Number = x.Number,
                Timestamp = x.Timestamp,
                Brightness = x.Brightness,
                DarkPixelPercent = x.DarkPercent,
                Contrast = x.Contrast,
                Detail = x.Detail,
                Motion = x.Motion,
                SceneChange = x.SceneChange,
                Difficulty = difficulty
            };
        }).ToList();

        double averageBrightness = samples.Average(x => x.Brightness);
        double averageDark = samples.Average(x => x.DarkPixelPercent);
        double averageContrast = samples.Average(x => x.Contrast);
        double averageDetail = samples.Average(x => x.Detail);
        double averageMotion = samples.Average(x => x.Motion);
        double averageDifficulty = samples.Average(x => x.Difficulty);

        double baseQuality = encoderId.StartsWith("nvenc_", StringComparison.OrdinalIgnoreCase) ? 18 : 20;
        double adjustment = 0;
        if (averageDifficulty >= 68) adjustment -= 2.0;
        else if (averageDifficulty >= 52) adjustment -= 1.0;
        else if (averageDifficulty < 28) adjustment += 1.0;
        if (averageDark >= 45) adjustment -= 1.0;
        if (samples.Max(x => x.Difficulty) >= 85) adjustment -= 0.5;
        double recommendation = Math.Clamp(baseQuality + adjustment, 14, 24);

        List<AnalysisSample> hardest = samples
            .OrderByDescending(x => x.Difficulty)
            .ThenBy(x => x.Timestamp)
            .Take(Math.Min(20, samples.Count))
            .ToList();

        string explanation =
            $"Analyzed {samples.Count} representative points across the video. " +
            $"Average brightness {averageBrightness:0.0}/255, dark-pixel load {averageDark:0.0}%, " +
            $"motion {averageMotion:0.0}, and difficulty {averageDifficulty:0.0}/100. " +
            $"Recommended starting quality: CQ/RF {recommendation:0.0}. " +
            "Lower CQ/RF numbers generally use more bitrate and produce larger files. " +
            "This is representative content analysis, not an exhaustive every-frame proof; the hardest sampled timestamps are listed below for inspection.";

        return new AnalysisResult
        {
            Samples = hardest,
            AverageBrightness = averageBrightness,
            AverageDarkPixels = averageDark,
            AverageContrast = averageContrast,
            AverageDetail = averageDetail,
            AverageMotion = averageMotion,
            AverageDifficulty = averageDifficulty,
            RecommendedQuality = recommendation,
            Explanation = explanation
        };
    }

    private static (double Average, double Minimum, int Compared) CompareCore(
        string sourcePath,
        string outputPath,
        int requestedSamples,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        using var sourceCapture = new VideoCapture(sourcePath);
        using var outputCapture = new VideoCapture(outputPath);
        if (!sourceCapture.IsOpened()) throw new InvalidOperationException("Visual Verify could not open the source file.");
        if (!outputCapture.IsOpened()) throw new InvalidOperationException("Visual Verify could not open the encoded output.");

        long sourceFrames = (long)Math.Round(sourceCapture.Get(VideoCaptureProperties.FrameCount));
        long outputFrames = (long)Math.Round(outputCapture.Get(VideoCaptureProperties.FrameCount));
        if (sourceFrames <= 1 || outputFrames <= 1) throw new InvalidOperationException("Visual Verify could not determine frame counts.");

        int sampleCount = Math.Clamp(requestedSamples, 6, 30);
        var similarities = new List<double>(sampleCount);

        for (int i = 0; i < sampleCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double fraction = (i + 1.0) / (sampleCount + 1.0);
            long sourceIndex = Math.Clamp((long)Math.Round(fraction * (sourceFrames - 1)), 0, sourceFrames - 1);
            long outputIndex = Math.Clamp((long)Math.Round(fraction * (outputFrames - 1)), 0, outputFrames - 1);

            using Mat sourceFrame = ReadFrame(sourceCapture, sourceIndex);
            using Mat outputFrame = ReadFrame(outputCapture, outputIndex);
            using Mat sourceGray = PrepareGray(sourceFrame);
            using Mat outputGrayInitial = PrepareGray(outputFrame);
            using var outputGray = new Mat();
            if (sourceGray.Size() != outputGrayInitial.Size())
            {
                Cv2.Resize(outputGrayInitial, outputGray, sourceGray.Size(), 0, 0, InterpolationFlags.Area);
            }
            else
            {
                outputGrayInitial.CopyTo(outputGray);
            }

            using var difference = new Mat();
            Cv2.Absdiff(sourceGray, outputGray, difference);
            double meanAbsoluteError = Cv2.Mean(difference).Val0;
            double similarity = Math.Clamp(1.0 - meanAbsoluteError / 255.0, 0, 1) * 100.0;
            similarities.Add(similarity);
            progress?.Report((int)Math.Round((i + 1) * 100.0 / sampleCount));
        }

        return (similarities.Average(), similarities.Min(), similarities.Count);
    }

    private static Mat ReadFrame(VideoCapture capture, long frameIndex)
    {
        capture.Set(VideoCaptureProperties.PosFrames, frameIndex);
        var frame = new Mat();
        if (!capture.Read(frame) || frame.Empty())
        {
            frame.Dispose();
            throw new InvalidOperationException($"The picture decoder could not read frame {frameIndex}.");
        }
        return frame;
    }

    private static Mat PrepareGray(Mat frame)
    {
        const int maximumWidth = 640;
        using var resized = new Mat();
        if (frame.Width > maximumWidth)
        {
            int height = Math.Max(1, (int)Math.Round(frame.Height * maximumWidth / (double)frame.Width));
            Cv2.Resize(frame, resized, new OpenCvSharp.Size(maximumWidth, height), 0, 0, InterpolationFlags.Area);
        }
        else
        {
            frame.CopyTo(resized);
        }

        var gray = new Mat();
        if (resized.Channels() == 1) resized.CopyTo(gray);
        else Cv2.CvtColor(resized, gray, ColorConversionCodes.BGR2GRAY);
        return gray;
    }

    private static Mat BuildHistogram(Mat gray)
    {
        var histogram = new Mat();
        Cv2.CalcHist([gray], [0], null, histogram, 1, [64], [new Rangef(0, 256)]);
        Cv2.Normalize(histogram, histogram, 1, 0, NormTypes.L1);
        return histogram;
    }

    private static double Normalize(double value, double minimum, double maximum)
    {
        if (maximum - minimum < 0.000001) return 0.5;
        return Math.Clamp((value - minimum) / (maximum - minimum), 0, 1);
    }

    private sealed record RawSample(
        int Number,
        TimeSpan Timestamp,
        double Brightness,
        double DarkPercent,
        double Contrast,
        double Detail,
        double Motion,
        double SceneChange);
}

public sealed class VerificationService
{
    private readonly HandBrakeCliService cli;
    private readonly MediaAnalysisService analysis;

    public VerificationService(HandBrakeCliService cli, MediaAnalysisService analysis)
    {
        this.cli = cli;
        this.analysis = analysis;
    }

    public async Task<VerificationResult> VerifyAsync(
        ScanResult source,
        string outputPath,
        IProgress<int>? visualProgress = null,
        CancellationToken cancellationToken = default)
    {
        var items = new List<VerificationItem>();
        if (!File.Exists(outputPath))
        {
            return new VerificationResult
            {
                Passed = false,
                Items = [new VerificationItem { Check = "Output file", Status = "FAIL", Details = "The encoded output does not exist." }]
            };
        }

        long size = new FileInfo(outputPath).Length;
        items.Add(new VerificationItem
        {
            Check = "Output file",
            Status = size > 1024 ? "PASS" : "FAIL",
            Details = $"{size:N0} bytes"
        });

        ScanResult output = await cli.ScanAsync(outputPath, null, cancellationToken).ConfigureAwait(false);
        items.Add(new VerificationItem
        {
            Check = "Readable media",
            Status = output.Width > 0 && output.Height > 0 ? "PASS" : "FAIL",
            Details = $"{output.Width} × {output.Height}; {output.Duration:hh\\:mm\\:ss}"
        });

        double durationDifference = Math.Abs((output.Duration - source.Duration).TotalSeconds);
        double allowedDurationDifference = Math.Max(2.0, source.Duration.TotalSeconds * 0.01);
        items.Add(new VerificationItem
        {
            Check = "Duration",
            Status = durationDifference <= allowedDurationDifference ? "PASS" : "WARN",
            Details = $"Source {source.Duration:hh\\:mm\\:ss}; output {output.Duration:hh\\:mm\\:ss}; difference {durationDifference:0.00}s"
        });

        items.Add(new VerificationItem
        {
            Check = "Audio tracks",
            Status = output.AudioTracks.Count > 0 ? "PASS" : "WARN",
            Details = $"Source {source.AudioTracks.Count}; output {output.AudioTracks.Count}"
        });
        items.Add(new VerificationItem
        {
            Check = "Subtitle tracks",
            Status = "INFO",
            Details = $"Source {source.SubtitleTracks.Count}; output {output.SubtitleTracks.Count}"
        });

        double averageSimilarity = 0;
        double minimumSimilarity = 0;
        try
        {
            (averageSimilarity, minimumSimilarity, int compared) = await analysis
                .CompareAsync(source.SourcePath, outputPath, 12, visualProgress, cancellationToken)
                .ConfigureAwait(false);
            string visualStatus = averageSimilarity >= 92 && minimumSimilarity >= 85 ? "PASS" : "REVIEW";
            items.Add(new VerificationItem
            {
                Check = "Sampled visual similarity",
                Status = visualStatus,
                Details = $"{compared} points; average {averageSimilarity:0.0}%; minimum {minimumSimilarity:0.0}%. Cropping, scaling, and intentional filtering can lower this value."
            });
        }
        catch (Exception ex)
        {
            items.Add(new VerificationItem
            {
                Check = "Sampled visual similarity",
                Status = "UNAVAILABLE",
                Details = ex.Message
            });
        }

        bool passed = items.All(item => item.Status is not "FAIL");
        return new VerificationResult
        {
            Passed = passed,
            AverageSimilarity = averageSimilarity,
            MinimumSimilarity = minimumSimilarity,
            Items = items
        };
    }
}
