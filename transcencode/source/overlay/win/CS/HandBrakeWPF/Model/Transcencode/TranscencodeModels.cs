// Transcencode additions to the HandBrake Windows GUI.
// Licensed under GPLv2 as part of the combined HandBrake build.

namespace HandBrakeWPF.Model.Transcencode
{
    public sealed class SourceAudioTrackRow
    {
        public int Track { get; set; }
        public string Language { get; set; }
        public string Code { get; set; }
        public string Codec { get; set; }
        public string Channels { get; set; }
        public string Bitrate { get; set; }
        public string SampleRate { get; set; }
        public string Name { get; set; }
    }

    public sealed class SourceSubtitleTrackRow
    {
        public int Track { get; set; }
        public string Language { get; set; }
        public string Code { get; set; }
        public string Type { get; set; }
        public string Name { get; set; }
        public string Capabilities { get; set; }
    }

    public sealed class AnalysisSampleRow
    {
        public int Sample { get; set; }
        public string ApproximateTime { get; set; }
        public string Brightness { get; set; }
        public string DarkPixels { get; set; }
        public string Contrast { get; set; }
        public string Detail { get; set; }
        public string SceneVariation { get; set; }
        public string Difficulty { get; set; }
    }

    public sealed class VerificationResultRow
    {
        public string Check { get; set; }
        public string Result { get; set; }
        public string Details { get; set; }
    }
}
