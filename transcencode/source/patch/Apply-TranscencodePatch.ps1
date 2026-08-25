param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot,

    [Parameter(Mandatory = $true)]
    [string]$OverlayRoot
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Read-NormalizedText {
    param([string]$Path)
    return ([IO.File]::ReadAllText($Path) -replace "`r`n", "`n")
}

function Write-NormalizedText {
    param([string]$Path, [string]$Text)
    $windowsText = $Text -replace "(?<!`r)`n", "`r`n"
    [IO.File]::WriteAllText($Path, $windowsText, $utf8NoBom)
}

function Replace-Exact {
    param(
        [string]$Path,
        [string]$Old,
        [string]$New,
        [switch]$All
    )

    $text = Read-NormalizedText $Path
    $oldNormalized = $Old -replace "`r`n", "`n"
    $newNormalized = $New -replace "`r`n", "`n"

    if (-not $text.Contains($oldNormalized)) {
        throw "Patch anchor was not found in $Path.`n--- expected anchor ---`n$oldNormalized"
    }

    if ($All) {
        $text = $text.Replace($oldNormalized, $newNormalized)
    }
    else {
        $index = $text.IndexOf($oldNormalized, [StringComparison]::Ordinal)
        $text = $text.Substring(0, $index) + $newNormalized + $text.Substring($index + $oldNormalized.Length)
    }

    Write-NormalizedText $Path $text
}

if (-not (Test-Path -LiteralPath $SourceRoot -PathType Container)) {
    throw "HandBrake source root does not exist: $SourceRoot"
}
if (-not (Test-Path -LiteralPath $OverlayRoot -PathType Container)) {
    throw "Transcencode overlay does not exist: $OverlayRoot"
}

$wpfRoot = Join-Path $SourceRoot 'win\CS\HandBrakeWPF'
$mainView = Join-Path $wpfRoot 'Views\MainView.xaml'
$mainViewModel = Join-Path $wpfRoot 'ViewModels\MainViewModel.cs'
$resources = Join-Path $wpfRoot 'Properties\Resources.resx'
$project = Join-Path $wpfRoot 'HandBrakeWPF.csproj'

foreach ($required in @($mainView, $mainViewModel, $resources, $project)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Expected HandBrake 1.11.2 file is missing: $required"
    }
}

$projectText = Read-NormalizedText $project
$compatibleVersion = $projectText.Contains('<Version>1.11.0</Version>') -or $projectText.Contains('<Version>1.11.2</Version>')
if (-not $compatibleVersion) {
    throw 'This patch is pinned to the compatible HandBrake 1.11.x Windows WPF source tree.'
}

if ((Read-NormalizedText $mainView).Contains('transcencodeAnalyzeTab')) {
    Write-Host 'Transcencode native GUI patch is already applied.'
    exit 0
}

$backupRoot = Join-Path $SourceRoot ('.transcencode-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
foreach ($file in @($mainView, $mainViewModel, $resources, $project)) {
    $relative = $file.Substring($SourceRoot.Length).TrimStart('\', '/')
    $destination = Join-Path $backupRoot $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $file -Destination $destination -Force
}

# Copy new C# and XAML files into the real HandBrake WPF project.
Get-ChildItem -LiteralPath $OverlayRoot -Recurse -File | ForEach-Object {
    $relative = $_.FullName.Substring($OverlayRoot.Length).TrimStart('\', '/')
    $destination = Join-Path $SourceRoot $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
}

$tabAnchor = @'
                <TabItem Name="chaptersTab" Header="{x:Static Properties:Resources.MainView_ChaptersTab}">
                    <views:ChaptersView DataContext="{Binding ChaptersViewModel}"  />
                </TabItem>

                <!--<TabItem Name="metaTab" Header="{x:Static Properties:Resources.MainView_MetaDataTab}">
'@

$tabReplacement = @'
                <TabItem Name="chaptersTab" Header="{x:Static Properties:Resources.MainView_ChaptersTab}">
                    <views:ChaptersView DataContext="{Binding ChaptersViewModel}"  />
                </TabItem>

                <TabItem Name="transcencodeAnalyzeTab" Header="Analyze">
                    <views:TranscencodeAnalyzeView DataContext="{Binding TranscencodeAnalyzeViewModel}" />
                </TabItem>
                <TabItem Name="transcencodeSourceTracksTab" Header="Source Tracks">
                    <views:TranscencodeSourceTracksView DataContext="{Binding TranscencodeSourceTracksViewModel}" />
                </TabItem>
                <TabItem Name="transcencodeUpscaleTab" Header="Upscale &amp; Enhance">
                    <views:TranscencodeUpscaleView DataContext="{Binding TranscencodeUpscaleViewModel}" />
                </TabItem>
                <TabItem Name="transcencodeVerifyTab" Header="Verify">
                    <views:TranscencodeVerifyView DataContext="{Binding TranscencodeVerifyViewModel}" />
                </TabItem>
                <TabItem Name="transcencodeLiveEngineTab" Header="Live Engine">
                    <views:TranscencodeLiveEngineView DataContext="{Binding TranscencodeLiveEngineViewModel}" />
                </TabItem>

                <!--<TabItem Name="metaTab" Header="{x:Static Properties:Resources.MainView_MetaDataTab}">
'@
Replace-Exact -Path $mainView -Old $tabAnchor -New $tabReplacement

$constructorAnchor = @'
            IChaptersViewModel chaptersViewModel,
            IStaticPreviewViewModel staticPreviewViewModel,
            IQueueViewModel queueViewModel,
'@
$constructorReplacement = @'
            IChaptersViewModel chaptersViewModel,
            IStaticPreviewViewModel staticPreviewViewModel,
            ITranscencodeAnalyzeViewModel transcencodeAnalyzeViewModel,
            ITranscencodeSourceTracksViewModel transcencodeSourceTracksViewModel,
            ITranscencodeUpscaleViewModel transcencodeUpscaleViewModel,
            ITranscencodeVerifyViewModel transcencodeVerifyViewModel,
            ITranscencodeLiveEngineViewModel transcencodeLiveEngineViewModel,
            IQueueViewModel queueViewModel,
'@
Replace-Exact -Path $mainViewModel -Old $constructorAnchor -New $constructorReplacement

$assignmentAnchor = @'
            this.StaticPreviewViewModel = staticPreviewViewModel;

            // Setup Properties
'@
$assignmentReplacement = @'
            this.StaticPreviewViewModel = staticPreviewViewModel;
            this.TranscencodeAnalyzeViewModel = transcencodeAnalyzeViewModel;
            this.TranscencodeSourceTracksViewModel = transcencodeSourceTracksViewModel;
            this.TranscencodeUpscaleViewModel = transcencodeUpscaleViewModel;
            this.TranscencodeVerifyViewModel = transcencodeVerifyViewModel;
            this.TranscencodeLiveEngineViewModel = transcencodeLiveEngineViewModel;

            // Setup Properties
'@
Replace-Exact -Path $mainViewModel -Old $assignmentAnchor -New $assignmentReplacement

$propertyAnchor = @'
        public ISummaryViewModel SummaryViewModel { get; set; }

        public int SelectedTab { get; set; }
'@
$propertyReplacement = @'
        public ISummaryViewModel SummaryViewModel { get; set; }

        public ITranscencodeAnalyzeViewModel TranscencodeAnalyzeViewModel { get; set; }

        public ITranscencodeSourceTracksViewModel TranscencodeSourceTracksViewModel { get; set; }

        public ITranscencodeUpscaleViewModel TranscencodeUpscaleViewModel { get; set; }

        public ITranscencodeVerifyViewModel TranscencodeVerifyViewModel { get; set; }

        public ITranscencodeLiveEngineViewModel TranscencodeLiveEngineViewModel { get; set; }

        public int SelectedTab { get; set; }
'@
Replace-Exact -Path $mainViewModel -Old $propertyAnchor -New $propertyReplacement

$subscribeAnchor = @'
            this.SummaryViewModel.TabStatusChanged += this.TabStatusChanged;

            // Menu State
'@
$subscribeReplacement = @'
            this.SummaryViewModel.TabStatusChanged += this.TabStatusChanged;
            this.TranscencodeAnalyzeViewModel.TabStatusChanged += this.TabStatusChanged;
            this.TranscencodeSourceTracksViewModel.TabStatusChanged += this.TabStatusChanged;
            this.TranscencodeUpscaleViewModel.TabStatusChanged += this.TabStatusChanged;
            this.TranscencodeVerifyViewModel.TabStatusChanged += this.TabStatusChanged;
            this.TranscencodeLiveEngineViewModel.TabStatusChanged += this.TabStatusChanged;

            // Menu State
'@
Replace-Exact -Path $mainViewModel -Old $subscribeAnchor -New $subscribeReplacement

$unsubscribeAnchor = @'
            this.SummaryViewModel.TabStatusChanged -= this.TabStatusChanged;
        }
'@
$unsubscribeReplacement = @'
            this.SummaryViewModel.TabStatusChanged -= this.TabStatusChanged;
            this.TranscencodeAnalyzeViewModel.TabStatusChanged -= this.TabStatusChanged;
            this.TranscencodeSourceTracksViewModel.TabStatusChanged -= this.TabStatusChanged;
            this.TranscencodeUpscaleViewModel.TabStatusChanged -= this.TabStatusChanged;
            this.TranscencodeVerifyViewModel.TabStatusChanged -= this.TabStatusChanged;
            this.TranscencodeLiveEngineViewModel.TabStatusChanged -= this.TabStatusChanged;
        }
'@
Replace-Exact -Path $mainViewModel -Old $unsubscribeAnchor -New $unsubscribeReplacement

$queueEditAnchor = @'
                this.MetaDataViewModel.UpdateTask(this.CurrentTask);
              
                // Cleanup
'@
$queueEditReplacement = @'
                this.MetaDataViewModel.UpdateTask(this.CurrentTask);
                this.TranscencodeAnalyzeViewModel.UpdateTask(this.CurrentTask);
                this.TranscencodeSourceTracksViewModel.UpdateTask(this.CurrentTask);
                this.TranscencodeUpscaleViewModel.UpdateTask(this.CurrentTask);
                this.TranscencodeVerifyViewModel.UpdateTask(this.CurrentTask);
                this.TranscencodeLiveEngineViewModel.UpdateTask(this.CurrentTask);
              
                // Cleanup
'@
Replace-Exact -Path $mainViewModel -Old $queueEditAnchor -New $queueEditReplacement

$setupTabsAnchor = @'
                this.SummaryViewModel.SetSource(this.ScannedSource, this.SelectedTitle, this.selectedPreset, this.CurrentTask);
                this.isSettingPreset = false;
'@
$setupTabsReplacement = @'
                this.SummaryViewModel.SetSource(this.ScannedSource, this.SelectedTitle, this.selectedPreset, this.CurrentTask);
                this.TranscencodeAnalyzeViewModel.SetSource(this.ScannedSource, this.SelectedTitle, this.selectedPreset, this.CurrentTask);
                this.TranscencodeSourceTracksViewModel.SetSource(this.ScannedSource, this.SelectedTitle, this.selectedPreset, this.CurrentTask);
                this.TranscencodeUpscaleViewModel.SetSource(this.ScannedSource, this.SelectedTitle, this.selectedPreset, this.CurrentTask);
                this.TranscencodeVerifyViewModel.SetSource(this.ScannedSource, this.SelectedTitle, this.selectedPreset, this.CurrentTask);
                this.TranscencodeLiveEngineViewModel.SetSource(this.ScannedSource, this.SelectedTitle, this.selectedPreset, this.CurrentTask);
                this.isSettingPreset = false;
'@
Replace-Exact -Path $mainViewModel -Old $setupTabsAnchor -New $setupTabsReplacement

$presetAnchor = @'
                    this.MetaDataViewModel.SetPreset(this.selectedPreset, this.CurrentTask);
                    this.SummaryViewModel.UpdateDisplayedInfo();
'@
$presetReplacement = @'
                    this.MetaDataViewModel.SetPreset(this.selectedPreset, this.CurrentTask);
                    this.TranscencodeAnalyzeViewModel.SetPreset(this.selectedPreset, this.CurrentTask);
                    this.TranscencodeSourceTracksViewModel.SetPreset(this.selectedPreset, this.CurrentTask);
                    this.TranscencodeUpscaleViewModel.SetPreset(this.selectedPreset, this.CurrentTask);
                    this.TranscencodeVerifyViewModel.SetPreset(this.selectedPreset, this.CurrentTask);
                    this.TranscencodeLiveEngineViewModel.SetPreset(this.selectedPreset, this.CurrentTask);
                    this.SummaryViewModel.UpdateDisplayedInfo();
'@
Replace-Exact -Path $mainViewModel -Old $presetAnchor -New $presetReplacement

$startLine = '                this.QueueViewModel.StartQueue(); // Provides user checks.'
$startReplacement = @'
                this.QueueViewModel.StartQueue(); // Provides user checks.
                this.SwitchTab(11); // Transcencode Live Engine
'@
Replace-Exact -Path $mainViewModel -Old $startLine -New $startReplacement -All

# Brand the fork while preserving HandBrake's assembly name and core packaging assumptions.
[xml]$resourceXml = Get-Content -LiteralPath $resources -Raw
$titleNode = $resourceXml.root.data | Where-Object { $_.name -eq 'HandBrake_Title' } | Select-Object -First 1
if ($null -eq $titleNode) {
    throw 'Could not find HandBrake_Title in Resources.resx.'
}
$titleNode.value = 'Transcencode'
$resourceXml.Save($resources)

Replace-Exact -Path $project -Old '<PackageId>HandBrake</PackageId>' -New '<PackageId>Transcencode</PackageId>'
Replace-Exact -Path $project -Old '<Company>HandBrake Team</Company>' -New '<Company>Transcencode Project and HandBrake Team</Company>'
Replace-Exact -Path $project -Old '<Product>HandBrake</Product>' -New '<Product>Transcencode</Product>'
Replace-Exact -Path $project -Old '<Description>HandBrake is an open-source, GPL-licensed, multiplatform,video transcoder.</Description>' -New '<Description>Transcencode is a GPLv2 fork of the HandBrake Windows GUI with source-content analysis, track visibility, guided upscaling, output verification, and live engine status.</Description>'

# Do not let the fork silently replace itself with an official HandBrake update.
Replace-Exact -Path $mainViewModel -Old '            this.updateService.PerformStartupUpdateCheck(this.HandleUpdateCheckResults);' -New '            // Transcencode fork: official HandBrake startup update checks are disabled.'

Write-Host 'Transcencode native GUI patch applied successfully to the compatible HandBrake 1.11.x WPF source tree.'
Write-Host "Original modified files were backed up under: $backupRoot"
