[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir,

    [Parameter(Mandatory = $true)]
    [string]$SmokeDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$publish = (Resolve-Path $PublishDir).Path
New-Item -ItemType Directory -Path $SmokeDir -Force | Out-Null
$smoke = (Resolve-Path $SmokeDir).Path
$cli = Join-Path $publish 'HandBrakeCLI.exe'

foreach ($required in @($cli, (Join-Path $publish 'HandBrake.exe'), (Join-Path $publish 'HandBrake.Worker.exe'), (Join-Path $publish 'hb.dll'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "CLI regression is missing required runtime file: $required"
    }
}
foreach ($tool in @('ffmpeg', 'ffprobe')) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "$tool must be installed before Run-CliRegression.ps1 is called."
    }
}

function Invoke-Cli {
    param(
        [Parameter(Mandatory = $true)][string]$LogName,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $logPath = Join-Path $smoke $LogName
    & $cli @Arguments 2>&1 | Tee-Object -FilePath $logPath | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "HandBrakeCLI failed with exit code $LASTEXITCODE. See $logPath"
    }

    return Get-Content $logPath -Raw
}

function Get-Probe {
    param([Parameter(Mandatory = $true)][string]$Path)

    $json = & ffprobe -v error -show_entries 'stream=index,codec_type,width,height:stream_tags=language,title' -of json $Path
    if ($LASTEXITCODE -ne 0) {
        throw "ffprobe failed for $Path"
    }

    return ($json -join [Environment]::NewLine) | ConvertFrom-Json
}

$english = Join-Path $smoke 'en.srt'
$spanish = Join-Path $smoke 'es.srt'
$source = Join-Path $smoke 'source.mkv'

@"
1
00:00:00,000 --> 00:00:04,000
English subtitle smoke test
"@ | Set-Content $english -Encoding UTF8

@"
1
00:00:00,000 --> 00:00:04,000
Prueba de subtitulos en espanol
"@ | Set-Content $spanish -Encoding UTF8

# The source is deliberately longer than HandBrake's default 10-second minimum title duration.
& ffmpeg -hide_banner -loglevel error -y `
    -f lavfi -i 'color=c=black:size=1280x720:rate=30:duration=12' `
    -f lavfi -i 'testsrc2=size=1280x536:rate=30:duration=12' `
    -f lavfi -i 'sine=frequency=440:sample_rate=48000:duration=12' `
    -f lavfi -i 'sine=frequency=660:sample_rate=48000:duration=12' `
    -i $english -i $spanish `
    -filter_complex '[0:v][1:v]overlay=0:92[v]' `
    -map '[v]' -map 2:a:0 -map 3:a:0 -map 4:s:0 -map 5:s:0 `
    -c:v libx264 -preset ultrafast -crf 18 -pix_fmt yuv420p `
    -c:a aac -b:a 128k -c:s srt `
    -metadata:s:a:0 language=eng -metadata:s:a:0 title='English Test' `
    -metadata:s:a:1 language=spa -metadata:s:a:1 title='Spanish Test' `
    -metadata:s:s:0 language=eng -metadata:s:s:0 title='English Test' `
    -metadata:s:s:1 language=spa -metadata:s:s:1 title='Spanish Test' `
    $source
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $source)) {
    throw 'ffmpeg did not create the black-bar, multilingual regression source.'
}

$sourceProbe = Get-Probe $source
$sourceAudio = @($sourceProbe.streams | Where-Object codec_type -eq 'audio')
$sourceSubtitles = @($sourceProbe.streams | Where-Object codec_type -eq 'subtitle')
if ($sourceAudio.Count -ne 2 -or $sourceSubtitles.Count -ne 2) {
    throw "Regression source did not contain 2 audio and 2 subtitle tracks. Audio=$($sourceAudio.Count), subtitles=$($sourceSubtitles.Count)"
}
$audioLanguages = @($sourceAudio | ForEach-Object { $_.tags.language })
$subtitleLanguages = @($sourceSubtitles | ForEach-Object { $_.tags.language })
foreach ($language in @('eng', 'spa')) {
    if ($audioLanguages -notcontains $language) { throw "Regression source is missing $language audio." }
    if ($subtitleLanguages -notcontains $language) { throw "Regression source is missing $language subtitles." }
}

$scan = Invoke-Cli -LogName 'source-scan.txt' -Arguments @('--scan', '-i', $source)
if ($scan -notmatch '(?i)autocrop\s*(?:=|:)\s*[1-9]\d*/[1-9]\d*/0/0') {
    throw 'HandBrake did not detect the intended top and bottom black bars.'
}

$preserve = Join-Path $smoke 'output-preserve.mkv'
Invoke-Cli -LogName 'encode-preserve.txt' -Arguments @(
    '-i', $source,
    '-o', $preserve,
    '-e', 'x264',
    '-q', '22',
    '--encoder-preset', 'veryfast',
    '--crop-mode', 'none',
    '--all-audio',
    '--all-subtitles') | Out-Null

$preserveProbe = Get-Probe $preserve
$preserveVideo = @($preserveProbe.streams | Where-Object codec_type -eq 'video') | Select-Object -First 1
$preserveAudio = @($preserveProbe.streams | Where-Object codec_type -eq 'audio')
$preserveSubtitles = @($preserveProbe.streams | Where-Object codec_type -eq 'subtitle')
if ($preserveVideo.width -ne 1280 -or $preserveVideo.height -ne 720) {
    throw "Same as source failed to preserve 1280x720. Actual: $($preserveVideo.width)x$($preserveVideo.height)"
}
if ($preserveAudio.Count -ne 2 -or $preserveSubtitles.Count -ne 2) {
    throw "Same-as-source output did not retain all tracks. Audio=$($preserveAudio.Count), subtitles=$($preserveSubtitles.Count)"
}

$cropped = Join-Path $smoke 'output-auto-crop.mkv'
Invoke-Cli -LogName 'encode-auto-crop.txt' -Arguments @(
    '-i', $source,
    '-o', $cropped,
    '-e', 'x264',
    '-q', '22',
    '--encoder-preset', 'veryfast',
    '--crop-mode', 'auto',
    '-a', '1',
    '-s', 'none') | Out-Null

$croppedProbe = Get-Probe $cropped
$croppedVideo = @($croppedProbe.streams | Where-Object codec_type -eq 'video') | Select-Object -First 1
if ($croppedVideo.height -ge 720) {
    throw "Automatic crop did not remove the detected bars. Output height: $($croppedVideo.height)"
}

@"
Transcencode CLI regression passed.

PASS: 12-second source exceeded HandBrake's default minimum title duration.
PASS: English and Spanish audio/subtitle tracks were present.
PASS: HandBrake detected top and bottom black bars.
PASS: Same as source retained the full 1280x720 frame and all four tracks.
PASS: Automatic crop produced a frame shorter than 720 pixels.
"@ | Set-Content (Join-Path $smoke 'CLI-REGRESSION-PASSED.txt') -Encoding UTF8

Write-Host 'Transcencode CLI regression passed.'
