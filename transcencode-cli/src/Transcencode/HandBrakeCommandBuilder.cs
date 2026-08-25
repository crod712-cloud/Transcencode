using System.Globalization;
using System.Text;

namespace Transcencode;

internal static class HandBrakeCommandBuilder
{
    internal static IReadOnlyList<string> BuildEncodeArguments(EncodeOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.InputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputPath);

        List<string> arguments =
        [
            "--input", options.InputPath,
            "--output", options.OutputPath,
            "--encoder", options.Encoder.CliName,
            "--quality", options.Quality.ToString("0.##", CultureInfo.InvariantCulture),
            "--verbose", "1"
        ];

        if (!string.IsNullOrWhiteSpace(options.EncoderPreset) &&
            !options.EncoderPreset.Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("--encoder-preset");
            arguments.Add(options.EncoderPreset);
        }

        AddCrop(arguments, options);
        AddScale(arguments, options);

        if (options.StartSeconds.HasValue)
        {
            arguments.Add("--start-at");
            arguments.Add($"seconds:{Math.Max(0, options.StartSeconds.Value)}");
        }

        if (options.StopAfterSeconds.HasValue)
        {
            arguments.Add("--stop-at");
            arguments.Add($"seconds:{Math.Max(1, options.StopAfterSeconds.Value)}");
        }

        if (!options.IsAnalysisSample)
        {
            AddAudioAndSubtitles(arguments, options);

            if (options.ChapterMarkers)
            {
                arguments.Add("--markers");
            }

            if (options.WebOptimizeMp4 &&
                Path.GetExtension(options.OutputPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                arguments.Add("--optimize");
            }
        }

        return arguments;
    }

    internal static string ToDisplayCommand(string executable, IEnumerable<string> arguments)
    {
        StringBuilder command = new();
        command.Append(QuoteForDisplay(executable));
        foreach (string argument in arguments)
        {
            command.Append(' ');
            command.Append(QuoteForDisplay(argument));
        }

        return command.ToString();
    }

    private static void AddCrop(List<string> arguments, EncodeOptions options)
    {
        switch (options.Crop)
        {
            case CropChoice.PreserveOriginal:
                arguments.Add("--crop");
                arguments.Add("0:0:0:0");
                break;
            case CropChoice.SafeAutomatic:
                arguments.Add("--crop-mode");
                arguments.Add("conservative");
                break;
            case CropChoice.Automatic:
                arguments.Add("--crop-mode");
                arguments.Add("automatic");
                break;
            case CropChoice.Custom:
                arguments.Add("--crop");
                arguments.Add(
                    $"{Math.Max(0, options.CropTop)}:{Math.Max(0, options.CropBottom)}:" +
                    $"{Math.Max(0, options.CropLeft)}:{Math.Max(0, options.CropRight)}");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(options.Crop));
        }
    }

    private static void AddScale(List<string> arguments, EncodeOptions options)
    {
        (int width, int height) = options.Scale switch
        {
            ScaleChoice.SameAsSource => (0, 0),
            ScaleChoice.FullHd1080 => (1920, 1080),
            ScaleChoice.QuadHd1440 => (2560, 1440),
            ScaleChoice.UltraHd2160 => (3840, 2160),
            ScaleChoice.Custom => (Math.Max(0, options.CustomWidth), Math.Max(0, options.CustomHeight)),
            _ => throw new ArgumentOutOfRangeException(nameof(options.Scale))
        };

        if (width <= 0 && height <= 0)
        {
            return;
        }

        if (width > 0)
        {
            arguments.Add("--width");
            arguments.Add(width.ToString(CultureInfo.InvariantCulture));
        }

        if (height > 0)
        {
            arguments.Add("--height");
            arguments.Add(height.ToString(CultureInfo.InvariantCulture));
        }

        arguments.Add("--keep-display-aspect");
        arguments.Add("--allow-upscaling");
    }

    private static void AddAudioAndSubtitles(List<string> arguments, EncodeOptions options)
    {
        if (options.PreserveAllAudio)
        {
            arguments.Add("--all-audio");
            arguments.Add("--aencoder");
            arguments.Add("copy");
            arguments.Add("--audio-copy-mask");
            arguments.Add("aac,ac3,eac3,truehd,dts,dtshd,mp2,mp3,flac,opus");
            arguments.Add("--audio-fallback");
            arguments.Add("av_aac");
        }

        if (options.PreserveAllSubtitles)
        {
            arguments.Add("--all-subtitles");
        }
    }

    private static string QuoteForDisplay(string value)
    {
        if (value.Length > 0 && value.All(character => !char.IsWhiteSpace(character) && character != '"'))
        {
            return value;
        }

        return '"' + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                           .Replace("\"", "\\\"", StringComparison.Ordinal) + '"';
    }
}
