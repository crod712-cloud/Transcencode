#requires -version 5.1
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:Queue = @()
$AnalysisOutputFolderBox = [pscustomobject]@{ Text = '' }
$OutputBox = [pscustomobject]@{ Text = '' }
$InputBox = [pscustomobject]@{ Text = '' }
$ContainerBox = [pscustomobject]@{ SelectedItem = 'MKV' }

function Get-PostAnalysisOutputPath {
    param([switch]$CreateFolder,[switch]$EnsureUnique)
    $folder = $AnalysisOutputFolderBox.Text.Trim()
    $folder = [IO.Path]::GetFullPath($folder)
    if ($CreateFolder -and -not (Test-Path -LiteralPath $folder)) {
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
    }
    $extension = if ($ContainerBox.SelectedItem -eq 'MP4') { '.mp4' } else { '.mkv' }
    $baseName = [IO.Path]::GetFileNameWithoutExtension($OutputBox.Text.Trim())
    $candidate = Join-Path $folder ($baseName + $extension)
    if (-not $EnsureUnique) { return $candidate }
    $usedPaths = @($script:Queue | ForEach-Object { [string]$_.Output })
    if (-not (Test-Path -LiteralPath $candidate) -and $usedPaths -notcontains $candidate) { return $candidate }
    for ($index = 1; $index -le 9999; $index++) {
        $numbered = Join-Path $folder ('{0} ({1}){2}' -f $baseName,$index,$extension)
        if (-not (Test-Path -LiteralPath $numbered) -and $usedPaths -notcontains $numbered) { return $numbered }
    }
    throw 'No unique path.'
}

$root = Join-Path $env:TEMP ('Transcencode-031-debug-' + [guid]::NewGuid().ToString('N'))
$sourceDirectory = Join-Path $root 'source'
$destinationDirectory = Join-Path $root 'finished encodes'
New-Item -ItemType Directory -Path $sourceDirectory -Force | Out-Null
try {
    $OutputBox.Text = Join-Path $sourceDirectory 'Tremors (1990).transcencode.mkv'
    $AnalysisOutputFolderBox.Text = $destinationDirectory
    $actual = Get-PostAnalysisOutputPath -CreateFolder -EnsureUnique
    $expected = Join-Path $destinationDirectory 'Tremors (1990).transcencode.mkv'
    Write-Host ('TEMP={0}' -f $env:TEMP)
    Write-Host ('OutputBox={0}' -f $OutputBox.Text)
    Write-Host ('BaseName={0}' -f [IO.Path]::GetFileNameWithoutExtension($OutputBox.Text.Trim()))
    Write-Host ('Destination={0}' -f $destinationDirectory)
    Write-Host ('Expected={0}' -f $expected)
    Write-Host ('Actual={0}' -f ($actual -join ' | '))
    Write-Host ('ActualType={0}' -f $actual.GetType().FullName)
    Write-Host ('Equal={0}' -f ($actual -eq $expected))
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
