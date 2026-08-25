#!/usr/bin/env python3
"""Canonical, idempotent source normalizer for the CLI-first Windows application."""

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


def replace_method(text: str, signature: str, replacement: str, description: str) -> str:
    start = text.find(signature)
    if start < 0:
        if replacement in text:
            return text
        raise RuntimeError(f"Could not find {description} signature.")

    brace = text.find("{", start)
    if brace < 0:
        raise RuntimeError(f"Could not find the opening brace for {description}.")

    depth = 0
    in_string = False
    verbatim = False
    escaped = False
    index = brace
    while index < len(text):
        character = text[index]
        if in_string:
            if verbatim:
                if character == '"':
                    if index + 1 < len(text) and text[index + 1] == '"':
                        index += 1
                    else:
                        in_string = False
                        verbatim = False
            else:
                if escaped:
                    escaped = False
                elif character == "\\":
                    escaped = True
                elif character == '"':
                    in_string = False
        else:
            if character == '"':
                in_string = True
                verbatim = index > 0 and text[index - 1] == '@'
            elif character == '{':
                depth += 1
            elif character == '}':
                depth -= 1
                if depth == 0:
                    end = index + 1
                    return text[:start] + replacement + text[end:]
        index += 1

    raise RuntimeError(f"Could not find the closing brace for {description}.")


service_path = "transcencode-cli/src/Transcencode/HandBrakeCliService.cs"
service = read(service_path)
if "using System.Collections.ObjectModel;" not in service:
    service = service.replace(
        "using System.Diagnostics;\n",
        "using System.Collections.ObjectModel;\nusing System.Diagnostics;\n",
        1,
    )
service = service.replace("using System.Text.RegularExpressions;\n", "")

manual_progress = '''    internal static ProgressInfo? TryParseProgress(string line)
    {
        int percentMarker = line.IndexOf('%');
        double? percent = percentMarker >= 0 ? ParseNumberBefore(line, percentMarker) : null;
        if (!percent.HasValue)
        {
            return null;
        }

        int firstFpsMarker = line.IndexOf(" fps", StringComparison.OrdinalIgnoreCase);
        double? currentFps = firstFpsMarker >= 0 ? ParseNumberBefore(line, firstFpsMarker) : null;

        double? averageFps = null;
        int averageMarker = line.IndexOf("avg", StringComparison.OrdinalIgnoreCase);
        if (averageMarker >= 0)
        {
            int averageFpsMarker = line.IndexOf(" fps", averageMarker, StringComparison.OrdinalIgnoreCase);
            if (averageFpsMarker >= 0)
            {
                averageFps = ParseNumberBefore(line, averageFpsMarker);
            }
        }

        TimeSpan? eta = null;
        int etaMarker = line.IndexOf("ETA", StringComparison.OrdinalIgnoreCase);
        if (etaMarker >= 0)
        {
            string etaText = line[(etaMarker + 3)..].TrimStart();
            int etaEnd = etaText.IndexOfAny(new[] { ')', ',', ' ' });
            if (etaEnd >= 0)
            {
                etaText = etaText[..etaEnd];
            }
            eta = ParseEta(etaText);
        }

        return new ProgressInfo
        {
            Percent = Math.Clamp(percent.Value, 0, 100),
            CurrentFps = currentFps,
            AverageFps = averageFps,
            Eta = eta,
            RawLine = line
        };
    }'''
service = replace_method(
    service,
    "    internal static ProgressInfo? TryParseProgress(string line)",
    manual_progress,
    "progress parser",
)

manual_eta = '''    private static TimeSpan? ParseEta(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out TimeSpan parsed))
        {
            return parsed;
        }

        int cursor = 0;
        int hours = 0;
        int minutes = 0;
        int seconds = 0;
        bool foundUnit = false;

        foreach ((char unit, Action<int> assign) in new (char, Action<int>)[]
        {
            ('h', parsedValue => hours = parsedValue),
            ('m', parsedValue => minutes = parsedValue),
            ('s', parsedValue => seconds = parsedValue)
        })
        {
            int unitIndex = value.IndexOf(unit, cursor);
            if (unitIndex < 0)
            {
                continue;
            }

            string numberText = value[cursor..unitIndex];
            if (!int.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) || number < 0)
            {
                return null;
            }

            assign(number);
            cursor = unitIndex + 1;
            foundUnit = true;
        }

        if (!foundUnit || cursor != value.Length)
        {
            return null;
        }

        try
        {
            return new TimeSpan(hours, minutes, seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }'''
service = replace_method(
    service,
    "    private static TimeSpan? ParseEta(string value)",
    manual_eta,
    "ETA parser",
)

if "private static double? ParseNumberBefore" not in service:
    service = replace_once(
        service,
        '''    private static double? ParseNullableDouble(string value)
    {''',
        '''    private static double? ParseNumberBefore(string text, int exclusiveEnd)
    {
        int end = exclusiveEnd - 1;
        while (end >= 0 && char.IsWhiteSpace(text[end]))
        {
            end--;
        }

        if (end < 0)
        {
            return null;
        }

        int start = end;
        while (start >= 0 && (char.IsDigit(text[start]) || text[start] == '.'))
        {
            start--;
        }

        string value = text[(start + 1)..(end + 1)];
        return ParseNullableDouble(value);
    }

    private static double? ParseNullableDouble(string value)
    {''',
        "number-before helper",
    )

# Remove any generated-regex declarations left by an earlier normalizer.
service = re.sub(
    r'''\n    \[GeneratedRegex\(.*?private static partial Regex EtaUnitsRegex\(\);''',
    "",
    service,
    count=1,
    flags=re.S,
)

if "result.StandardOutput" not in service:
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

if "StringBuilder standardOutput" not in service:
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
window = read(window_path).replace("Dispatcher.BeginInvoke(() =>", "Dispatcher.InvokeAsync(() =>")
write(window_path, window)

self_test_path = "transcencode-cli/src/Transcencode/SelfTestRunner.cs"
self_test = read(self_test_path)
self_test = self_test.replace("PreserveAllAudio = true,", "PreserveAllAudio = false,")
self_test = self_test.replace("PreserveAllSubtitles = true,", "PreserveAllSubtitles = false,")
self_test = self_test.replace("ChapterMarkers = true", "ChapterMarkers = false")
write(self_test_path, self_test)

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

print("Canonical Transcencode CLI source normalization completed.")
