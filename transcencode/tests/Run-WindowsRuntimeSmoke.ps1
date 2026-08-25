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
$app = Join-Path $publish 'HandBrake.exe'

foreach ($required in @($cli, $app, (Join-Path $publish 'HandBrake.Worker.exe'), (Join-Path $publish 'hb.dll'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Runtime smoke test is missing required file: $required"
    }
}

if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) {
    throw 'ffmpeg must be installed before Run-WindowsRuntimeSmoke.ps1 is called.'
}

function Write-RegressionSource {
    $english = Join-Path $smoke 'en.srt'
    $spanish = Join-Path $smoke 'es.srt'
    $source = Join-Path $smoke 'source.mkv'

    @"
1
00:00:00,000 --> 00:00:02,500
English subtitle smoke test
"@ | Set-Content $english -Encoding UTF8

    @"
1
00:00:00,000 --> 00:00:02,500
Prueba de subtitulos en espanol
"@ | Set-Content $spanish -Encoding UTF8

    & ffmpeg -hide_banner -loglevel error -y `
        -f lavfi -i 'color=c=black:size=1280x720:rate=30:duration=8' `
        -f lavfi -i 'testsrc2=size=1280x536:rate=30:duration=8' `
        -f lavfi -i 'sine=frequency=440:sample_rate=48000:duration=8' `
        -f lavfi -i 'sine=frequency=660:sample_rate=48000:duration=8' `
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

    return $source
}

function Invoke-CliToLog {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LogName,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $logPath = Join-Path $smoke $LogName
    & $cli @Arguments 2>&1 | Tee-Object -FilePath $logPath
    $exitCode = $LASTEXITCODE
    $text = Get-Content $logPath -Raw
    if ($exitCode -ne 0) {
        throw "HandBrakeCLI failed with exit code $exitCode. See $logPath"
    }

    return $text
}

function Test-CliPipeline {
    param([Parameter(Mandatory = $true)][string]$Source)

    $scan = Invoke-CliToLog -LogName 'source-scan.txt' -Arguments @('--scan', '-i', $Source)
    if ($scan -notmatch '(?i)English|eng') {
        throw 'Source scan did not expose English audio/subtitle metadata.'
    }
    if ($scan -notmatch '(?i)Spanish|spa|español') {
        throw 'Source scan did not expose Spanish audio/subtitle metadata.'
    }
    if ($scan -notmatch '(?i)autocrop\s*(?:=|:)\s*[1-9]\d*/[1-9]\d*/0/0') {
        throw 'The regression source did not produce detectable top and bottom black bars.'
    }

    $preserve = Join-Path $smoke 'output-preserve.mkv'
    Invoke-CliToLog -LogName 'encode-preserve.txt' -Arguments @(
        '-i', $Source,
        '-o', $preserve,
        '-e', 'x264',
        '-q', '22',
        '--encoder-preset', 'veryfast',
        '--crop-mode', 'none',
        '--all-audio',
        '--all-subtitles') | Out-Null

    $preserveScan = Invoke-CliToLog -LogName 'output-preserve-scan.txt' -Arguments @('--scan', '-i', $preserve)
    if ($preserveScan -notmatch '1280x720') {
        throw 'Same as source did not preserve the complete 1280x720 frame and original black bars.'
    }
    if ($preserveScan -notmatch '(?s)audio tracks:.*?\+\s*1,.*?\+\s*2,') {
        throw 'Same-as-source output did not retain both audio tracks.'
    }
    if ($preserveScan -notmatch '(?s)subtitle tracks:.*?\+\s*1,.*?\+\s*2,') {
        throw 'Same-as-source output did not retain both subtitle tracks.'
    }

    $cropped = Join-Path $smoke 'output-auto-crop.mkv'
    Invoke-CliToLog -LogName 'encode-auto-crop.txt' -Arguments @(
        '-i', $Source,
        '-o', $cropped,
        '-e', 'x264',
        '-q', '22',
        '--encoder-preset', 'veryfast',
        '--crop-mode', 'auto',
        '-a', '1',
        '-s', 'none') | Out-Null

    $croppedScan = Invoke-CliToLog -LogName 'output-auto-crop-scan.txt' -Arguments @('--scan', '-i', $cropped)
    $sizeMatch = [regex]::Match($croppedScan, '(?i)\+\s*size:\s*(\d+)x(\d+)')
    if (-not $sizeMatch.Success) {
        throw 'Could not read automatic-crop output dimensions.'
    }
    if ([int]$sizeMatch.Groups[2].Value -ge 720) {
        throw "Automatic crop did not remove the detected bars; output height was $($sizeMatch.Groups[2].Value)."
    }
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function New-PropertyCondition {
    param($Property, $Value)
    return New-Object System.Windows.Automation.PropertyCondition($Property, $Value)
}

function Find-Element {
    param(
        [Parameter(Mandatory = $true)]
        [System.Windows.Automation.AutomationElement]$Root,

        [Parameter(Mandatory = $true)]
        [System.Windows.Automation.ControlType]$ControlType,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $condition = New-Object System.Windows.Automation.AndCondition(
        (New-PropertyCondition `
            -Property ([System.Windows.Automation.AutomationElement]::ControlTypeProperty) `
            -Value $ControlType),
        (New-PropertyCondition `
            -Property ([System.Windows.Automation.AutomationElement]::NameProperty) `
            -Value $Name))

    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Get-TabNames {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Root)

    $condition = New-PropertyCondition `
        -Property ([System.Windows.Automation.AutomationElement]::ControlTypeProperty) `
        -Value ([System.Windows.Automation.ControlType]::TabItem)
    $items = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    $names = @()
    foreach ($item in $items) {
        try {
            if (-not [string]::IsNullOrWhiteSpace($item.Current.Name)) {
                $names += $item.Current.Name
            }
        }
        catch {
        }
    }

    return @($names | Sort-Object -Unique)
}

function Get-AllAutomationNames {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Root)

    $items = $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $names = @()
    foreach ($item in $items) {
        try {
            if (-not [string]::IsNullOrWhiteSpace($item.Current.Name)) {
                $names += $item.Current.Name
            }
        }
        catch {
        }
    }

    return @($names | Sort-Object -Unique)
}

function Get-ProcessWindows {
    param([Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process)

    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $condition = New-Object System.Windows.Automation.AndCondition(
        (New-PropertyCondition `
            -Property ([System.Windows.Automation.AutomationElement]::ControlTypeProperty) `
            -Value ([System.Windows.Automation.ControlType]::Window)),
        (New-PropertyCondition `
            -Property ([System.Windows.Automation.AutomationElement]::ProcessIdProperty) `
            -Value $Process.Id))

    return $desktop.FindAll([System.Windows.Automation.TreeScope]::Children, $condition)
}

function Wait-ForMainWindow {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory = $true)]
        [string[]]$RequiredTabs,

        [int]$TimeoutSeconds = 100
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastSnapshot = @()
    do {
        Start-Sleep -Milliseconds 500
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "Transcencode exited while its main window was loading with code $($Process.ExitCode)."
        }

        $windows = Get-ProcessWindows -Process $Process
        foreach ($window in $windows) {
            try {
                $tabs = Get-TabNames -Root $window
                if ($tabs.Count -gt 0) {
                    $lastSnapshot = $tabs
                }
                $missing = @($RequiredTabs | Where-Object { $tabs -notcontains $_ })
                if ($missing.Count -eq 0) {
                    return $window
                }
            }
            catch {
            }
        }
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Could not find a Transcencode main window containing all required tabs. Last tab snapshot: $($lastSnapshot -join ', ')"
}

function Select-Tab {
    param(
        [Parameter(Mandatory = $true)]
        [System.Windows.Automation.AutomationElement]$Root,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $tab = Find-Element -Root $Root -ControlType ([System.Windows.Automation.ControlType]::TabItem) -Name $Name
    if ($null -eq $tab) {
        throw "Could not find native tab '$Name'."
    }

    $pattern = $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $pattern.Select()
    Start-Sleep -Milliseconds 600
}

function Wait-ForAutomationName {
    param(
        [Parameter(Mandatory = $true)]
        [System.Windows.Automation.AutomationElement]$Root,

        [Parameter(Mandatory = $true)]
        [string]$Pattern,

        [int]$TimeoutSeconds = 30
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $names = Get-AllAutomationNames -Root $Root
        $match = $names | Where-Object { $_ -match $Pattern } | Select-Object -First 1
        if ($null -ne $match) {
            return $match
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)

    return $null
}

function Test-GuiPipeline {
    param([Parameter(Mandatory = $true)][string]$Source)

    $appData = Join-Path $smoke 'AppData'
    $localAppData = Join-Path $smoke 'LocalAppData'
    New-Item -ItemType Directory -Path $appData, $localAppData -Force | Out-Null

    $oldAppData = $env:APPDATA
    $oldLocalAppData = $env:LOCALAPPDATA
    $env:APPDATA = $appData
    $env:LOCALAPPDATA = $localAppData

    $requiredTabs = @(
        'Summary',
        'Dimensions',
        'Filters',
        'Video',
        'Audio',
        'Subtitles',
        'Chapters',
        'Analyze',
        'Source Tracks',
        'Upscale & Enhance',
        'Verify',
        'Live Engine')

    $process = $null
    try {
        $process = Start-Process `
            -FilePath $app `
            -ArgumentList @($Source, '--no-hardware') `
            -WorkingDirectory $publish `
            -PassThru

        $root = Wait-ForMainWindow -Process $process -RequiredTabs $requiredTabs -TimeoutSeconds 100
        $tabs = Get-TabNames -Root $root
        $tabs | Set-Content (Join-Path $smoke 'ui-tabs.txt') -Encoding UTF8

        if ($root.Current.Name -notmatch '(?i)Transcencode') {
            throw "The loaded native main window is not branded Transcencode. Actual title: '$($root.Current.Name)'"
        }

        Select-Tab -Root $root -Name 'Source Tracks'
        $trackSummary = Wait-ForAutomationName `
            -Root $root `
            -Pattern '^Source contains 2 audio tracks and 2 subtitle tracks\.' `
            -TimeoutSeconds 25
        if ($null -eq $trackSummary) {
            Get-AllAutomationNames -Root $root | Set-Content (Join-Path $smoke 'source-tracks-ui.txt') -Encoding UTF8
            throw 'Source Tracks did not expose the expected two audio and two subtitle tracks.'
        }

        $trackNames = Get-AllAutomationNames -Root $root
        $trackNames | Set-Content (Join-Path $smoke 'source-tracks-ui.txt') -Encoding UTF8
        if (($trackNames -match '(?i)English').Count -eq 0) {
            throw 'Source Tracks did not expose the English source language.'
        }
        if (($trackNames -match '(?i)Spanish|español').Count -eq 0) {
            throw 'Source Tracks did not expose the Spanish source language.'
        }

        Select-Tab -Root $root -Name 'Analyze'
        $analyzeButton = Find-Element `
            -Root $root `
            -ControlType ([System.Windows.Automation.ControlType]::Button) `
            -Name 'Run Deep Analyze'
        if ($null -eq $analyzeButton -or -not $analyzeButton.Current.IsEnabled) {
            throw 'Run Deep Analyze was not available after loading a valid source.'
        }
        $analyzeButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

        $analysisComplete = Wait-ForAutomationName `
            -Root $root `
            -Pattern '^Deep Analyze complete\.$' `
            -TimeoutSeconds 100
        if ($null -eq $analysisComplete) {
            $analysisNames = Get-AllAutomationNames -Root $root
            $analysisNames | Set-Content (Join-Path $smoke 'analyze-ui.txt') -Encoding UTF8
            $failure = $analysisNames | Where-Object { $_ -match '^Deep Analyze failed:' } | Select-Object -First 1
            if ($null -ne $failure) {
                throw $failure
            }
            throw 'Deep Analyze did not complete within 100 seconds.'
        }

        $analysisNames = Get-AllAutomationNames -Root $root
        $analysisNames | Set-Content (Join-Path $smoke 'analyze-ui.txt') -Encoding UTF8
        $recommendation = $analysisNames | Where-Object { $_ -match '^Recommended starting point for ' } | Select-Object -First 1
        if ($null -eq $recommendation) {
            throw 'Deep Analyze completed without an encoder-aware quality recommendation.'
        }

        $applyButton = Find-Element `
            -Root $root `
            -ControlType ([System.Windows.Automation.ControlType]::Button) `
            -Name 'Apply Recommendation to Video Tab'
        if ($null -eq $applyButton -or -not $applyButton.Current.IsEnabled) {
            throw 'The Analyze recommendation could not be applied to HandBrake Video settings.'
        }
        $applyButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

        $applied = Wait-ForAutomationName `
            -Root $root `
            -Pattern '^Applied Constant Quality \d+ to HandBrake''s Video tab\.$' `
            -TimeoutSeconds 15
        if ($null -eq $applied) {
            throw 'Analyze did not confirm that the recommended quality reached the native Video model.'
        }
        $recommendedQuality = [regex]::Match($applied, '\d+').Value

        Select-Tab -Root $root -Name 'Video'
        $videoNames = Get-AllAutomationNames -Root $root
        $videoNames | Set-Content (Join-Path $smoke 'video-after-recommendation-ui.txt') -Encoding UTF8
        if ($videoNames -notcontains $recommendedQuality) {
            throw "The Video tab did not display the applied Constant Quality value $recommendedQuality."
        }
        if (($videoNames -match '(?i)NVENC constant quality is not automatically lossless').Count -eq 0) {
            throw 'The native Video tab is missing the plain-language NVENC quality warning.'
        }

        Select-Tab -Root $root -Name 'Dimensions'
        $cropPicker = Find-Element `
            -Root $root `
            -ControlType ([System.Windows.Automation.ControlType]::ComboBox) `
            -Name 'Cropping'
        if ($null -eq $cropPicker) {
            throw 'The Dimensions black-bar/cropping control was not found.'
        }

        $desktop = [System.Windows.Automation.AutomationElement]::RootElement
        $cropPicker.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        Start-Sleep -Milliseconds 400
        $listCondition = New-PropertyCondition `
            -Property ([System.Windows.Automation.AutomationElement]::ControlTypeProperty) `
            -Value ([System.Windows.Automation.ControlType]::ListItem)
        $items = $desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listCondition)
        $expectedCropLabels = @(
            'Same as source (preserve original black bars)',
            'Safe auto-crop (least aggressive)',
            'Automatic crop (remove detected black bars)',
            'Custom crop')
        $actualCropLabels = @()
        foreach ($item in $items) {
            if ($expectedCropLabels -contains $item.Current.Name) {
                $actualCropLabels += $item.Current.Name
            }
        }
        $actualCropLabels | Set-Content (Join-Path $smoke 'crop-options-ui.txt') -Encoding UTF8
        if ($actualCropLabels.Count -ne 4) {
            throw "The Dimensions control did not expose all four plain-language black-bar choices. Found: $($actualCropLabels -join ' | ')"
        }
        if ($actualCropLabels[0] -ne $expectedCropLabels[0]) {
            throw "Same as source was not the first black-bar choice. Actual order: $($actualCropLabels -join ' | ')"
        }

        $sameAsSource = Find-Element `
            -Root $desktop `
            -ControlType ([System.Windows.Automation.ControlType]::ListItem) `
            -Name $expectedCropLabels[0]
        if ($null -eq $sameAsSource) {
            throw 'Same as source could not be selected from the black-bar control.'
        }
        $sameAsSource.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()

        foreach ($tabName in @('Upscale & Enhance', 'Verify', 'Live Engine', 'Summary')) {
            Select-Tab -Root $root -Name $tabName
            $process.Refresh()
            if ($process.HasExited) {
                throw "Transcencode crashed while opening '$tabName' with code $($process.ExitCode)."
            }
        }

        $scaleCondition = New-PropertyCondition `
            -Property ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) `
            -Value 'TranscencodeScalePicker'
        $scalePicker = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $scaleCondition)
        if ($null -eq $scalePicker) {
            throw 'The whole-interface size control was not found in the native window.'
        }

        $scalePicker.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        Start-Sleep -Milliseconds 300
        $scale125 = Find-Element `
            -Root $desktop `
            -ControlType ([System.Windows.Automation.ControlType]::ListItem) `
            -Name '125%'
        if ($null -eq $scale125) {
            throw 'The 125% interface-size option was not found.'
        }
        $scale125.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 750

        $scaleFile = Join-Path $appData 'Transcencode\interface-scale.txt'
        if (-not (Test-Path $scaleFile)) {
            throw 'The selected interface size was not persisted.'
        }
        $savedScale = (Get-Content $scaleFile -Raw).Trim()
        if ($savedScale -ne '1.25') {
            throw "Expected a saved interface factor of 1.25, found '$savedScale'."
        }

        $scalePicker.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        Start-Sleep -Milliseconds 300
        $scale100 = Find-Element `
            -Root $desktop `
            -ControlType ([System.Windows.Automation.ControlType]::ListItem) `
            -Name '100%'
        if ($null -eq $scale100) {
            throw 'The 100% interface-size reset option was not found.'
        }
        $scale100.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()

        Start-Sleep -Seconds 5
        $process.Refresh()
        if ($process.HasExited) {
            throw "Transcencode crashed after the complete GUI exercise with code $($process.ExitCode)."
        }
    }
    finally {
        if ($null -ne $process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
        $env:APPDATA = $oldAppData
        $env:LOCALAPPDATA = $oldLocalAppData
    }
}

$sourcePath = Write-RegressionSource
Test-CliPipeline -Source $sourcePath
Test-GuiPipeline -Source $sourcePath

@"
Transcencode Windows runtime smoke test passed.

Validated:
- Native WPF application launch and Transcencode window branding
- Original HandBrake tabs and five Transcencode tabs
- English and Spanish audio/subtitle visibility
- Same-as-source full-frame and black-bar preservation
- Automatic black-bar crop behavior
- Deep Analyze completion and recommendation application
- Recommendation propagation into the native Video tab
- Plain-language quality warning
- Plain-language black-bar choices with Same as source first
- 125% whole-interface scaling persistence and reset
- No process crash during the exercised workflow
"@ | Set-Content (Join-Path $smoke 'RUNTIME-SMOKE-PASSED.txt') -Encoding UTF8

Write-Host 'Transcencode Windows runtime smoke test passed.'
