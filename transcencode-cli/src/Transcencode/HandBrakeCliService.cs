using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Transcencode;

internal sealed partial class HandBrakeCliService
{
    private readonly string cliPath;

    internal HandBrakeCliService(string? cliPath = null)
    {
        this.cliPath = cliPath ?? Path.Combine(AppContext.BaseDirectory, "HandBrakeCLI.exe");
    }

    internal string CliPath => cliPath;
    internal bool Exists => File.Exists(cliPath);

    internal async Task<string> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        ProcessResult result = await RunAsync(["--version"], null, null, cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"HandBrakeCLI version check failed with exit code {result.ExitCode}.\n{result.CombinedOutput}");
        }

        return result.CombinedOutput.Trim();
    }

    internal async Task<SourceInfo> ScanAsync(
        string inputPath,
        Action<string>? onLine,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            throw new FileNotFoundException("The selected source file does not exist.", inputPath);
        }

        ProcessResult result = await RunAsync(
            ["--input", inputPath, "--title", "0", "--scan", "--json"],
            onLine,
            null,
            cancellationToken);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"HandBrakeCLI could not scan the source (exit code {result.ExitCode}).\n\n{Tail(result.CombinedOutput, 6000)}");
        }

        return SourceScanParser.Parse(inputPath, result.CombinedOutput);
    }

    internal async Task<ProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        Action<string>? onLine,
        Action<ProgressInfo>? onProgress,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(cliPath))
        {
            throw new FileNotFoundException(
                "HandBrakeCLI.exe is missing. Reinstall Transcencode or keep all portable files together.",
                cliPath);
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = cliPath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        StringBuilder combined = new();
        object outputLock = new();
        ProgressInfo? lastProgress = null;

        void ReceiveLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            lock (outputLock)
            {
                combined.AppendLine(line);
            }

            onLine?.Invoke(line);
            ProgressInfo? progress = TryParseProgress(line);
            if (progress is not null)
            {
                lastProgress = progress;
                onProgress?.Invoke(progress);
            }
        }

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Windows did not start HandBrakeCLI.exe.");
            }
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Transcencode could not start its encoding engine at:\n{cliPath}",
                exception);
        }

        Task stdout = ConsumeStreamAsync(process.StandardOutput, ReceiveLine, cancellationToken);
        Task stderr = ConsumeStreamAsync(process.StandardError, ReceiveLine, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch
        {
            TryKill(process);
            throw;
        }

        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            CombinedOutput = combined.ToString(),
            LastProgress = lastProgress
        };
    }

    internal static ProgressInfo? TryParseProgress(string line)
    {
        Match match = ProgressRegex().Match(line);
        if (!match.Success ||
            !double.TryParse(match.Groups["percent"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
        {
            return null;
        }

        double? currentFps = ParseNullableDouble(match.Groups["fps"].Value);
        double? averageFps = ParseNullableDouble(match.Groups["avg"].Value);
        TimeSpan? eta = ParseEta(match.Groups["eta"].Value);

        return new ProgressInfo
        {
            Percent = Math.Clamp(percent, 0, 100),
            CurrentFps = currentFps,
            AverageFps = averageFps,
            Eta = eta,
            RawLine = line
        };
    }

    internal static string Tail(string value, int maximumCharacters)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maximumCharacters)
        {
            return value;
        }

        return value[^maximumCharacters..];
    }

    private static async Task ConsumeStreamAsync(
        StreamReader reader,
        Action<string> receiveLine,
        CancellationToken cancellationToken)
    {
        char[] buffer = new char[2048];
        StringBuilder line = new();

        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (count == 0)
            {
                break;
            }

            for (int index = 0; index < count; index++)
            {
                char character = buffer[index];
                if (character is '\r' or '\n')
                {
                    if (line.Length > 0)
                    {
                        receiveLine(line.ToString());
                        line.Clear();
                    }
                }
                else
                {
                    line.Append(character);
                }
            }
        }

        if (line.Length > 0)
        {
            receiveLine(line.ToString());
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // Cancellation must not be replaced by a secondary process-kill failure.
        }
    }

    private static double? ParseNullableDouble(string value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : null;
    }

    private static TimeSpan? ParseEta(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        Match unitMatch = EtaUnitsRegex().Match(value);
        if (unitMatch.Success)
        {
            int hours = ParseInt(unitMatch.Groups["h"].Value);
            int minutes = ParseInt(unitMatch.Groups["m"].Value);
            int seconds = ParseInt(unitMatch.Groups["s"].Value);
            return new TimeSpan(hours, minutes, seconds);
        }

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out TimeSpan parsed))
        {
            return parsed;
        }

        return null;
    }

    private static int ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;

    [GeneratedRegex(
        @"(?<percent>\d+(?:\.\d+)?)\s*%.*?(?:(?<fps>\d+(?:\.\d+)?)\s*fps)?(?:.*?avg\s*(?<avg>\d+(?:\.\d+)?)\s*fps)?(?:.*?ETA\s*(?<eta>(?:\d+h)?(?:\d+m)?(?:\d+s)?|\d{1,2}:\d{2}:\d{2}|unknown))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProgressRegex();

    [GeneratedRegex(@"(?:(?<h>\d+)h)?(?:(?<m>\d+)m)?(?:(?<s>\d+)s)?", RegexOptions.IgnoreCase)]
    private static partial Regex EtaUnitsRegex();
}

internal static class SourceScanParser
{
    internal static SourceInfo Parse(string inputPath, string scanOutput)
    {
        string json = ExtractTitleSetJson(scanOutput);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        if (!TryGetProperty(root, "TitleList", out JsonElement titleList) ||
            titleList.ValueKind != JsonValueKind.Array ||
            titleList.GetArrayLength() == 0)
        {
            throw new InvalidDataException("HandBrakeCLI returned JSON, but it did not contain any readable titles.");
        }

        int mainFeature = GetInt(root, "MainFeature", -1);
        JsonElement selectedTitle = titleList[0];
        if (mainFeature >= 0)
        {
            foreach (JsonElement title in titleList.EnumerateArray())
            {
                if (GetInt(title, "Index", -1) == mainFeature)
                {
                    selectedTitle = title;
                    break;
                }
            }
        }

        int width = 0;
        int height = 0;
        if (TryGetProperty(selectedTitle, "Geometry", out JsonElement geometry))
        {
            width = GetInt(geometry, "Width", 0);
            height = GetInt(geometry, "Height", 0);
        }

        ObservableCollection<AudioTrackInfo> audio = [];
        if (TryGetProperty(selectedTitle, "AudioList", out JsonElement audioList) && audioList.ValueKind == JsonValueKind.Array)
        {
            int fallbackTrack = 1;
            foreach (JsonElement item in audioList.EnumerateArray())
            {
                audio.Add(new AudioTrackInfo
                {
                    Track = GetInt(item, "TrackNumber", fallbackTrack),
                    Language = FirstString(item, "Language", "Description") ?? "Unknown",
                    LanguageCode = FirstString(item, "LanguageCode", "Code") ?? string.Empty,
                    Codec = FirstString(item, "CodecName", "Codec", "Description") ?? string.Empty,
                    Channels = FirstString(item, "ChannelLayoutName", "ChannelLayout", "Description") ?? string.Empty,
                    BitRate = GetLong(item, "BitRate", 0),
                    SampleRate = GetInt(item, "SampleRate", 0),
                    Name = FirstString(item, "Name") ?? string.Empty
                });
                fallbackTrack++;
            }
        }

        ObservableCollection<SubtitleTrackInfo> subtitles = [];
        if (TryGetProperty(selectedTitle, "SubtitleList", out JsonElement subtitleList) && subtitleList.ValueKind == JsonValueKind.Array)
        {
            int fallbackTrack = 1;
            foreach (JsonElement item in subtitleList.EnumerateArray())
            {
                subtitles.Add(new SubtitleTrackInfo
                {
                    Track = GetInt(item, "TrackNumber", fallbackTrack),
                    Language = FirstString(item, "Language", "Description") ?? "Unknown",
                    LanguageCode = FirstString(item, "LanguageCode", "Code") ?? string.Empty,
                    Type = FirstString(item, "SourceName", "Format", "CodecName", "Description") ?? string.Empty,
                    Name = FirstString(item, "Name") ?? string.Empty,
                    Capabilities = BuildSubtitleCapabilities(item)
                });
                fallbackTrack++;
            }
        }

        return new SourceInfo
        {
            Path = inputPath,
            TitleIndex = GetInt(selectedTitle, "Index", 1),
            Duration = ReadDuration(selectedTitle),
            Width = width,
            Height = height,
            VideoCodec = FirstString(selectedTitle, "VideoCodec", "VideoCodecBase", "VideoCodecName") ?? "Unknown",
            FrameRate = ReadFrameRate(selectedTitle),
            HdrSummary = ReadHdrSummary(selectedTitle),
            CropSummary = ReadCrop(selectedTitle),
            AudioTracks = audio,
            SubtitleTracks = subtitles
        };
    }

    private static string ExtractTitleSetJson(string output)
    {
        for (int start = 0; start < output.Length; start++)
        {
            if (output[start] != '{')
            {
                continue;
            }

            int end = FindBalancedObjectEnd(output, start);
            if (end < 0)
            {
                continue;
            }

            string candidate = output[start..(end + 1)];
            if (!candidate.Contains("\"TitleList\"", StringComparison.Ordinal))
            {
                start = end;
                continue;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(candidate);
                if (TryGetProperty(document.RootElement, "TitleList", out _))
                {
                    return candidate;
                }
            }
            catch (JsonException)
            {
                // Continue searching; HandBrake may have printed non-JSON text containing braces.
            }

            start = end;
        }

        throw new InvalidDataException(
            "HandBrakeCLI completed its scan but Transcencode could not find the JSON title information in its output.");
    }

    private static int FindBalancedObjectEnd(string value, int start)
    {
        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int index = start; index < value.Length; index++)
        {
            char character = value[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
            }
            else if (character == '{')
            {
                depth++;
            }
            else if (character == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return index;
                }
            }
        }

        return -1;
    }

    private static TimeSpan ReadDuration(JsonElement title)
    {
        if (!TryGetProperty(title, "Duration", out JsonElement duration))
        {
            return TimeSpan.Zero;
        }

        if (duration.ValueKind == JsonValueKind.String && TimeSpan.TryParse(duration.GetString(), out TimeSpan parsed))
        {
            return parsed;
        }

        if (duration.ValueKind != JsonValueKind.Object)
        {
            return TimeSpan.Zero;
        }

        int hours = GetInt(duration, "Hours", 0);
        int minutes = GetInt(duration, "Minutes", 0);
        int seconds = GetInt(duration, "Seconds", 0);
        int milliseconds = 0;
        long ticks90Khz = GetLong(duration, "Ticks", 0);
        if (ticks90Khz > 0)
        {
            long wholeSecondsTicks = ((hours * 3600L) + (minutes * 60L) + seconds) * 90000L;
            milliseconds = (int)Math.Clamp((ticks90Khz - wholeSecondsTicks) / 90L, 0, 999);
        }

        return new TimeSpan(0, hours, minutes, seconds, milliseconds);
    }

    private static string ReadFrameRate(JsonElement title)
    {
        if (!TryGetProperty(title, "FrameRate", out JsonElement rate))
        {
            return "Unknown";
        }

        if (rate.ValueKind == JsonValueKind.String)
        {
            return rate.GetString() ?? "Unknown";
        }

        if (rate.ValueKind == JsonValueKind.Object)
        {
            double numerator = GetDouble(rate, "Num", 0);
            double denominator = GetDouble(rate, "Den", 1);
            if (numerator > 0 && denominator > 0)
            {
                return (numerator / denominator).ToString("0.###", CultureInfo.InvariantCulture) + " fps";
            }
        }

        return "Unknown";
    }

    private static string ReadCrop(JsonElement title)
    {
        if (!TryGetProperty(title, "Crop", out JsonElement crop) || crop.ValueKind != JsonValueKind.Array)
        {
            return "Not reported";
        }

        int[] values = crop.EnumerateArray().Select(item => item.TryGetInt32(out int value) ? value : 0).ToArray();
        return values.Length >= 4
            ? $"Top {values[0]}, bottom {values[1]}, left {values[2]}, right {values[3]}"
            : "Not reported";
    }

    private static string ReadHdrSummary(JsonElement title)
    {
        List<string> parts = [];
        string serialized = title.GetRawText();

        if (serialized.Contains("DolbyVision", StringComparison.OrdinalIgnoreCase) ||
            serialized.Contains("Dolby Vision", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add("Dolby Vision metadata detected");
        }

        if (serialized.Contains("HDR10Plus", StringComparison.OrdinalIgnoreCase) &&
            !serialized.Contains("\"HDR10Plus\": 0", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add("HDR10+ metadata detected");
        }

        if (TryGetProperty(title, "Color", out JsonElement color))
        {
            int transfer = GetInt(color, "Transfer", GetInt(color, "TransferCharacteristics", 0));
            if (transfer == 16)
            {
                parts.Add("HDR10 / PQ transfer");
            }
            else if (transfer == 18)
            {
                parts.Add("HLG transfer");
            }
        }

        return parts.Count == 0 ? "Standard dynamic range or undetermined" : string.Join("; ", parts.Distinct());
    }

    private static string BuildSubtitleCapabilities(JsonElement item)
    {
        List<string> capabilities = [];
        if (GetBoolean(item, "CanBurn", false)) capabilities.Add("burnable");
        if (GetBoolean(item, "CanForce", false)) capabilities.Add("forced flag");
        if (GetBoolean(item, "CanPass", false)) capabilities.Add("passthrough");
        return capabilities.Count == 0 ? string.Empty : string.Join(", ", capabilities);
    }

    private static string? FirstString(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (!TryGetProperty(element, name, out JsonElement value))
            {
                continue;
            }

            string? result = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(result))
            {
                return result;
            }
        }

        return null;
    }

    private static int GetInt(JsonElement element, string name, int fallback)
    {
        if (!TryGetProperty(element, name, out JsonElement value)) return fallback;
        if (value.TryGetInt32(out int result)) return result;
        return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : fallback;
    }

    private static long GetLong(JsonElement element, string name, long fallback)
    {
        if (!TryGetProperty(element, name, out JsonElement value)) return fallback;
        if (value.TryGetInt64(out long result)) return result;
        return long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : fallback;
    }

    private static double GetDouble(JsonElement element, string name, double fallback)
    {
        if (!TryGetProperty(element, name, out JsonElement value)) return fallback;
        if (value.TryGetDouble(out double result)) return result;
        return double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result) ? result : fallback;
    }

    private static bool GetBoolean(JsonElement element, string name, bool fallback)
    {
        if (!TryGetProperty(element, name, out JsonElement value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt32(out int number) && number != 0,
            JsonValueKind.String => bool.TryParse(value.GetString(), out bool parsed) ? parsed : fallback,
            _ => fallback
        };
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
