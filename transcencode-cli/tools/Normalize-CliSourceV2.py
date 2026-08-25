#!/usr/bin/env python3
"""Idempotently harden the CLI-first Transcencode source before build and release."""

from __future__ import annotations

import pathlib
import re

ROOT = pathlib.Path(__file__).resolve().parents[2]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def write(path: str, content: str) -> None:
    (ROOT / path).write_text(content, encoding="utf-8-sig")


def replace_once(text: str, old: str, new: str, description: str) -> str:
    count = text.count(old)
    if count == 0 and new in text:
        return text
    if count != 1:
        raise RuntimeError(f"Expected one {description} anchor; found {count}.")
    return text.replace(old, new, 1)


def replace_regex_once(text: str, pattern: str, replacement: str, description: str) -> str:
    updated, count = re.subn(pattern, replacement, text, count=1, flags=re.S)
    if count == 0 and replacement in text:
        return text
    if count != 1:
        raise RuntimeError(f"Expected one {description} regex anchor; found {count}.")
    return updated


# Ensure the scan parser compiles both in the WPF application and in the linked console tests.
service_path = "transcencode-cli/src/Transcencode/HandBrakeCliService.cs"
service = read(service_path)
if "using System.Collections.ObjectModel;" not in service:
    service = service.replace(
        "using System.Diagnostics;\n",
        "using System.Collections.ObjectModel;\nusing System.Diagnostics;\n",
        1,
    )

# Parse percent, FPS, and ETA independently. A single expression with optional groups could
# legally stop after the percent and silently omit all remaining progress information.
progress_method = r'''    internal static ProgressInfo\? TryParseProgress\(string line\)\n    \{.*?\n    \}\n\n    internal static string Tail'''
progress_replacement = '''    internal static ProgressInfo? TryParseProgress(string line)
    {
        Match percentMatch = PercentRegex().Match(line);
        if (!percentMatch.Success ||
            !double.TryParse(percentMatch.Groups["percent"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
        {
            return null;
        }

        Match fpsMatch = FpsRegex().Match(line);
        Match etaMatch = EtaRegex().Match(line);
        double? currentFps = fpsMatch.Success ? ParseNullableDouble(fpsMatch.Groups["fps"].Value) : null;
        double? averageFps = fpsMatch.Success ? ParseNullableDouble(fpsMatch.Groups["avg"].Value) : null;
        TimeSpan? eta = etaMatch.Success ? ParseEta(etaMatch.Groups["eta"].Value) : null;

        return new ProgressInfo
        {
            Percent = Math.Clamp(percent, 0, 100),
            CurrentFps = currentFps,
            AverageFps = averageFps,
            Eta = eta,
            RawLine = line
        };
    }

    internal static string Tail'''
service = replace_regex_once(service, progress_method, progress_replacement, "progress parser")

regex_block = r'''    \[GeneratedRegex\(.*?private static partial Regex EtaUnitsRegex\(\);'''
regex_replacement = '''    [GeneratedRegex(@"(?<percent>\\d+(?:\\.\\d+)?)\\s*%", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"(?<fps>\\d+(?:\\.\\d+)?)\\s*fps(?:,\\s*avg\\s*(?<avg>\\d+(?:\\.\\d+)?)\\s*fps)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FpsRegex();

    [GeneratedRegex(@"ETA\\s*(?<eta>(?:(?:\\d+h)(?:\\d+m)?(?:\\d+s)?|(?:\\d+m)(?:\\d+s)?|\\d+s|\\d{1,2}:\\d{2}:\\d{2}|unknown))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EtaRegex();

    [GeneratedRegex(@"^(?:(?<h>\\d+)h)?(?:(?<m>\\d+)m)?(?:(?<s>\\d+)s)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EtaUnitsRegex();'''
service = replace_regex_once(service, regex_block, regex_replacement, "generated regex block")

# Keep stdout and stderr independently so JSON emitted on one stream cannot be corrupted by
# diagnostic lines arriving from the other stream.
service = replace_once(
    service,
    '''        return SourceScanParser.Parse(inputPath, result.CombinedOutput);''',
    '''        Exception? parseFailure = null;
        foreach (string candidate in new[] { result.StandardOutput, result.StandardError, result.CombinedOutput })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                return SourceScanParser.Parse(inputPath, candidate);
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException)
            {
                parseFailure = exception;
            }
        }

        throw new InvalidDataException(
            "HandBrakeCLI completed the scan, but Transcencode could not parse its JSON title information. " +
            "The final engine output is available in Live Engine and the diagnostic log.",
            parseFailure);''',
    "scan parser stream selection",
)

service = replace_once(
    service,
    '''        StringBuilder combined = new();
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
        }''',
    '''        StringBuilder combined = new();
        StringBuilder standardOutput = new();
        StringBuilder standardError = new();
        object outputLock = new();
        ProgressInfo? lastProgress = null;

        void ReceiveLine(string line, bool isError)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            lock (outputLock)
            {
                (isError ? standardError : standardOutput).AppendLine(line);
                combined.AppendLine(line);
            }

            onLine?.Invoke(line);
            ProgressInfo? progress = TryParseProgress(line);
            if (progress is not null)
            {
                lastProgress = progress;
                onProgress?.Invoke(progress);
            }
        }''',
    "separate stream builders",
)
service = replace_once(
    service,
    '''        Task stdout = ConsumeStreamAsync(process.StandardOutput, ReceiveLine, cancellationToken);
        Task stderr = ConsumeStreamAsync(process.StandardError, ReceiveLine, cancellationToken);''',
    '''        Task stdout = ConsumeStreamAsync(process.StandardOutput, line => ReceiveLine(line, false), cancellationToken);
        Task stderr = ConsumeStreamAsync(process.StandardError, line => ReceiveLine(line, true), cancellationToken);''',
    "separate stream consumers",
)
service = replace_once(
    service,
    '''            ExitCode = process.ExitCode,
            CombinedOutput = combined.ToString(),
            LastProgress = lastProgress''',
    '''            ExitCode = process.ExitCode,
            StandardOutput = standardOutput.ToString(),
            StandardError = standardError.ToString(),
            CombinedOutput = combined.ToString(),
            LastProgress = lastProgress''',
    "process result streams",
)
write(service_path, service)

models_path = "transcencode-cli/src/Transcencode/Models.cs"
models = read(models_path)
if "public string StandardOutput" not in models:
    models = replace_once(
        models,
        '''public sealed class ProcessResult
{
    public int ExitCode { get; init; }
    public string CombinedOutput { get; init; } = string.Empty;''',
        '''public sealed class ProcessResult
{
    public int ExitCode { get; init; }
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;
    public string CombinedOutput { get; init; } = string.Empty;''',
        "process result model",
    )
write(models_path, models)

command_path = "transcencode-cli/src/Transcencode/HandBrakeCommandBuilder.cs"
command = read(command_path)

# Preserve Windows paths in the on-screen command preview. Backslashes are ordinary argument
# characters; only embedded quotation marks need escaping for display.
command = re.sub(
    r'''return '\"' \+ value\.Replace\("\\\\", "\\\\\\\\", StringComparison\.Ordinal\)\s*\.Replace\("\\\"", "\\\\\\\"", StringComparison\.Ordinal\) \+ '\"';''',
    '''return '\"' + value.Replace("\\\"", "\\\\\\\"", StringComparison.Ordinal) + '\"';''',
    command,
    count=1,
)

if '"--format", GetContainerName(options.OutputPath),' not in command:
    command = replace_once(
        command,
        '''            "--output", options.OutputPath,
            "--encoder", options.Encoder.CliName,''',
        '''            "--output", options.OutputPath,
            "--format", GetContainerName(options.OutputPath),
            "--encoder", options.Encoder.CliName,''',
        "explicit output container",
    )

command = command.replace('arguments.Add("automatic");', 'arguments.Add("auto");')

if "private static string GetContainerName" not in command:
    command = replace_once(
        command,
        '''    private static void AddCrop(List<string> arguments, EncodeOptions options)
    {''',
        '''    private static string GetContainerName(string outputPath)
    {
        return Path.GetExtension(outputPath).ToLowerInvariant() switch
        {
            ".mp4" or ".m4v" => "av_mp4",
            ".webm" => "av_webm",
            _ => "av_mkv"
        };
    }

    private static void AddCrop(List<string> arguments, EncodeOptions options)
    {''',
        "output container helper",
    )
write(command_path, command)

window_path = "transcencode-cli/src/Transcencode/MainWindow.xaml.cs"
window = read(window_path)
window = window.replace("Dispatcher.BeginInvoke(() =>", "Dispatcher.InvokeAsync(() =>")
write(window_path, window)

self_test_path = "transcencode-cli/src/Transcencode/SelfTestRunner.cs"
self_test = read(self_test_path)
self_test = self_test.replace("PreserveAllAudio = true,", "PreserveAllAudio = false,")
self_test = self_test.replace("PreserveAllSubtitles = true,", "PreserveAllSubtitles = false,")
self_test = self_test.replace("ChapterMarkers = true", "ChapterMarkers = false")
write(self_test_path, self_test)

# The dependency-free tests link the service file directly, so include the app's global usings too.
test_project_path = "transcencode-cli/tests/Transcencode.CommandTests/Transcencode.CommandTests.csproj"
test_project = read(test_project_path)
if "GlobalUsings.cs" not in test_project:
    test_project = replace_once(
        test_project,
        '''    <Compile Include="../../src/Transcencode/Models.cs" Link="Models.cs" />''',
        '''    <Compile Include="../../src/Transcencode/GlobalUsings.cs" Link="GlobalUsings.cs" />
    <Compile Include="../../src/Transcencode/Models.cs" Link="Models.cs" />''',
        "test global usings include",
    )
write(test_project_path, test_project)

# Normalize command-preview assertions to expect normal Windows paths rather than doubled slashes.
test_path = "transcencode-cli/tests/Transcencode.CommandTests/Program.cs"
tests = read(test_path)
tests = tests.replace(
    '"\\\"C:\\\\Program Files\\\\Transcencode\\\\HandBrakeCLI.exe\\\""',
    '"\\\"C:\\Program Files\\Transcencode\\HandBrakeCLI.exe\\\""',
)
tests = tests.replace(
    '"\\\"C:\\\\Input Files\\\\movie.mkv\\\""',
    '"\\\"C:\\Input Files\\movie.mkv\\\""',
)
if 'ContainsPair(arguments, "--format", "av_mkv")' not in tests:
    tests = replace_once(
        tests,
        '''Check(ContainsPair(arguments, "--quality", "16"), "Quality value was not emitted.");''',
        '''Check(ContainsPair(arguments, "--quality", "16"), "Quality value was not emitted.");
Check(ContainsPair(arguments, "--format", "av_mkv"), "The Matroska output container was not emitted.");''',
        "container assertion",
    )
write(test_path, tests)

print("Transcencode CLI source normalization V2 completed.")
