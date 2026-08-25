// Transcencode additions to the HandBrake Windows GUI.
// Licensed under GPLv2 as part of the combined HandBrake build.

namespace HandBrakeWPF.ViewModels
{
    using System;
    using System.Collections.ObjectModel;
    using System.Diagnostics;
    using System.IO;
    using System.Text.Json;
    using System.Threading.Tasks;
    using System.Windows.Input;

    using HandBrakeWPF.Commands;
    using HandBrakeWPF.EventArgs;
    using HandBrakeWPF.Model.Transcencode;
    using HandBrakeWPF.Services.Encode.Model;
    using HandBrakeWPF.Services.Interfaces;
    using HandBrakeWPF.Services.Presets.Model;
    using HandBrakeWPF.Services.Scan.Model;
    using HandBrakeWPF.ViewModels.Interfaces;

    public sealed class TranscencodeVerifyViewModel : ViewModelBase, ITranscencodeVerifyViewModel
    {
        private EncodeTask task;
        private bool isVerifying;
        private string outputPath = "No output selected.";
        private string status = "Encode a file, then verify it.";

        public TranscencodeVerifyViewModel(IUserSettingService userSettingService)
            : base(userSettingService)
        {
            this.Results = new ObservableCollection<VerificationResultRow>();
            this.VerifyCommand = new SimpleRelayCommand<object>(_ => this.StartVerify());
        }

        public event EventHandler<TabStatusEventArgs> TabStatusChanged;

        public ObservableCollection<VerificationResultRow> Results { get; }

        public ICommand VerifyCommand { get; }

        public bool IsVerifying
        {
            get => this.isVerifying;
            private set
            {
                if (value == this.isVerifying)
                {
                    return;
                }

                this.isVerifying = value;
                this.NotifyOfPropertyChange(() => this.IsVerifying);
            }
        }

        public string OutputPath
        {
            get => this.outputPath;
            private set
            {
                if (value == this.outputPath)
                {
                    return;
                }

                this.outputPath = value;
                this.NotifyOfPropertyChange(() => this.OutputPath);
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

        public void SetSource(Source source, Title selectedTitle, Preset currentPreset, EncodeTask encodeTask)
        {
            this.task = encodeTask;
            this.UpdateOutputPath();
        }

        public void SetPreset(Preset preset, EncodeTask encodeTask)
        {
            this.task = encodeTask;
            this.UpdateOutputPath();
        }

        public void UpdateTask(EncodeTask encodeTask)
        {
            this.task = encodeTask;
            this.UpdateOutputPath();
        }

        public bool MatchesPreset(Preset preset) => true;

        private void StartVerify()
        {
            if (this.IsVerifying)
            {
                return;
            }

            _ = this.VerifyAsync();
        }

        private async Task VerifyAsync()
        {
            this.IsVerifying = true;
            this.Results.Clear();

            try
            {
                string path = this.task?.Destination;
                this.OutputPath = string.IsNullOrWhiteSpace(path) ? "No output selected." : path;

                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    this.AddResult("Output file", "REVIEW", "The destination file does not exist yet.");
                    this.Status = "Nothing was verified.";
                    return;
                }

                FileInfo file = new FileInfo(path);
                this.AddResult("Output file", file.Length > 0 ? "PASS" : "FAIL", string.Format("{0:N0} bytes", file.Length));

                string cli = FindCli();
                if (cli == null)
                {
                    this.AddResult(
                        "HandBrakeCLI",
                        "REVIEW",
                        "HandBrakeCLI.exe was not found next to Transcencode. The Transcencode build workflow packages it; developer builds must copy it into the application folder.");
                    this.Status = "Basic file check complete; media scan was not run.";
                    return;
                }

                this.Status = "Scanning the encoded output...";
                ProcessStartInfo start = new ProcessStartInfo
                {
                    FileName = cli,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                start.ArgumentList.Add("--scan");
                start.ArgumentList.Add("--json");
                start.ArgumentList.Add("-i");
                start.ArgumentList.Add(path);

                using Process process = new Process { StartInfo = start };
                process.Start();
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                string stdout = await stdoutTask;
                string stderr = await stderrTask;
                string combined = stdout + Environment.NewLine + stderr;

                this.AddResult(
                    "Readable media",
                    process.ExitCode == 0 ? "PASS" : "FAIL",
                    process.ExitCode == 0 ? "HandBrake completed an output scan." : string.Format("HandBrakeCLI exit code {0}.", process.ExitCode));

                if (process.ExitCode != 0)
                {
                    this.AddResult("Scan details", "REVIEW", TrimForDisplay(combined));
                    this.Status = "Verification found an output scan error.";
                    return;
                }

                string json = ExtractJson(combined);
                if (json == null)
                {
                    this.AddResult("Media details", "REVIEW", "The output scan completed, but Transcencode could not isolate HandBrake's JSON title data.");
                    this.Status = "Structural scan completed with limited details.";
                    return;
                }

                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("TitleList", out JsonElement titleList) || titleList.GetArrayLength() == 0)
                {
                    this.AddResult("Title data", "FAIL", "HandBrake returned no playable titles.");
                    this.Status = "Verification failed.";
                    return;
                }

                JsonElement title = titleList[0];
                this.AddGeometryResult(title);
                this.AddDurationResult(title);
                this.AddTrackResults(title);
                this.Status = "Structural verification complete. Visual quality comparison is a later verification stage.";
            }
            catch (Exception exc)
            {
                this.AddResult("Verification", "FAIL", exc.Message);
                this.Status = "Verification failed.";
            }
            finally
            {
                this.IsVerifying = false;
            }
        }

        private void AddGeometryResult(JsonElement title)
        {
            if (title.TryGetProperty("Geometry", out JsonElement geometry) &&
                geometry.TryGetProperty("Width", out JsonElement width) &&
                geometry.TryGetProperty("Height", out JsonElement height))
            {
                this.AddResult("Resolution", "PASS", string.Format("{0} × {1}", width.GetInt32(), height.GetInt32()));
            }
            else
            {
                this.AddResult("Resolution", "REVIEW", "Resolution was not present in the output scan data.");
            }
        }

        private void AddDurationResult(JsonElement title)
        {
            if (title.TryGetProperty("Duration", out JsonElement duration) &&
                duration.TryGetProperty("Hours", out JsonElement hours) &&
                duration.TryGetProperty("Minutes", out JsonElement minutes) &&
                duration.TryGetProperty("Seconds", out JsonElement seconds))
            {
                this.AddResult(
                    "Duration",
                    "PASS",
                    string.Format("{0:00}:{1:00}:{2:00}", hours.GetInt32(), minutes.GetInt32(), seconds.GetInt32()));
            }
            else
            {
                this.AddResult("Duration", "REVIEW", "Duration was not present in the output scan data.");
            }
        }

        private void AddTrackResults(JsonElement title)
        {
            int audio = title.TryGetProperty("AudioList", out JsonElement audioList) ? audioList.GetArrayLength() : 0;
            int subtitles = title.TryGetProperty("SubtitleList", out JsonElement subtitleList) ? subtitleList.GetArrayLength() : 0;
            this.AddResult("Audio tracks", audio > 0 ? "PASS" : "REVIEW", audio.ToString());
            this.AddResult("Subtitle tracks", "PASS", subtitles.ToString());
        }

        private void AddResult(string check, string result, string details)
        {
            this.Results.Add(new VerificationResultRow { Check = check, Result = result, Details = details });
        }

        private void UpdateOutputPath()
        {
            this.OutputPath = string.IsNullOrWhiteSpace(this.task?.Destination) ? "No output selected." : this.task.Destination;
        }

        private static string FindCli()
        {
            string besideApp = Path.Combine(AppContext.BaseDirectory, "HandBrakeCLI.exe");
            if (File.Exists(besideApp))
            {
                return besideApp;
            }

            return null;
        }

        private static string ExtractJson(string text)
        {
            int titleList = text.IndexOf("\"TitleList\"", StringComparison.Ordinal);
            if (titleList < 0)
            {
                return null;
            }

            int start = text.LastIndexOf('{', titleList);
            int end = text.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                return null;
            }

            return text.Substring(start, end - start + 1);
        }

        private static string TrimForDisplay(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "No scan details were returned.";
            }

            string compact = text.Replace(Environment.NewLine, " ").Trim();
            return compact.Length <= 400 ? compact : compact.Substring(0, 400) + "...";
        }
    }
}
