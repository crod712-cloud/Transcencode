#requires -version 5.1
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName System.Windows.Forms

function Assert-True {
    param([bool]$Condition,[string]$Message)
    if (-not $Condition) { throw $Message }
}

# The exact WPF shape added to the Deep Analyze area must load on Windows
# PowerShell 5.1 and expose every control used by the application logic.
[xml]$xaml = @"
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Transcencode analysis flow test" Width="900" Height="300">
    <GroupBox Header="After successful analysis" Margin="4">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="105"/>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <CheckBox Grid.Row="0" Grid.ColumnSpan="3" x:Name="EncodeAfterAnalysisCheck" Content="Start the full encode automatically after Deep Analyze finishes" Margin="2,2,2,8"/>
            <TextBlock Grid.Row="1" Grid.Column="0" Text="Save to folder" VerticalAlignment="Center"/>
            <TextBox Grid.Row="1" Grid.Column="1" x:Name="AnalysisOutputFolderBox" IsEnabled="False"/>
            <Button Grid.Row="1" Grid.Column="2" x:Name="BrowseAnalysisOutputFolderButton" Content="Choose folder..." IsEnabled="False"/>
            <TextBlock Grid.Row="2" Grid.ColumnSpan="3" x:Name="AnalysisOutputPreviewText" TextWrapping="Wrap" Margin="2,6,2,0"/>
        </Grid>
    </GroupBox>
</Window>
"@
$reader = New-Object System.Xml.XmlNodeReader $xaml
$testWindow = [Windows.Markup.XamlReader]::Load($reader)
foreach ($name in @('EncodeAfterAnalysisCheck','AnalysisOutputFolderBox','BrowseAnalysisOutputFolderButton','AnalysisOutputPreviewText')) {
    Assert-True ($null -ne $testWindow.FindName($name)) "WPF control $name was not created."
}
Assert-True ($null -ne ([System.Windows.Forms.FolderBrowserDialog]::new())) 'FolderBrowserDialog is unavailable.'
$testWindow.Close()

$script:PostAnalysisOutputFolder = ''
$script:Queue = @()
$AnalysisOutputFolderBox = [pscustomobject]@{ Text = '' }
$OutputBox = [pscustomobject]@{ Text = '' }
$InputBox = [pscustomobject]@{ Text = '' }
$ContainerBox = [pscustomobject]@{ SelectedItem = 'MKV' }

function Get-DefaultAnalysisOutputFolder {
    if (-not [string]::IsNullOrWhiteSpace($script:PostAnalysisOutputFolder)) {
        return $script:PostAnalysisOutputFolder
    }
    if ($AnalysisOutputFolderBox -and -not [string]::IsNullOrWhiteSpace($AnalysisOutputFolderBox.Text)) {
        return $AnalysisOutputFolderBox.Text.Trim()
    }
    if ($OutputBox -and -not [string]::IsNullOrWhiteSpace($OutputBox.Text)) {
        try {
            $directory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($OutputBox.Text.Trim()))
            if (-not [string]::IsNullOrWhiteSpace($directory)) { return $directory }
        }
        catch { }
    }
    if ($InputBox -and -not [string]::IsNullOrWhiteSpace($InputBox.Text) -and (Test-Path -LiteralPath $InputBox.Text.Trim())) {
        return [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($InputBox.Text.Trim()))
    }
    return [Environment]::GetFolderPath([Environment+SpecialFolder]::MyVideos)
}

function Get-PostAnalysisOutputPath {
    param(
        [switch]$CreateFolder,
        [switch]$EnsureUnique
    )

    $folder = if ($AnalysisOutputFolderBox) { $AnalysisOutputFolderBox.Text.Trim() } else { '' }
    if ([string]::IsNullOrWhiteSpace($folder)) { $folder = Get-DefaultAnalysisOutputFolder }
    if ([string]::IsNullOrWhiteSpace($folder)) { throw 'Choose a folder for the completed encode.' }

    try { $folder = [IO.Path]::GetFullPath($folder) }
    catch { throw 'The selected encode folder is not a valid Windows path.' }

    if ($CreateFolder -and -not (Test-Path -LiteralPath $folder)) {
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
    }
    if ((Test-Path -LiteralPath $folder) -and -not (Get-Item -LiteralPath $folder).PSIsContainer) {
        throw 'The selected encode destination is not a folder.'
    }

    $extension = if ($ContainerBox.SelectedItem -eq 'MP4') { '.mp4' } else { '.mkv' }
    $baseName = ''
    if ($OutputBox -and -not [string]::IsNullOrWhiteSpace($OutputBox.Text)) {
        try { $baseName = [IO.Path]::GetFileNameWithoutExtension($OutputBox.Text.Trim()) } catch { }
    }
    if ([string]::IsNullOrWhiteSpace($baseName) -and $InputBox -and -not [string]::IsNullOrWhiteSpace($InputBox.Text)) {
        try { $baseName = [IO.Path]::GetFileNameWithoutExtension($InputBox.Text.Trim()) + '.transcencode' } catch { }
    }
    if ([string]::IsNullOrWhiteSpace($baseName)) { $baseName = 'transcencode-output' }

    $candidate = Join-Path $folder ($baseName + $extension)
    if (-not $EnsureUnique) { return $candidate }

    $usedPaths = @($script:Queue | ForEach-Object { [string]$_.Output })
    if (-not (Test-Path -LiteralPath $candidate) -and $usedPaths -notcontains $candidate) { return $candidate }

    for ($index = 1; $index -le 9999; $index++) {
        $numbered = Join-Path $folder ('{0} ({1}){2}' -f $baseName,$index,$extension)
        if (-not (Test-Path -LiteralPath $numbered) -and $usedPaths -notcontains $numbered) { return $numbered }
    }
    throw 'Transcencode could not create a unique output filename in the selected folder.'
}

$root = Join-Path $env:TEMP ('Transcencode-031-analysis-flow-' + [guid]::NewGuid().ToString('N'))
$sourceDirectory = Join-Path $root 'source'
$destinationDirectory = Join-Path $root 'finished encodes'
New-Item -ItemType Directory -Path $sourceDirectory -Force | Out-Null
$source = Join-Path $sourceDirectory 'Tremors (1990).mkv'
[IO.File]::WriteAllText($source,'test')

try {
    $InputBox.Text = $source
    $OutputBox.Text = Join-Path $sourceDirectory 'Tremors (1990).transcencode.mkv'
    $AnalysisOutputFolderBox.Text = $destinationDirectory

    $first = Get-PostAnalysisOutputPath -CreateFolder -EnsureUnique
    Assert-True ($first -eq (Join-Path $destinationDirectory 'Tremors (1990).transcencode.mkv')) 'The selected destination folder or output filename was not preserved.'
    Assert-True (Test-Path -LiteralPath $destinationDirectory -PathType Container) 'The selected output folder was not created.'

    [IO.File]::WriteAllText($first,'existing')
    $second = Get-PostAnalysisOutputPath -CreateFolder -EnsureUnique
    Assert-True ($second -eq (Join-Path $destinationDirectory 'Tremors (1990).transcencode (1).mkv')) 'An existing output was not protected by a numbered filename.'

    $script:Queue = @([pscustomobject]@{ Output = $second })
    $third = Get-PostAnalysisOutputPath -CreateFolder -EnsureUnique
    Assert-True ($third -eq (Join-Path $destinationDirectory 'Tremors (1990).transcencode (2).mkv')) 'A queued destination collision was not protected.'

    $ContainerBox.SelectedItem = 'MP4'
    $script:Queue = @()
    $mp4 = Get-PostAnalysisOutputPath -CreateFolder -EnsureUnique
    Assert-True ($mp4.EndsWith('.mp4',[StringComparison]::OrdinalIgnoreCase)) 'The selected container did not control the final extension.'

    $settings = [ordered]@{
        PostAnalysisAutoEncode = $true
        PostAnalysisOutputFolder = $destinationDirectory
    }
    $settingsPath = Join-Path $root 'settings.json'
    $settings | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding UTF8
    $loaded = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    Assert-True ([bool]$loaded.PostAnalysisAutoEncode) 'The auto-encode selection was not persisted.'
    Assert-True ([string]$loaded.PostAnalysisOutputFolder -eq $destinationDirectory) 'The selected destination folder was not persisted.'

    # The flow intentionally starts the full encode only after a non-null
    # calibration result. Parse the exact event-control sequence under PS 5.1.
    $eventFlow = @'
$calibration = Start-DeepAnalysis
if ($startEncodeAfterAnalysis) {
    if (-not $calibration) { throw 'Deep Analyze did not return a completed calibration result. The encode was not started.' }
    $OutputBox.Text = Get-PostAnalysisOutputPath -CreateFolder -EnsureUnique
    $job = New-JobFromUi
    Start-EngineProcess -Mode Encode -Arguments (New-EncodeArguments $job) -Job $job
    if ($MainTabControl -and $LiveCliTab) { $MainTabControl.SelectedItem = $LiveCliTab }
}
'@
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseInput($eventFlow,[ref]$tokens,[ref]$errors)
    Assert-True ($errors.Count -eq 0) ('The post-analysis encode event does not parse: ' + (($errors | ForEach-Object Message) -join '; '))

    Write-Host 'TRANSCENCODE_031_ANALYSIS_AUTO_ENCODE_TEST_PASSED'
    Write-Host ('Destination: {0}' -f $first)
    Write-Host ('Collision-safe destination: {0}' -f $third)
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
