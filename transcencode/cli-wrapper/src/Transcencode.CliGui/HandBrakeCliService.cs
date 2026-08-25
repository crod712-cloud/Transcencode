using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Transcencode.CliGui;

public sealed class HandBrakeCliService : IDisposable
{
    private readonly object processLock = new();
    private Process? activeProcess;

    public HandBrakeCliService(string? explicitPath = null)
    {
        ExecutablePath = ResolveExecutable(explicitPath);
    }

    public string ExecutablePath { get; }
    public bool IsRunning
    {
        get
        {
            lock (processLock)
            {
                return activeProcess is { HasExited: false };
            }
        }
    }

    public static string ResolveExecutable(string? explicitPath = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath)) candidates.Add(explicitPath);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "HandBrakeCLI.exe"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "Engine", "HandBrakeCLI.exe"));
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Transcencode", "HandBrakeCLI.exe"));

        string? path = candidates.FirstOrDefault(File.Exists);
        if (path is null)
        {
            throw new FileNotFoundException(
                "HandBrakeCLI.exe was not found beside Transcencode. Reinstall Transcencode so the verified encoding engine is restored.\n\nChecked:\n" +
                string.Join("\n", candidates));
        }

        return Path.GetFullPath(path);
    }

    public static ProcessStartInfo CreateHiddenStartInfo(string executablePath, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    public async Task<ProcessRunResult> RunAsync(
        IReadOnlyList<string> arguments,
        Action<string, bool>? lineReceived = null,
        Action<EngineProgress>? progressReceived = null,
        string? logPrefix = null,
        CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureDirectories();
        string logPath = Path.Combine(
            AppPaths.LogDirectory,
            $"{SanitizeFilePart(logPrefix ?? "engine")}-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        var combined = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();

        using var process = new Process
        {
            StartInfo = CreateHiddenStartInfo(ExecutablePath, arguments),
            EnableRaisingEvents = true
        };

        lock (processLock)
        {
            if (activeProcess is { HasExited: false })
            {
                throw new InvalidOperationException("Another HandBrake operation is already running.");
            }
            activeProcess = process;
        }

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Windows did not start HandBrakeCLI.exe.");
            }

            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited) process.Kill(true);
                }
                catch
                {
                    // Cancellation is best effort; the original operation state is preserved.
                }
            });

            Task stdoutTask = PumpReaderAsync(process.StandardOutput, false);
            Task stderrTask = PumpReaderAsync(process.StandardError, true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            stopwatch.Stop();

            string commandDisplay = FormatCommand(ExecutablePath, arguments);
            var log = new StringBuilder();
            log.AppendLine("Transcencode HandBrakeCLI operation");
            log.AppendLine("Started: " + DateTime.Now.ToString("O"));
            log.AppendLine("Command: " + commandDisplay);
            log.AppendLine("Exit code: " + process.ExitCode);
            log.AppendLine("Elapsed: " + stopwatch.Elapsed);
            log.AppendLine();
            log.Append(combined);
            await File.WriteAllTextAsync(logPath, log.ToString(), new UTF8Encoding(true), CancellationToken.None).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return new ProcessRunResult
            {
                ExitCode = process.ExitCode,
                StandardOutput = standardOutput.ToString(),
                StandardError = standardError.ToString(),
                CombinedOutput = combined.ToString(),
                Elapsed = stopwatch.Elapsed,
                LogPath = logPath
            };
        }
        finally
        {
            lock (processLock)
            {
                if (ReferenceEquals(activeProcess, process)) activeProcess = null;
            }
        }

        async Task PumpReaderAsync(StreamReader reader, bool isError)
        {
            while (true)
            {
                string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;

                lock (combined)
                {
                    if (isError) standardError.AppendLine(line);
                    else standardOutput.AppendLine(line);
                    combined.Append(isError ? "[stderr] " : "[stdout] ").AppendLine(line);
                }

                lineReceived?.Invoke(line, isError);
                if (TryParseProgress(line, out EngineProgress? progress))
                {
                    progressReceived?.Invoke(progress);
                }
            }
        }
    }

    public async Task<ScanResult> ScanAsync(
        string sourcePath,
        Action<string, bool>? lineReceived = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("The selected source file does not exist.", sourcePath);

        IReadOnlyList<string> args = ["-i", sourcePath, "--scan", "--json"];
        ProcessRunResult run = await RunAsync(args, lineReceived, null, "scan", cancellationToken).ConfigureAwait(false);
        string combined = run.StandardOutput + Environment.NewLine + run.StandardError;

        if (run.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"HandBrake could not scan the source (exit code {run.ExitCode}).\n\nLog: {run.LogPath}\n\n{Tail(combined, 5000)}");
        }

        return ParseScan(sourcePath, combined);
    }

    public static IReadOnlyList<string> BuildEncodeArguments(EncodePlan plan)
    {
        if (string.IsNullOrWhiteSpace(plan.SourcePath)) throw new ArgumentException("Source path is required.");
        if (string.IsNullOrWhiteSpace(plan.OutputPath)) throw new ArgumentException("Output path is required.");

        var args = new List<string>
        {
            "-i", plan.SourcePath,
            "-o", plan.OutputPath,
            "-e", plan.EncoderId,
            "-q", plan.Quality.ToString("0.0", CultureInfo.InvariantCulture)
        };

        if (!string.IsNullOrWhiteSpace(plan.EncoderPreset))
        {
            args.Add("--encoder-preset");
            args.Add(plan.EncoderPreset);
        }

        string extension = Path.GetExtension(plan.OutputPath);
        args.Add("-f");
        args.Add(extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                 extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
            ? "av_mp4"
            : "av_mkv");

        if (plan.EnableNvdec)
        {
            args.Add("--enable-hw-decoding");
            args.Add("nvdec");
        }

        switch (plan.CropMode)
        {
            case "none":
            case "conservative":
            case "auto":
                args.Add("--crop-mode");
                args.Add(plan.CropMode);
                break;
            case "custom":
                args.Add("--crop-mode");
                args.Add("custom");
                args.Add("--crop");
                args.Add($"{Math.Max(0, plan.CropTop)}:{Math.Max(0, plan.CropBottom)}:{Math.Max(0, plan.CropLeft)}:{Math.Max(0, plan.CropRight)}");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(plan.CropMode), "Unknown crop mode: " + plan.CropMode);
        }

        if (!string.Equals(plan.ScaleMode, "source", StringComparison.OrdinalIgnoreCase))
        {
            int width = Math.Max(0, plan.TargetWidth);
            int height = Math.Max(0, plan.TargetHeight);
            if (width > 0)
            {
                args.Add("-w");
                args.Add(width.ToString(CultureInfo.InvariantCulture));
            }
            if (height > 0)
            {
                args.Add("-l");
                args.Add(height.ToString(CultureInfo.InvariantCulture));
            }
            args.Add("--keep-display-aspect");

            if (width > plan.SourceWidth || height > plan.SourceHeight)
            {
                args.Add("--upscaling");
            }
        }

        args.Add("-a");
        args.Add(plan.AudioTracks.Count == 0 ? "none" : string.Join(',', plan.AudioTracks));
        args.Add("-s");
        args.Add(plan.SubtitleTracks.Count == 0 ? "none" : string.Join(',', plan.SubtitleTracks));

        return args;
    }

    public static string FormatCommand(string executablePath, IEnumerable<string> arguments)
    {
        static string Quote(string value)
        {
            if (value.Length == 0) return "\"\"";
            if (!value.Any(char.IsWhiteSpace) && !value.Contains('"')) return value;
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        return Quote(executablePath) + " " + string.Join(" ", arguments.Select(Quote));
    }

    public static bool TryParseProgress(string line, out EngineProgress? progress)
    {
        progress = null;
        if (string.IsNullOrWhiteSpace(line)) return false;

        Match percentMatch = Regex.Match(line, @"(?<percent>\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.CultureInvariant);
        if (!percentMatch.Success) return false;

        double percent = double.Parse(percentMatch.Groups["percent"].Value, CultureInfo.InvariantCulture);
        double currentFps = ParseDouble(line, @"(?<value>\d+(?:\.\d+)?)\s*fps");
        double averageFps = ParseDouble(line, @"avg\s*(?<value>\d+(?:\.\d+)?)\s*fps");
        TimeSpan? eta = ParseEta(line);
        string phase = line.Contains("Muxing", StringComparison.OrdinalIgnoreCase) ? "Muxing" : "Encoding";

        progress = new EngineProgress
        {
            Phase = phase,
            Percent = Math.Clamp(percent, 0, 100),
            CurrentFps = currentFps,
            AverageFps = averageFps,
            Eta = eta,
            RawLine = line
        };
        return true;
    }

    public static ScanResult ParseScan(string sourcePath, string scanText)
    {
        try
        {
            string? json = ExtractTitleSetJson(scanText);
            if (!string.IsNullOrWhiteSpace(json))
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                JsonElement titleList = GetProperty(root, "TitleList");
                if (titleList.ValueKind == JsonValueKind.Array && titleList.GetArrayLength() > 0)
                {
                    JsonElement title = titleList[0];
                    return ParseJsonTitle(sourcePath, title, scanText);
                }
            }
        }
        catch (Exception ex)
        {
            CrashReporter.Write("HandBrake JSON scan parsing failed; text fallback was used", ex);
        }

        return ParseTextFallback(sourcePath, scanText);
    }

    public static string? ExtractTitleSetJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        int keyIndex = text.IndexOf("\"TitleList\"", StringComparison.OrdinalIgnoreCase);
        if (keyIndex < 0) return null;

        for (int start = text.LastIndexOf('{', keyIndex); start >= 0; start = text.LastIndexOf('{', start - 1))
        {
            string? candidate = ExtractBalancedObject(text, start);
            if (candidate is null || !candidate.Contains("\"TitleList\"", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using JsonDocument document = JsonDocument.Parse(candidate);
                if (GetProperty(document.RootElement, "TitleList").ValueKind == JsonValueKind.Array)
                {
                    return candidate;
                }
            }
            catch (JsonException)
            {
                // Try an earlier opening brace.
            }
        }

        return null;
    }

    private static string? ExtractBalancedObject(string text, int start)
    {
        int depth = 0;
        bool inString = false;
        bool escape = false;

        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return text.Substring(start, i - start + 1);
            }
        }

        return null;
    }

    private static ScanResult ParseJsonTitle(string sourcePath, JsonElement title, string raw)
    {
        JsonElement geometry = GetProperty(title, "Geometry");
        int width = GetInt(geometry, "Width", GetInt(title, "Width", 0));
        int height = GetInt(geometry, "Height", GetInt(title, "Height", 0));
        double frameRate = GetDouble(title, "FrameRate", 0);
        if (frameRate <= 0)
        {
            double num = GetDouble(title, "FrameRateNum", 0);
            double den = GetDouble(title, "FrameRateDen", 0);
            if (num > 0 && den > 0) frameRate = num / den;
        }

        TimeSpan duration = ParseDuration(GetProperty(title, "Duration"));
        JsonElement crop = GetProperty(title, "Crop");
        int[] cropValues = ReadIntArray(crop, 4);

        var audio = ParseTrackList(GetProperty(title, "AudioList"), false);
        var subtitles = ParseTrackList(GetProperty(title, "SubtitleList"), true);
        string videoCodec = GetString(title, "VideoCodec", GetString(title, "VideoCodecName", "Unknown"));

        bool dolbyVision = raw.Contains("Dolby Vision", StringComparison.OrdinalIgnoreCase) ||
                           GetProperty(title, "DolbyVisionConfiguration").ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        bool hdr10Plus = raw.Contains("HDR10+", StringComparison.OrdinalIgnoreCase) || GetBool(title, "HDR10Plus", false);
        bool hdr = dolbyVision || hdr10Plus || raw.Contains("HDR", StringComparison.OrdinalIgnoreCase) ||
                   GetInt(title, "ColorTransfer", 0) is 16 or 18;

        return new ScanResult
        {
            SourcePath = sourcePath,
            TitleName = GetString(title, "Name", Path.GetFileName(sourcePath)),
            Width = width,
            Height = height,
            Duration = duration,
            VideoCodec = videoCodec,
            FrameRate = frameRate,
            CropTop = cropValues[0],
            CropBottom = cropValues[1],
            CropLeft = cropValues[2],
            CropRight = cropValues[3],
            IsHdr = hdr,
            IsHdr10Plus = hdr10Plus,
            IsDolbyVision = dolbyVision,
            AudioTracks = audio,
            SubtitleTracks = subtitles,
            RawScanText = raw
        };
    }

    private static List<TrackItem> ParseTrackList(JsonElement list, bool subtitle)
    {
        var tracks = new List<TrackItem>();
        if (list.ValueKind != JsonValueKind.Array) return tracks;
        int fallback = 1;
        foreach (JsonElement item in list.EnumerateArray())
        {
            int number = GetInt(item, "TrackNumber", GetInt(item, "Track", fallback));
            string language = GetString(item, "Language", "Unknown");
            string code = GetString(item, "LanguageCode", GetString(item, "Lang", "und"));
            string codec = subtitle
                ? GetString(item, "SourceName", GetString(item, "Format", "Unknown"))
                : GetString(item, "CodecName", GetString(item, "Codec", "Unknown"));
            string name = GetString(item, "Name", string.Empty);
            string details = subtitle
                ? BuildSubtitleDetails(item)
                : BuildAudioDetails(item);

            tracks.Add(new TrackItem
            {
                Number = number,
                Language = language,
                Code = code,
                Codec = codec,
                Name = name,
                Details = details,
                IsSubtitle = subtitle
            });
            fallback++;
        }
        return tracks;
    }

    private static string BuildAudioDetails(JsonElement item)
    {
        string layout = GetString(item, "ChannelLayoutName", GetString(item, "ChannelLayout", string.Empty));
        int rate = GetInt(item, "SampleRate", 0);
        int bitrate = GetInt(item, "BitRate", GetInt(item, "Bitrate", 0));
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(layout)) parts.Add(layout);
        if (rate > 0) parts.Add(rate.ToString(CultureInfo.InvariantCulture) + " Hz");
        if (bitrate > 0) parts.Add((bitrate / 1000.0).ToString("0", CultureInfo.InvariantCulture) + " kb/s");
        return string.Join(" | ", parts);
    }

    private static string BuildSubtitleDetails(JsonElement item)
    {
        var parts = new List<string>();
        if (GetBool(item, "CanBurn", false)) parts.Add("can burn");
        if (GetBool(item, "CanForce", false)) parts.Add("forced flag");
        if (GetBool(item, "CanPass", false)) parts.Add("can pass through");
        return string.Join(" | ", parts);
    }

    private static ScanResult ParseTextFallback(string sourcePath, string raw)
    {
        Match geometry = Regex.Match(raw, @"(?<width>\d{3,5})x(?<height>\d{3,5})", RegexOptions.CultureInvariant);
        Match durationMatch = Regex.Match(raw, @"duration:\s*(?<value>\d{2}:\d{2}:\d{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        TimeSpan duration = durationMatch.Success && TimeSpan.TryParse(durationMatch.Groups["value"].Value, CultureInfo.InvariantCulture, out TimeSpan parsed)
            ? parsed
            : TimeSpan.Zero;

        var audio = ParseFallbackTracks(raw, "audio tracks", false);
        var subtitles = ParseFallbackTracks(raw, "subtitle tracks", true);
        int[] crop = [0, 0, 0, 0];
        Match cropMatch = Regex.Match(raw, @"autocrop:\s*(?<t>\d+)\/(?<b>\d+)\/(?<l>\d+)\/(?<r>\d+)", RegexOptions.IgnoreCase);
        if (cropMatch.Success)
        {
            crop = [
                int.Parse(cropMatch.Groups["t"].Value, CultureInfo.InvariantCulture),
                int.Parse(cropMatch.Groups["b"].Value, CultureInfo.InvariantCulture),
                int.Parse(cropMatch.Groups["l"].Value, CultureInfo.InvariantCulture),
                int.Parse(cropMatch.Groups["r"].Value, CultureInfo.InvariantCulture)
            ];
        }

        return new ScanResult
        {
            SourcePath = sourcePath,
            TitleName = Path.GetFileName(sourcePath),
            Width = geometry.Success ? int.Parse(geometry.Groups["width"].Value, CultureInfo.InvariantCulture) : 0,
            Height = geometry.Success ? int.Parse(geometry.Groups["height"].Value, CultureInfo.InvariantCulture) : 0,
            Duration = duration,
            VideoCodec = "See raw scan log",
            CropTop = crop[0],
            CropBottom = crop[1],
            CropLeft = crop[2],
            CropRight = crop[3],
            IsDolbyVision = raw.Contains("Dolby Vision", StringComparison.OrdinalIgnoreCase),
            IsHdr10Plus = raw.Contains("HDR10+", StringComparison.OrdinalIgnoreCase),
            IsHdr = raw.Contains("HDR", StringComparison.OrdinalIgnoreCase),
            AudioTracks = audio,
            SubtitleTracks = subtitles,
            RawScanText = raw
        };
    }

    private static List<TrackItem> ParseFallbackTracks(string text, string heading, bool subtitle)
    {
        var result = new List<TrackItem>();
        int headingIndex = text.IndexOf(heading, StringComparison.OrdinalIgnoreCase);
        if (headingIndex < 0) return result;
        string section = text.Substring(headingIndex, Math.Min(5000, text.Length - headingIndex));
        foreach (Match match in Regex.Matches(section, @"^\s*\+\s*(?<number>\d+),\s*(?<details>.+)$", RegexOptions.Multiline))
        {
            string details = match.Groups["details"].Value.Trim();
            if (details.Contains("tracks", StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(new TrackItem
            {
                Number = int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture),
                Language = details.Split('(', 2)[0].Trim(),
                Code = "und",
                Codec = details.Contains('(') ? details[(details.IndexOf('(') + 1)..].TrimEnd(')') : "Unknown",
                Details = details,
                IsSubtitle = subtitle
            });
            if (result.Count >= 64) break;
        }
        return result;
    }

    private static TimeSpan ParseDuration(JsonElement duration)
    {
        if (duration.ValueKind == JsonValueKind.String && TimeSpan.TryParse(duration.GetString(), CultureInfo.InvariantCulture, out TimeSpan value))
            return value;
        if (duration.ValueKind != JsonValueKind.Object) return TimeSpan.Zero;
        int hours = GetInt(duration, "Hours", 0);
        int minutes = GetInt(duration, "Minutes", 0);
        int seconds = GetInt(duration, "Seconds", 0);
        int ticks = GetInt(duration, "Ticks", 0);
        return new TimeSpan(hours, minutes, seconds) + TimeSpan.FromTicks(ticks);
    }

    private static JsonElement GetProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return default;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        }
        return default;
    }

    private static string GetString(JsonElement element, string name, string fallback)
    {
        JsonElement value = GetProperty(element, name);
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? fallback,
            JsonValueKind.Number => value.ToString(),
            _ => fallback
        };
    }

    private static int GetInt(JsonElement element, string name, int fallback)
    {
        JsonElement value = GetProperty(element, name);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        return fallback;
    }

    private static double GetDouble(JsonElement element, string name, double fallback)
    {
        JsonElement value = GetProperty(element, name);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number)) return number;
        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return number;
        return fallback;
    }

    private static bool GetBool(JsonElement element, string name, bool fallback)
    {
        JsonElement value = GetProperty(element, name);
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) return number != 0;
        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool parsed)) return parsed;
        return fallback;
    }

    private static int[] ReadIntArray(JsonElement element, int count)
    {
        var values = new int[count];
        if (element.ValueKind != JsonValueKind.Array) return values;
        int index = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (index >= count) break;
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int value)) values[index] = value;
            index++;
        }
        return values;
    }

    private static double ParseDouble(string line, string pattern)
    {
        Match match = Regex.Match(line, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : 0;
    }

    private static TimeSpan? ParseEta(string line)
    {
        Match hms = Regex.Match(line, @"ETA\s*(?<h>\d{1,3})h(?<m>\d{1,2})m(?<s>\d{1,2})s", RegexOptions.IgnoreCase);
        if (hms.Success)
        {
            return new TimeSpan(
                int.Parse(hms.Groups["h"].Value, CultureInfo.InvariantCulture),
                int.Parse(hms.Groups["m"].Value, CultureInfo.InvariantCulture),
                int.Parse(hms.Groups["s"].Value, CultureInfo.InvariantCulture));
        }

        Match colon = Regex.Match(line, @"ETA\s*(?<h>\d{1,3}):(?<m>\d{2}):(?<s>\d{2})", RegexOptions.IgnoreCase);
        if (colon.Success)
        {
            return new TimeSpan(
                int.Parse(colon.Groups["h"].Value, CultureInfo.InvariantCulture),
                int.Parse(colon.Groups["m"].Value, CultureInfo.InvariantCulture),
                int.Parse(colon.Groups["s"].Value, CultureInfo.InvariantCulture));
        }

        return null;
    }

    private static string SanitizeFilePart(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return value;
    }

    private static string Tail(string text, int maximumCharacters) =>
        text.Length <= maximumCharacters ? text : text[^maximumCharacters..];

    public void CancelActiveOperation()
    {
        lock (processLock)
        {
            try
            {
                if (activeProcess is { HasExited: false }) activeProcess.Kill(true);
            }
            catch (Exception ex)
            {
                CrashReporter.Write("Failed to terminate HandBrakeCLI", ex);
            }
        }
    }

    public void Dispose()
    {
        CancelActiveOperation();
        lock (processLock)
        {
            activeProcess?.Dispose();
            activeProcess = null;
        }
    }
}
