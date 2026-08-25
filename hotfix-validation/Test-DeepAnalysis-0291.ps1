#requires -version 5.1
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Test-DeepAnalysisSummaryInterpolation {
    param(
        [Parameter(Mandatory = $true)][string]$EncoderId,
        [Parameter(Mandatory = $true)][double]$RecommendedQuality
    )

    $rows = @([pscustomobject]@{ Time = '00:00:10' })
    $averageBrightness = 42.5
    $averageDark = 55.0
    $CurrentQuality = 18.0
    $measuredDescription = 'One test encode was measured.'
    $hardestTimes = ($rows | ForEach-Object { $_.Time }) -join ', '
    $summary = "Deep Analyze examined $($rows.Count) distributed preview pictures. Average brightness was $([Math]::Round($averageBrightness,1))/255 and average dark-pixel load was $([Math]::Round($averageDark,1))%. $measuredDescription Hardest sampled times: $hardestTimes. Recommended starting quality for ${EncoderId}: CQ/RF $RecommendedQuality (current value $([Math]::Round($CurrentQuality,1)))."
    return $summary
}

$source = Get-Content -LiteralPath $MyInvocation.MyCommand.Path -Raw
$tokens = $null
$parseErrors = $null
[void][System.Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) {
    $parseErrors | Format-List * | Out-String | Write-Error
    throw "PowerShell parser reported $($parseErrors.Count) error(s)."
}

$result = Test-DeepAnalysisSummaryInterpolation -EncoderId 'nvenc_h265_10bit' -RecommendedQuality 17
$expected = 'Recommended starting quality for nvenc_h265_10bit: CQ/RF 17'
if ($result.IndexOf($expected, [StringComparison]::Ordinal) -lt 0) {
    throw "The corrected interpolation did not produce the expected text. Result: $result"
}

$invalidNeedle = '$EncoderId' + ': CQ/RF'
if ($source.IndexOf($invalidNeedle, [StringComparison]::Ordinal) -ge 0) {
    throw 'The invalid unbraced PowerShell interpolation form returned.'
}

Write-Host 'TRANSCENCODE_0291_POWERSHELL51_INTERPOLATION_TEST_PASSED'
Write-Host $result
