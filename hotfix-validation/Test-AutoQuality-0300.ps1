#requires -version 5.1
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

function Assert-True {
    param([bool]$Condition,[string]$Message)
    if (-not $Condition) { throw $Message }
}

function Quote-Argument {
    param([string]$Value)
    if ($null -eq $Value) { return '""' }
    if ($Value -notmatch '[\s"]') { return $Value }
    return '"' + ($Value -replace '(\\*)"','$1$1\"' -replace '(\\+)$','$1$1') + '"'
}

function Join-Arguments {
    param([string[]]$Arguments)
    return (($Arguments | ForEach-Object { Quote-Argument ([string]$_) }) -join ' ')
}

function Copy-LivePreviews {
    param(
        [Parameter(Mandatory=$true)][string]$TemporaryDirectory,
        [Parameter(Mandatory=$true)][string]$CaptureDirectory
    )
    if (-not (Test-Path -LiteralPath $TemporaryDirectory)) { return }
    New-Item -ItemType Directory -Path $CaptureDirectory -Force | Out-Null
    $files = @(Get-ChildItem -LiteralPath $TemporaryDirectory -Filter '*.jpg' -File -Recurse -ErrorAction SilentlyContinue)
    foreach ($file in $files) {
        $destination = Join-Path $CaptureDirectory $file.Name
        if (Test-Path -LiteralPath $destination) { continue }
        try {
            $sourceStream = [IO.File]::Open($file.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
            try {
                $destinationStream = [IO.File]::Open($destination,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
                try { $sourceStream.CopyTo($destinationStream) } finally { $destinationStream.Dispose() }
            }
            finally { $sourceStream.Dispose() }
        }
        catch { }
    }
}

function Invoke-HandBrake {
    param(
        [Parameter(Mandatory=$true)][string]$HandBrakeCli,
        [Parameter(Mandatory=$true)][string[]]$Arguments,
        [string]$TemporaryDirectory = '',
        [string]$CaptureDirectory = ''
    )
    if ($TemporaryDirectory) { New-Item -ItemType Directory -Path $TemporaryDirectory -Force | Out-Null }
    if ($CaptureDirectory) { New-Item -ItemType Directory -Path $CaptureDirectory -Force | Out-Null }

    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $HandBrakeCli
    $start.Arguments = Join-Arguments $Arguments
    $start.WorkingDirectory = Split-Path -Parent $HandBrakeCli
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    if ($TemporaryDirectory) {
        $start.EnvironmentVariables['TEMP'] = $TemporaryDirectory
        $start.EnvironmentVariables['TMP'] = $TemporaryDirectory
    }

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $start
    try {
        Assert-True ($process.Start()) 'Windows could not start HandBrakeCLI.'
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        while (-not $process.HasExited) {
            if ($TemporaryDirectory -and $CaptureDirectory) {
                Copy-LivePreviews -TemporaryDirectory $TemporaryDirectory -CaptureDirectory $CaptureDirectory
            }
            Start-Sleep -Milliseconds 15
        }
        if ($TemporaryDirectory -and $CaptureDirectory) {
            Copy-LivePreviews -TemporaryDirectory $TemporaryDirectory -CaptureDirectory $CaptureDirectory
        }
        $stdout = [string]$stdoutTask.Result
        $stderr = [string]$stderrTask.Result
        return [pscustomobject]@{ ExitCode=[int]$process.ExitCode; StdOut=$stdout; StdErr=$stderr }
    }
    finally { $process.Dispose() }
}

function Get-GrayImage {
    param([Parameter(Mandatory=$true)][string]$Path)
    $bitmap = New-Object System.Windows.Media.Imaging.BitmapImage
    $bitmap.BeginInit()
    $bitmap.CacheOption = [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
    $bitmap.CreateOptions = [System.Windows.Media.Imaging.BitmapCreateOptions]::IgnoreColorProfile
    $bitmap.DecodePixelWidth = 320
    $bitmap.UriSource = [System.Uri]::new([IO.Path]::GetFullPath($Path))
    $bitmap.EndInit()
    $bitmap.Freeze()

    $converted = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap
    $converted.BeginInit()
    $converted.Source = $bitmap
    $converted.DestinationFormat = [System.Windows.Media.PixelFormats]::Gray8
    $converted.EndInit()
    $converted.Freeze()
    $stride = [int]$converted.PixelWidth
    $pixels = New-Object byte[] ($stride * [int]$converted.PixelHeight)
    $converted.CopyPixels($pixels,$stride,0)
    return [pscustomobject]@{ Width=[int]$converted.PixelWidth; Height=[int]$converted.PixelHeight; Stride=$stride; Pixels=$pixels }
}

function Compare-Pictures {
    param([string]$ReferencePath,[string]$CandidatePath)
    $reference = Get-GrayImage $ReferencePath
    $candidate = Get-GrayImage $CandidatePath
    Assert-True ($reference.Width -eq $candidate.Width -and $reference.Height -eq $candidate.Height) 'Decoded comparison dimensions differed.'
    [double]$sumAbsolute = 0
    [double]$sumSquares = 0
    [long]$count = 0
    $hist = New-Object long[] 256
    for ($y=0; $y -lt $reference.Height; $y+=2) {
        $row = $y * $reference.Stride
        for ($x=0; $x -lt $reference.Width; $x+=2) {
            $index = $row + $x
            $difference = [Math]::Abs([int]$reference.Pixels[$index] - [int]$candidate.Pixels[$index])
            $sumAbsolute += $difference
            $sumSquares += ($difference * $difference)
            $hist[$difference]++
            $count++
        }
    }
    $mae = $sumAbsolute / $count
    $mse = $sumSquares / $count
    $psnr = if ($mse -le 0.0000001) { 99.0 } else { 10.0 * [Math]::Log10((255.0 * 255.0) / $mse) }
    $similarity = [Math]::Max(0.0,100.0 * (1.0 - ($mae / 255.0)))
    $target = [long][Math]::Ceiling($count * 0.95)
    [long]$running = 0
    $p95 = 255
    for ($i=0; $i -lt $hist.Count; $i++) { $running += $hist[$i]; if ($running -ge $target) { $p95=$i; break } }
    return [pscustomobject]@{ MAE=$mae; PSNR=$psnr; Similarity=$similarity; P95=$p95 }
}

function Test-VisualGate {
    param([object[]]$Comparisons)
    $averagePsnr = [double](($Comparisons | Measure-Object PSNR -Average).Average)
    $minimumPsnr = [double](($Comparisons | Measure-Object PSNR -Minimum).Minimum)
    $averageSimilarity = [double](($Comparisons | Measure-Object Similarity -Average).Average)
    $averageMae = [double](($Comparisons | Measure-Object MAE -Average).Average)
    $maximumP95 = [int](($Comparisons | Measure-Object P95 -Maximum).Maximum)
    return ($averagePsnr -ge 42.0 -and $minimumPsnr -ge 39.0 -and $averageSimilarity -ge 99.0 -and $averageMae -le 2.55 -and $maximumP95 -le 12)
}

function Get-PreviewFiles {
    param([string]$Directory)
    return [IO.FileInfo[]]@(Get-ChildItem -LiteralPath $Directory -Filter '*.jpg' -File | Sort-Object Name)
}

$handBrake = [IO.Path]::GetFullPath($env:TRANSCENCODE_HBCLI)
$source = [IO.Path]::GetFullPath($env:TRANSCENCODE_TEST_SOURCE)
Assert-True (Test-Path -LiteralPath $handBrake) 'TRANSCENCODE_HBCLI does not exist.'
Assert-True (Test-Path -LiteralPath $source) 'TRANSCENCODE_TEST_SOURCE does not exist.'

$root = Join-Path $env:RUNNER_TEMP ('transcencode-auto-quality-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
try {
    $scanTemp = Join-Path $root 'source-hb-temp'
    $scanCapture = Join-Path $root 'source-previews'
    $scanArgs = @('--json','--scan','-t','1','--min-duration','0','--previews','12:1','-i',$source)
    $scan = Invoke-HandBrake -HandBrakeCli $handBrake -Arguments $scanArgs -TemporaryDirectory $scanTemp -CaptureDirectory $scanCapture
    Assert-True ($scan.ExitCode -eq 0) ("HandBrake source preview scan failed: `n" + $scan.StdErr)
    $sourcePreviews = Get-PreviewFiles $scanCapture
    Assert-True ($sourcePreviews.Count -ge 2) "Live-copy capture found only $($sourcePreviews.Count) source JPEG preview(s)."
    $decoded = Get-GrayImage $sourcePreviews[0].FullName
    Assert-True ($decoded.Width -ge 300 -and $decoded.Height -gt 50) 'A live-captured JPEG could not be decoded through WPF.'
    Write-Host ("PASS source preview live-copy: {0} persistent JPEGs; first decoded {1}x{2}." -f $sourcePreviews.Count,$decoded.Width,$decoded.Height)

    $reference = Join-Path $root 'reference-q1.mkv'
    $poor = Join-Path $root 'candidate-q40.mkv'
    foreach ($pair in @(@($reference,'1'),@($poor,'40'))) {
        $args = @('--json','-i',$source,'-o',$pair[0],'-f','av_mkv','-e','x264','-q',$pair[1],'--encoder-preset','fast','--vfr','--crop-mode','none','-a','none','-s','none','--start-at','seconds:3','--stop-at','seconds:5')
        $run = Invoke-HandBrake -HandBrakeCli $handBrake -Arguments $args
        Assert-True ($run.ExitCode -eq 0 -and (Test-Path -LiteralPath $pair[0])) "Calibration test encode q$($pair[1]) failed."
    }

    $referenceTemp = Join-Path $root 'reference-temp'
    $referenceCapture = Join-Path $root 'reference-previews'
    $referenceScan = Invoke-HandBrake -HandBrakeCli $handBrake -Arguments @('--json','--scan','-t','1','--min-duration','0','--previews','3:1','-i',$reference) -TemporaryDirectory $referenceTemp -CaptureDirectory $referenceCapture
    Assert-True ($referenceScan.ExitCode -eq 0) 'Reference preview scan failed.'
    $referencePreviews = Get-PreviewFiles $referenceCapture
    Assert-True ($referencePreviews.Count -ge 1) 'Reference live-copy preview capture produced no JPEG.'

    $poorTemp = Join-Path $root 'poor-temp'
    $poorCapture = Join-Path $root 'poor-previews'
    $poorScan = Invoke-HandBrake -HandBrakeCli $handBrake -Arguments @('--json','--scan','-t','1','--min-duration','0','--previews','3:1','-i',$poor) -TemporaryDirectory $poorTemp -CaptureDirectory $poorCapture
    Assert-True ($poorScan.ExitCode -eq 0) 'Poor candidate preview scan failed.'
    $poorPreviews = Get-PreviewFiles $poorCapture
    Assert-True ($poorPreviews.Count -ge 1) 'Poor candidate live-copy preview capture produced no JPEG.'

    $selfComparison = Compare-Pictures $referencePreviews[0].FullName $referencePreviews[0].FullName
    Assert-True (Test-VisualGate @($selfComparison)) 'An identical picture did not pass the visual-transparency gate.'
    $poorComparison = Compare-Pictures $referencePreviews[0].FullName $poorPreviews[0].FullName
    Assert-True ($poorComparison.MAE -gt $selfComparison.MAE) 'A deliberately low-quality candidate did not measure worse than the reference.'
    Assert-True ($poorComparison.PSNR -lt $selfComparison.PSNR) 'A deliberately low-quality candidate did not reduce measured PSNR.'
    Write-Host ("PASS decoded-picture comparison: self PSNR {0:0.0} dB, q40 PSNR {1:0.0} dB; q40 MAE {2:0.00}." -f $selfComparison.PSNR,$poorComparison.PSNR,$poorComparison.MAE)

    Write-Host 'TRANSCENCODE_0300_AUTO_QUALITY_WINDOWS_TEST_PASSED'
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
