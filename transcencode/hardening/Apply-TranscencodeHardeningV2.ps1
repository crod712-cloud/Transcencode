[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot,

    [Parameter(Mandatory = $true)]
    [string]$OverlayRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Apply the first hardening layer before the incremental V2 corrections.
& (Join-Path $PSScriptRoot 'Apply-TranscencodeHardening.ps1') `
    -SourceRoot $SourceRoot `
    -OverlayRoot $OverlayRoot

$source = (Resolve-Path $SourceRoot).Path
$pictureViewModelPath = Join-Path $source 'win/CS/HandBrakeWPF/ViewModels/PictureSettingsViewModel.cs'
if (-not (Test-Path $pictureViewModelPath)) {
    throw 'PictureSettingsViewModel.cs was not found.'
}

$text = [System.IO.File]::ReadAllText($pictureViewModelPath)
$old = 'public BindingList<CropMode> CropModes { get; } = new BindingList<CropMode> { CropMode.Automatic, CropMode.Loose, CropMode.None, CropMode.Custom };'
$new = 'public BindingList<CropMode> CropModes { get; } = new BindingList<CropMode> { CropMode.None, CropMode.Loose, CropMode.Automatic, CropMode.Custom };'
$count = ([System.Text.RegularExpressions.Regex]::Matches($text, [System.Text.RegularExpressions.Regex]::Escape($old))).Count
if ($count -ne 1) {
    throw "Expected exactly one CropModes ordering anchor, found $count."
}
$text = $text.Replace($old, $new)
[System.IO.File]::WriteAllText($pictureViewModelPath, $text, [System.Text.UTF8Encoding]::new($true))

# The native window must remain usable when a saved scale file is malformed or read-only.
$shellCodePath = Join-Path $source 'win/CS/HandBrakeWPF/Views/ShellView.xaml.cs'
$shellCode = [System.IO.File]::ReadAllText($shellCodePath)
foreach ($required in @(
    'savedScale < 1.0 || savedScale > 2.0',
    'System.Math.Max(1.0, System.Math.Min(2.0, factor))',
    'Scaling remains active for this session even if the preference cannot be persisted.'
)) {
    if (-not $shellCode.Contains($required, [System.StringComparison]::Ordinal)) {
        throw "Interface scaling safety check is missing: $required"
    }
}

Write-Host 'Transcencode hardening V2 applied: Same as source is first and scaling guards are verified.'
