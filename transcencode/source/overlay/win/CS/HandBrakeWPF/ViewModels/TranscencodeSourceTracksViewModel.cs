// Transcencode additions to the HandBrake Windows GUI.
// Licensed under GPLv2 as part of the combined HandBrake build.

namespace HandBrakeWPF.ViewModels
{
    using System;
    using System.Collections.ObjectModel;
    using System.Linq;

    using HandBrakeWPF.EventArgs;
    using HandBrakeWPF.Model.Transcencode;
    using HandBrakeWPF.Services.Encode.Model;
    using HandBrakeWPF.Services.Interfaces;
    using HandBrakeWPF.Services.Presets.Model;
    using HandBrakeWPF.Services.Scan.Model;
    using HandBrakeWPF.ViewModels.Interfaces;

    public sealed class TranscencodeSourceTracksViewModel : ViewModelBase, ITranscencodeSourceTracksViewModel
    {
        private string summary = "Open a source to see every available audio and subtitle track.";

        public TranscencodeSourceTracksViewModel(IUserSettingService userSettingService)
            : base(userSettingService)
        {
            this.AudioTracks = new ObservableCollection<SourceAudioTrackRow>();
            this.SubtitleTracks = new ObservableCollection<SourceSubtitleTrackRow>();
        }

        public event EventHandler<TabStatusEventArgs> TabStatusChanged;

        public ObservableCollection<SourceAudioTrackRow> AudioTracks { get; }

        public ObservableCollection<SourceSubtitleTrackRow> SubtitleTracks { get; }

        public string Summary
        {
            get => this.summary;
            private set
            {
                if (value == this.summary)
                {
                    return;
                }

                this.summary = value;
                this.NotifyOfPropertyChange(() => this.Summary);
            }
        }

        public void SetSource(Source source, Title selectedTitle, Preset currentPreset, EncodeTask task)
        {
            this.Populate(selectedTitle);
        }

        public void SetPreset(Preset preset, EncodeTask task)
        {
        }

        public void UpdateTask(EncodeTask task)
        {
        }

        public bool MatchesPreset(Preset preset) => true;

        private void Populate(Title title)
        {
            this.AudioTracks.Clear();
            this.SubtitleTracks.Clear();

            if (title == null)
            {
                this.Summary = "Open a source to see every available audio and subtitle track.";
                return;
            }

            foreach (Audio track in title.AudioTracks ?? Enumerable.Empty<Audio>())
            {
                this.AudioTracks.Add(
                    new SourceAudioTrackRow
                    {
                        Track = track.TrackNumber,
                        Language = string.IsNullOrWhiteSpace(track.Language) ? "Unknown" : track.Language,
                        Code = string.IsNullOrWhiteSpace(track.LanguageCode) ? "und" : track.LanguageCode,
                        Codec = string.IsNullOrWhiteSpace(track.Description) ? track.Codec.ToString() : track.Description,
                        Channels = string.IsNullOrWhiteSpace(track.ChannelLayout) ? "Unknown" : track.ChannelLayout,
                        Bitrate = track.Bitrate > 0 ? string.Format("{0:N0} kbps", track.Bitrate / 1000.0) : "Unknown",
                        SampleRate = track.SampleRate > 0 ? string.Format("{0:N1} kHz", track.SampleRate / 1000.0) : "Unknown",
                        Name = string.IsNullOrWhiteSpace(track.Name) ? string.Empty : track.Name
                    });
            }

            foreach (Subtitle track in title.Subtitles ?? Enumerable.Empty<Subtitle>())
            {
                string capabilities = track.CanBurnIn ? "Can burn in" : "Passthrough only";
                if (track.CanForce)
                {
                    capabilities += ", forced-only supported";
                }

                this.SubtitleTracks.Add(
                    new SourceSubtitleTrackRow
                    {
                        Track = track.TrackNumber,
                        Language = string.IsNullOrWhiteSpace(track.Language) ? "Unknown" : track.Language,
                        Code = string.IsNullOrWhiteSpace(track.LanguageCodeClean) ? "und" : track.LanguageCodeClean,
                        Type = string.IsNullOrWhiteSpace(track.TypeString) ? track.SubtitleType.ToString() : track.TypeString,
                        Name = string.IsNullOrWhiteSpace(track.Name) ? string.Empty : track.Name,
                        Capabilities = capabilities
                    });
            }

            this.Summary = string.Format(
                "Source contains {0} audio track{1} and {2} subtitle track{3}. Select output tracks on HandBrake's Audio and Subtitles tabs.",
                this.AudioTracks.Count,
                this.AudioTracks.Count == 1 ? string.Empty : "s",
                this.SubtitleTracks.Count,
                this.SubtitleTracks.Count == 1 ? string.Empty : "s");
        }
    }
}
