// Transcencode additions to the HandBrake Windows GUI.
// Licensed under GPLv2 as part of the combined HandBrake build.

namespace HandBrakeWPF.ViewModels
{
    using System;
    using System.Collections.Generic;
    using System.Windows.Input;

    using HandBrakeWPF.Commands;
    using HandBrakeWPF.EventArgs;
    using HandBrakeWPF.Model;
    using HandBrakeWPF.Services.Encode.Model;
    using HandBrakeWPF.Services.Interfaces;
    using HandBrakeWPF.Services.Presets.Model;
    using HandBrakeWPF.Services.Scan.Model;
    using HandBrakeWPF.ViewModels.Interfaces;

    public sealed class TranscencodeUpscaleViewModel : ViewModelBase, ITranscencodeUpscaleViewModel
    {
        private readonly IPictureSettingsViewModel pictureSettingsViewModel;
        private Title currentTitle;
        private EncodeTask task;
        private string selectedTarget;
        private int targetWidth;
        private int targetHeight;
        private string sourceResolution = "--";
        private string status = "Open a source to configure scaling.";

        public TranscencodeUpscaleViewModel(
            IUserSettingService userSettingService,
            IPictureSettingsViewModel pictureSettingsViewModel)
            : base(userSettingService)
        {
            this.pictureSettingsViewModel = pictureSettingsViewModel;
            this.TargetChoices = new List<string>
            {
                "Same as source",
                "1920 × 1080 (1080p)",
                "2560 × 1440 (1440p)",
                "3840 × 2160 (4K)",
                "Custom"
            };
            this.selectedTarget = this.TargetChoices[0];
            this.PreserveAspectRatio = true;
            this.ApplyCommand = new SimpleRelayCommand<object>(_ => this.Apply());
            this.KeepSourceCommand = new SimpleRelayCommand<object>(_ => this.KeepSource());
        }

        public event EventHandler<TabStatusEventArgs> TabStatusChanged;

        public IList<string> TargetChoices { get; }

        public ICommand ApplyCommand { get; }

        public ICommand KeepSourceCommand { get; }

        public string SourceResolution
        {
            get => this.sourceResolution;
            private set
            {
                if (value == this.sourceResolution)
                {
                    return;
                }

                this.sourceResolution = value;
                this.NotifyOfPropertyChange(() => this.SourceResolution);
            }
        }

        public string SelectedTarget
        {
            get => this.selectedTarget;
            set
            {
                if (value == this.selectedTarget)
                {
                    return;
                }

                this.selectedTarget = value;
                this.ApplyTargetChoice(value);
                this.NotifyOfPropertyChange(() => this.SelectedTarget);
            }
        }

        public int TargetWidth
        {
            get => this.targetWidth;
            set
            {
                if (value == this.targetWidth)
                {
                    return;
                }

                this.targetWidth = Math.Max(2, value);
                this.NotifyOfPropertyChange(() => this.TargetWidth);
            }
        }

        public int TargetHeight
        {
            get => this.targetHeight;
            set
            {
                if (value == this.targetHeight)
                {
                    return;
                }

                this.targetHeight = Math.Max(2, value);
                this.NotifyOfPropertyChange(() => this.TargetHeight);
            }
        }

        public bool PreserveAspectRatio { get; set; }

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
            this.currentTitle = selectedTitle;
            this.task = encodeTask;

            if (selectedTitle == null || selectedTitle.Resolution == null)
            {
                this.SourceResolution = "--";
                this.Status = "Open a source to configure scaling.";
                return;
            }

            this.SourceResolution = string.Format("{0} × {1}", selectedTitle.Resolution.Width, selectedTitle.Resolution.Height);
            this.TargetWidth = selectedTitle.Resolution.Width;
            this.TargetHeight = selectedTitle.Resolution.Height;
            this.selectedTarget = this.TargetChoices[0];
            this.NotifyOfPropertyChange(() => this.SelectedTarget);
            this.Status = "Same as source keeps the original dimensions. Choose a larger target to enable HandBrake upscaling.";
        }

        public void SetPreset(Preset preset, EncodeTask encodeTask)
        {
            this.task = encodeTask;
        }

        public void UpdateTask(EncodeTask encodeTask)
        {
            this.task = encodeTask;
        }

        public bool MatchesPreset(Preset preset) => true;

        private void ApplyTargetChoice(string choice)
        {
            if (this.currentTitle == null || this.currentTitle.Resolution == null)
            {
                return;
            }

            switch (choice)
            {
                case "Same as source":
                    this.TargetWidth = this.currentTitle.Resolution.Width;
                    this.TargetHeight = this.currentTitle.Resolution.Height;
                    break;
                case "1920 × 1080 (1080p)":
                    this.TargetWidth = 1920;
                    this.TargetHeight = 1080;
                    break;
                case "2560 × 1440 (1440p)":
                    this.TargetWidth = 2560;
                    this.TargetHeight = 1440;
                    break;
                case "3840 × 2160 (4K)":
                    this.TargetWidth = 3840;
                    this.TargetHeight = 2160;
                    break;
            }
        }

        private void Apply()
        {
            if (this.task == null || this.currentTitle == null || this.currentTitle.Resolution == null)
            {
                this.Status = "Load a source before applying scaling.";
                return;
            }

            int sourceWidth = this.currentTitle.Resolution.Width;
            int sourceHeight = this.currentTitle.Resolution.Height;
            bool isUpscale = this.TargetWidth > sourceWidth || this.TargetHeight > sourceHeight;

            this.task.Width = null;
            this.task.Height = null;
            this.task.MaxWidth = this.TargetWidth;
            this.task.MaxHeight = this.TargetHeight;
            this.task.AllowUpscaling = isUpscale;
            this.task.KeepDisplayAspect = this.PreserveAspectRatio;
            this.pictureSettingsViewModel.UpdateTask(this.task);
            this.TabStatusChanged?.Invoke(this, new TabStatusEventArgs("TranscencodeUpscale", ChangedOption.Dimensions));

            this.Status = isUpscale
                ? string.Format("Applied a {0} × {1} maximum output with upscaling enabled. Review the Dimensions tab before encoding.", this.TargetWidth, this.TargetHeight)
                : string.Format("Applied a {0} × {1} maximum output without upscaling.", this.TargetWidth, this.TargetHeight);
        }

        private void KeepSource()
        {
            if (this.currentTitle == null || this.currentTitle.Resolution == null)
            {
                this.Status = "Load a source before restoring its dimensions.";
                return;
            }

            this.SelectedTarget = "Same as source";
            this.Apply();
        }
    }
}
