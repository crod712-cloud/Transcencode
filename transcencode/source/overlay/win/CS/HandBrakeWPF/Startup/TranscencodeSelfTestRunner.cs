// --------------------------------------------------------------------------------------------------------------------
// Transcencode native in-process Windows smoke-test runner.
// This file is part of the Transcencode HandBrake fork and is licensed under GPL-2.0-or-later.
// --------------------------------------------------------------------------------------------------------------------

namespace HandBrakeWPF.Startup
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;
    using System.Windows.Threading;

    using HandBrakeWPF.Converters.Picture;
    using HandBrakeWPF.Helpers;
    using HandBrakeWPF.Model.Picture;
    using HandBrakeWPF.Model.Video;
    using HandBrakeWPF.ViewModels;
    using HandBrakeWPF.ViewModels.Interfaces;
    using HandBrakeWPF.Views;

    /// <summary>
    /// Runs a deterministic, in-process test against the actual native WPF application.
    /// It is only activated through the hidden --transcencode-self-test-report command-line switch.
    /// </summary>
    public static class TranscencodeSelfTestRunner
    {
        private const string SuccessMarker = "TRANSCENCODE_NATIVE_SELF_TEST_PASSED";

        public static void Schedule(string reportPath)
        {
            if (string.IsNullOrWhiteSpace(reportPath) || Application.Current == null)
            {
                return;
            }

            Application.Current.Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(async () => await RunAndExitAsync(reportPath)));
        }

        private static async Task RunAndExitAsync(string reportPath)
        {
            StringBuilder report = new StringBuilder();
            int exitCode = 1;

            try
            {
                report.AppendLine("Transcencode native WPF in-process self-test");
                report.AppendLine("Started: " + DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                report.AppendLine();

                Application application = Application.Current
                    ?? throw new InvalidOperationException("WPF Application.Current was unavailable.");

                await WaitUntilAsync(
                    () => application.MainWindow is ShellView && application.MainWindow.IsLoaded,
                    TimeSpan.FromSeconds(45),
                    "The native ShellView did not finish loading.");

                ShellView shell = (ShellView)application.MainWindow;
                Assert(
                    shell.Title?.IndexOf("Transcencode", StringComparison.OrdinalIgnoreCase) >= 0,
                    "The native main window title is not branded Transcencode.");
                Pass(report, "Native ShellView loaded and is branded Transcencode.");

                MainViewModel main = IoCHelper.Get<IMainViewModel>() as MainViewModel
                    ?? throw new InvalidOperationException("The native MainViewModel was not available from HandBrake IoC.");

                await WaitUntilAsync(
                    () => main.SelectedTitle != null &&
                          main.ScannedSource?.Titles?.Count > 0 &&
                          main.CurrentTask != null &&
                          !string.IsNullOrWhiteSpace(main.CurrentTask.Source),
                    TimeSpan.FromSeconds(120),
                    "The command-line regression source did not finish scanning.");

                Assert(File.Exists(main.CurrentTask.Source), "The loaded source path does not exist.");
                Pass(
                    report,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Source scan completed: {0}x{1}, {2} audio tracks, {3} subtitle tracks.",
                        main.SelectedTitle.Resolution?.Width ?? 0,
                        main.SelectedTitle.Resolution?.Height ?? 0,
                        main.SelectedTitle.AudioTracks?.Count ?? 0,
                        main.SelectedTitle.Subtitles?.Count ?? 0));

                ValidateNativeTabs(shell, report);
                ValidateSourceTracks(main, report);
                await ValidateAnalyzeAsync(main, report);
                ValidateCropAndBlackBars(main, report);
                ValidateUpscale(main, report);
                await ValidateInterfaceScalingAsync(shell, report);
                ValidateNativeViewConstruction(report);

                report.AppendLine();
                report.AppendLine(SuccessMarker);
                report.AppendLine("Completed: " + DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                exitCode = 0;
            }
            catch (Exception exc)
            {
                report.AppendLine();
                report.AppendLine("TRANSCENCODE_NATIVE_SELF_TEST_FAILED");
                report.AppendLine(exc.ToString());
            }
            finally
            {
                try
                {
                    string fullReportPath = Path.GetFullPath(reportPath);
                    string reportDirectory = Path.GetDirectoryName(fullReportPath);
                    if (!string.IsNullOrWhiteSpace(reportDirectory))
                    {
                        Directory.CreateDirectory(reportDirectory);
                    }

                    File.WriteAllText(fullReportPath, report.ToString(), new UTF8Encoding(false));
                }
                catch
                {
                    exitCode = 1;
                }

                try
                {
                    Application.Current?.Shutdown(exitCode);
                }
                catch
                {
                    Environment.Exit(exitCode);
                }
            }
        }

        private static void ValidateNativeTabs(ShellView shell, StringBuilder report)
        {
            string[] expected =
            {
                "Summary",
                "Dimensions",
                "Filters",
                "Video",
                "Audio",
                "Subtitles",
                "Chapters",
                "Analyze",
                "Source Tracks",
                "Upscale & Enhance",
                "Verify",
                "Live Engine"
            };

            HashSet<string> headers = new HashSet<string>(StringComparer.Ordinal);
            foreach (TabControl tabControl in EnumerateDescendants<TabControl>(shell))
            {
                foreach (object item in tabControl.Items)
                {
                    if (item is TabItem tab)
                    {
                        string header = tab.Header?.ToString();
                        if (!string.IsNullOrWhiteSpace(header))
                        {
                            headers.Add(header);
                        }
                    }
                }
            }

            string[] missing = expected.Where(item => !headers.Contains(item)).ToArray();
            Assert(
                missing.Length == 0,
                "The native main window is missing tabs: " + string.Join(", ", missing) +
                ". Found: " + string.Join(", ", headers.OrderBy(item => item)));

            Pass(report, "Original HandBrake tabs and all five Transcencode tabs are present in the live WPF tree.");
        }

        private static void ValidateSourceTracks(MainViewModel main, StringBuilder report)
        {
            TranscencodeSourceTracksViewModel tracks =
                main.TranscencodeSourceTracksViewModel as TranscencodeSourceTracksViewModel
                ?? throw new InvalidOperationException("Source Tracks view-model was not wired into MainViewModel.");

            Assert(tracks.AudioTracks.Count == 2, "Expected exactly two source audio tracks.");
            Assert(tracks.SubtitleTracks.Count == 2, "Expected exactly two source subtitle tracks.");
            Assert(
                tracks.AudioTracks.Any(item => IsEnglish(item.Language, item.Code)),
                "English audio was not exposed in Source Tracks.");
            Assert(
                tracks.AudioTracks.Any(item => IsSpanish(item.Language, item.Code)),
                "Spanish audio was not exposed in Source Tracks.");
            Assert(
                tracks.SubtitleTracks.Any(item => IsEnglish(item.Language, item.Code)),
                "English subtitles were not exposed in Source Tracks.");
            Assert(
                tracks.SubtitleTracks.Any(item => IsSpanish(item.Language, item.Code)),
                "Spanish subtitles were not exposed in Source Tracks.");
            Assert(
                tracks.Summary.StartsWith("Source contains 2 audio tracks and 2 subtitle tracks.", StringComparison.Ordinal),
                "Source Tracks summary did not report the expected counts.");

            Pass(report, "Source Tracks exposes both English and Spanish audio/subtitle streams and correct counts.");
        }

        private static async Task ValidateAnalyzeAsync(MainViewModel main, StringBuilder report)
        {
            TranscencodeAnalyzeViewModel analyze =
                main.TranscencodeAnalyzeViewModel as TranscencodeAnalyzeViewModel
                ?? throw new InvalidOperationException("Analyze view-model was not wired into MainViewModel.");

            Assert(analyze.CanAnalyze, "Deep Analyze was unavailable after source scanning.");
            analyze.AnalyzeCommand.Execute(null);

            await WaitUntilAsync(
                () => !analyze.IsAnalyzing &&
                      (string.Equals(analyze.Status, "Deep Analyze complete.", StringComparison.Ordinal) ||
                       analyze.Status.StartsWith("Deep Analyze failed:", StringComparison.Ordinal) ||
                       analyze.Status.StartsWith("Deep Analyze could not", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(120),
                "Deep Analyze did not finish within 120 seconds.");

            Assert(
                string.Equals(analyze.Status, "Deep Analyze complete.", StringComparison.Ordinal),
                "Deep Analyze did not complete successfully: " + analyze.Status);
            Assert(analyze.HasRecommendation, "Deep Analyze completed without enabling its recommendation.");
            Assert(analyze.Samples.Count > 0, "Deep Analyze completed without recording any picture samples.");
            Assert(
                analyze.Recommendation.StartsWith("Recommended starting point for ", StringComparison.Ordinal),
                "Deep Analyze did not produce an encoder-aware recommendation.");

            Match qualityMatch = Regex.Match(analyze.Recommendation, @"Constant Quality\s+(\d+)");
            Assert(qualityMatch.Success, "Could not parse the recommended Constant Quality value.");
            int recommendedQuality = int.Parse(qualityMatch.Groups[1].Value, CultureInfo.InvariantCulture);

            analyze.ApplyRecommendationCommand.Execute(null);
            Assert(
                main.CurrentTask.VideoEncodeRateType == VideoEncodeRateType.ConstantQuality,
                "Analyze did not switch the real encode task to Constant Quality.");
            Assert(
                main.CurrentTask.Quality.HasValue &&
                Math.Abs(main.CurrentTask.Quality.Value - recommendedQuality) < 0.001,
                "Analyze did not apply its recommendation to the real HandBrake encode task.");

            VideoViewModel video = main.VideoViewModel as VideoViewModel
                ?? throw new InvalidOperationException("The native VideoViewModel was unavailable.");
            Assert(
                video.Task.Quality.HasValue &&
                Math.Abs(video.Task.Quality.Value - recommendedQuality) < 0.001,
                "The native Video tab did not receive the Analyze recommendation.");
            Assert(
                analyze.Status == string.Format(
                    CultureInfo.InvariantCulture,
                    "Applied Constant Quality {0} to HandBrake's Video tab.",
                    recommendedQuality),
                "Analyze did not confirm recommendation application.");

            Pass(
                report,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Deep Analyze inspected {0} representative pictures and applied Constant Quality {1} to the native Video model.",
                    analyze.Samples.Count,
                    recommendedQuality));
        }

        private static void ValidateCropAndBlackBars(MainViewModel main, StringBuilder report)
        {
            PictureSettingsViewModel picture = main.PictureSettingsViewModel as PictureSettingsViewModel
                ?? throw new InvalidOperationException("The native Dimensions/Picture view-model was unavailable.");

            CropMode[] expectedOrder =
            {
                CropMode.None,
                CropMode.Loose,
                CropMode.Automatic,
                CropMode.Custom
            };
            Assert(
                picture.CropModes.SequenceEqual(expectedOrder),
                "The black-bar modes are not ordered with Same as source first.");

            TranscencodeCropModeConverter converter = new TranscencodeCropModeConverter();
            object converted = converter.Convert(
                picture.CropModes,
                typeof(BindingList<string>),
                null,
                CultureInfo.InvariantCulture);
            IList labels = converted as IList
                ?? throw new InvalidOperationException("The plain-language black-bar converter did not return a list.");

            string[] expectedLabels =
            {
                "Same as source (preserve original black bars)",
                "Safe auto-crop (least aggressive)",
                "Automatic crop (remove detected black bars)",
                "Custom crop"
            };
            Assert(labels.Count == expectedLabels.Length, "The black-bar converter returned the wrong number of choices.");
            for (int index = 0; index < expectedLabels.Length; index++)
            {
                Assert(
                    string.Equals(labels[index]?.ToString(), expectedLabels[index], StringComparison.Ordinal),
                    "Unexpected black-bar label/order at position " + index + ".");
            }

            picture.SelectedCropMode = CropMode.None;
            Assert(
                main.CurrentTask.Cropping.Top == 0 &&
                main.CurrentTask.Cropping.Bottom == 0 &&
                main.CurrentTask.Cropping.Left == 0 &&
                main.CurrentTask.Cropping.Right == 0,
                "Same as source did not set all crop values to zero.");

            Pass(report, "Same as source is first, uses plain language, and preserves the complete frame with zero crop.");
        }

        private static void ValidateUpscale(MainViewModel main, StringBuilder report)
        {
            TranscencodeUpscaleViewModel upscale =
                main.TranscencodeUpscaleViewModel as TranscencodeUpscaleViewModel
                ?? throw new InvalidOperationException("Upscale & Enhance view-model was not wired into MainViewModel.");

            Assert(
                upscale.TargetChoices.Count >= 5 && upscale.TargetChoices[0] == "Same as source",
                "Upscale & Enhance does not begin with Same as source.");

            upscale.SelectedTarget = "1920 × 1080 (1080p)";
            upscale.ApplyCommand.Execute(null);
            Assert(main.CurrentTask.AllowUpscaling, "Selecting 1080p did not enable native HandBrake upscaling.");
            Assert(
                main.CurrentTask.MaxWidth == 1920 && main.CurrentTask.MaxHeight == 1080,
                "Selecting 1080p did not update the real HandBrake dimensions task.");

            upscale.KeepSourceCommand.Execute(null);
            Assert(!main.CurrentTask.AllowUpscaling, "Restoring source size left upscaling enabled.");
            Assert(
                main.CurrentTask.MaxWidth == main.SelectedTitle.Resolution.Width &&
                main.CurrentTask.MaxHeight == main.SelectedTitle.Resolution.Height,
                "Restoring source size did not restore the original dimensions.");

            Pass(report, "Upscale & Enhance modifies the real HandBrake task and can restore source dimensions safely.");
        }

        private static async Task ValidateInterfaceScalingAsync(ShellView shell, StringBuilder report)
        {
            ComboBox picker = shell.FindName("TranscencodeScalePicker") as ComboBox
                ?? throw new InvalidOperationException("The whole-interface size selector was not present in ShellView.");
            ScaleTransform transform = shell.FindName("TranscencodeScaleTransform") as ScaleTransform
                ?? throw new InvalidOperationException("The whole-interface ScaleTransform was not present in ShellView.");

            Assert(picker.Items.Count == 6, "The interface-size selector does not contain all six supported sizes.");
            picker.SelectedIndex = 2;
            await Task.Delay(500);
            Assert(
                Math.Abs(transform.ScaleX - 1.25) < 0.001 &&
                Math.Abs(transform.ScaleY - 1.25) < 0.001,
                "Selecting 125% did not scale the complete WPF interface.");

            string scaleFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Transcencode",
                "interface-scale.txt");
            Assert(File.Exists(scaleFile), "The 125% interface preference was not saved.");
            string saved = File.ReadAllText(scaleFile).Trim();
            Assert(saved == "1.25", "The saved interface factor was not 1.25.");

            picker.SelectedIndex = 0;
            await Task.Delay(250);
            Assert(
                Math.Abs(transform.ScaleX - 1.0) < 0.001 &&
                Math.Abs(transform.ScaleY - 1.0) < 0.001,
                "Resetting interface size did not restore 100%.");

            Pass(report, "Whole-interface scaling applies at 125%, persists, remains reachable, and resets to 100%.");
        }

        private static void ValidateNativeViewConstruction(StringBuilder report)
        {
            FrameworkElement[] views =
            {
                new TranscencodeAnalyzeView(),
                new TranscencodeSourceTracksView(),
                new TranscencodeUpscaleView(),
                new TranscencodeVerifyView(),
                new TranscencodeLiveEngineView()
            };

            Assert(views.All(view => view != null), "One or more native Transcencode WPF views could not be constructed.");
            Pass(report, "Analyze, Source Tracks, Upscale & Enhance, Verify, and Live Engine XAML views construct successfully.");
        }

        private static IEnumerable<T> EnumerateDescendants<T>(DependencyObject root)
            where T : DependencyObject
        {
            if (root == null)
            {
                yield break;
            }

            if (root is T typed)
            {
                yield return typed;
            }

            int visualCount = 0;
            try
            {
                visualCount = VisualTreeHelper.GetChildrenCount(root);
            }
            catch
            {
                visualCount = 0;
            }

            if (visualCount > 0)
            {
                for (int index = 0; index < visualCount; index++)
                {
                    DependencyObject child = VisualTreeHelper.GetChild(root, index);
                    foreach (T item in EnumerateDescendants<T>(child))
                    {
                        yield return item;
                    }
                }

                yield break;
            }

            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is DependencyObject dependencyObject)
                {
                    foreach (T item in EnumerateDescendants<T>(dependencyObject))
                    {
                        yield return item;
                    }
                }
            }
        }

        private static async Task WaitUntilAsync(
            Func<bool> predicate,
            TimeSpan timeout,
            string timeoutMessage)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (predicate())
                {
                    return;
                }

                await Task.Delay(200);
            }

            throw new TimeoutException(timeoutMessage);
        }

        private static bool IsEnglish(string language, string code)
        {
            return string.Equals(code, "eng", StringComparison.OrdinalIgnoreCase) ||
                   (!string.IsNullOrWhiteSpace(language) &&
                    language.IndexOf("English", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsSpanish(string language, string code)
        {
            return string.Equals(code, "spa", StringComparison.OrdinalIgnoreCase) ||
                   (!string.IsNullOrWhiteSpace(language) &&
                    (language.IndexOf("Spanish", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     language.IndexOf("Español", StringComparison.OrdinalIgnoreCase) >= 0));
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void Pass(StringBuilder report, string message)
        {
            report.AppendLine("PASS: " + message);
        }
    }
}
