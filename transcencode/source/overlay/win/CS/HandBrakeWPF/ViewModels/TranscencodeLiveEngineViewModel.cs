// Transcencode additions to the HandBrake Windows GUI.
// Licensed under GPLv2 as part of the combined HandBrake build.

namespace HandBrakeWPF.ViewModels
{
    using System;
    using System.Linq;
    using System.Windows;
    using System.Windows.Input;

    using HandBrakeWPF.Commands;
    using HandBrakeWPF.EventArgs;
    using HandBrakeWPF.Services.Encode.Model;
    using HandBrakeWPF.Services.Interfaces;
    using HandBrakeWPF.Services.Logging.EventArgs;
    using HandBrakeWPF.Services.Logging.Interfaces;
    using HandBrakeWPF.Services.Presets.Model;
    using HandBrakeWPF.Services.Queue.Interfaces;
    using HandBrakeWPF.Services.Queue.Model;
    using HandBrakeWPF.Services.Scan.Model;
    using HandBrakeWPF.ViewModels.Interfaces;

    public sealed class TranscencodeLiveEngineViewModel : ViewModelBase, ITranscencodeLiveEngineViewModel
    {
        private const int MaximumLogCharacters = 600000;

        private readonly ILog logService;
        private readonly IQueueService queueService;
        private string logText = string.Empty;
        private string status = "Ready";
        private string eta = "--:--:--";
        private string estimatedFinish = "--";
        private string speed = "-- fps";
        private double progress;

        public TranscencodeLiveEngineViewModel(
            IUserSettingService userSettingService,
            ILog logService,
            IQueueService queueService)
            : base(userSettingService)
        {
            this.logService = logService;
            this.queueService = queueService;
            this.ClearCommand = new SimpleRelayCommand<object>(_ => this.Clear());
            this.RefreshCommand = new SimpleRelayCommand<object>(_ => this.Refresh());

            this.logService.MessageLogged += this.LogServiceMessageLogged;
            this.logService.LogReset += this.LogServiceLogReset;
            this.queueService.QueueJobStatusChanged += this.QueueServiceQueueJobStatusChanged;
            this.queueService.JobProcessingStarted += this.QueueServiceJobProcessingStarted;
            this.queueService.QueueCompleted += this.QueueServiceQueueCompleted;

            this.Refresh();
        }

        public event EventHandler<TabStatusEventArgs> TabStatusChanged;

        public ICommand ClearCommand { get; }

        public ICommand RefreshCommand { get; }

        public string LogText
        {
            get => this.logText;
            private set
            {
                if (value == this.logText)
                {
                    return;
                }

                this.logText = value;
                this.NotifyOfPropertyChange(() => this.LogText);
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

        public string Eta
        {
            get => this.eta;
            private set
            {
                if (value == this.eta)
                {
                    return;
                }

                this.eta = value;
                this.NotifyOfPropertyChange(() => this.Eta);
            }
        }

        public string EstimatedFinish
        {
            get => this.estimatedFinish;
            private set
            {
                if (value == this.estimatedFinish)
                {
                    return;
                }

                this.estimatedFinish = value;
                this.NotifyOfPropertyChange(() => this.EstimatedFinish);
            }
        }

        public string Speed
        {
            get => this.speed;
            private set
            {
                if (value == this.speed)
                {
                    return;
                }

                this.speed = value;
                this.NotifyOfPropertyChange(() => this.Speed);
            }
        }

        public double Progress
        {
            get => this.progress;
            private set
            {
                if (Math.Abs(value - this.progress) < 0.001)
                {
                    return;
                }

                this.progress = value;
                this.NotifyOfPropertyChange(() => this.Progress);
                this.NotifyOfPropertyChange(() => this.ProgressText);
            }
        }

        public string ProgressText => string.Format("{0:N1}%", this.Progress);

        public void SetSource(Source source, Title selectedTitle, Preset currentPreset, EncodeTask task)
        {
            this.Refresh();
        }

        public void SetPreset(Preset preset, EncodeTask task)
        {
        }

        public void UpdateTask(EncodeTask task)
        {
        }

        public bool MatchesPreset(Preset preset) => true;

        private void LogServiceMessageLogged(object sender, LogEventArgs e)
        {
            string content = e?.Log?.Content;
            if (string.IsNullOrEmpty(content))
            {
                return;
            }

            RunOnUi(
                () =>
                {
                    string combined = this.LogText + content;
                    if (!combined.EndsWith(Environment.NewLine, StringComparison.Ordinal))
                    {
                        combined += Environment.NewLine;
                    }

                    if (combined.Length > MaximumLogCharacters)
                    {
                        combined = combined.Substring(combined.Length - MaximumLogCharacters);
                    }

                    this.LogText = combined;
                });
        }

        private void LogServiceLogReset(object sender, EventArgs e)
        {
            RunOnUi(this.Refresh);
        }

        private void QueueServiceJobProcessingStarted(object sender, QueueProgressEventArgs e)
        {
            RunOnUi(
                () =>
                {
                    this.Status = "Preparing encode";
                    this.Progress = 0;
                    this.Eta = "Calculating...";
                    this.EstimatedFinish = "Calculating...";
                    this.Speed = "-- fps";
                });
        }

        private void QueueServiceQueueCompleted(object sender, QueueCompletedEventArgs e)
        {
            RunOnUi(
                () =>
                {
                    this.Status = "Queue complete";
                    this.Progress = 100;
                    this.Eta = "00:00:00";
                    this.EstimatedFinish = DateTime.Now.ToString("g");
                    this.Speed = "-- fps";
                });
        }

        private void QueueServiceQueueJobStatusChanged(object sender, EventArgs e)
        {
            RunOnUi(
                () =>
                {
                    QueueProgressStatus current = this.queueService.GetQueueProgressStatus().FirstOrDefault();
                    if (current == null)
                    {
                        if (!this.queueService.IsProcessing)
                        {
                            this.Status = "Ready";
                        }

                        return;
                    }

                    this.Status = string.IsNullOrWhiteSpace(current.JobStatusShort) ? current.JobStatus : current.JobStatusShort;
                    this.Progress = current.ProgressValue;
                    this.Speed = current.AverageFrameRate > 0
                        ? string.Format("{0:N1} fps", current.AverageFrameRate)
                        : "-- fps";

                    TimeSpan? remaining = current.EstimatedTimeLeft;
                    if (remaining.HasValue && remaining.Value >= TimeSpan.Zero)
                    {
                        this.Eta = FormatTime(remaining.Value);
                        this.EstimatedFinish = DateTime.Now.Add(remaining.Value).ToString("g");
                    }
                    else
                    {
                        this.Eta = "Calculating...";
                        this.EstimatedFinish = "Calculating...";
                    }
                });
        }

        private void Clear()
        {
            this.LogText = string.Empty;
        }

        private void Refresh()
        {
            this.LogText = this.logService.GetFullLog() ?? string.Empty;
        }

        private static string FormatTime(TimeSpan value)
        {
            return value.Days >= 1
                ? string.Format("{0:d\\:hh\\:mm\\:ss}", value)
                : string.Format("{0:hh\\:mm\\:ss}", value);
        }

        private static void RunOnUi(Action action)
        {
            if (Application.Current?.Dispatcher == null || Application.Current.Dispatcher.CheckAccess())
            {
                action();
                return;
            }

            Application.Current.Dispatcher.BeginInvoke(action);
        }
    }
}
