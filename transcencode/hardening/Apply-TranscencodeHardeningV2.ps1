[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot,

    [Parameter(Mandatory = $true)]
    [string]$OverlayRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Replace-ExactlyOnce {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Old,
        [Parameter(Mandatory = $true)][string]$New,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $first = $Text.IndexOf($Old, [System.StringComparison]::Ordinal)
    if ($first -lt 0) {
        throw "Patch anchor was not found: $Description"
    }

    $second = $Text.IndexOf($Old, $first + $Old.Length, [System.StringComparison]::Ordinal)
    if ($second -ge 0) {
        throw "Patch anchor was not unique: $Description"
    }

    return $Text.Substring(0, $first) + $New + $Text.Substring($first + $Old.Length)
}

function Write-Utf8Bom {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Text
    )

    [System.IO.File]::WriteAllText($Path, $Text, [System.Text.UTF8Encoding]::new($true))
}

# Apply the first hardening layer before the incremental V2 corrections.
& (Join-Path $PSScriptRoot 'Apply-TranscencodeHardening.ps1') `
    -SourceRoot $SourceRoot `
    -OverlayRoot $OverlayRoot

$source = (Resolve-Path $SourceRoot).Path

# Same as source must be the first, clearest black-bar choice.
$pictureViewModelPath = Join-Path $source 'win/CS/HandBrakeWPF/ViewModels/PictureSettingsViewModel.cs'
if (-not (Test-Path $pictureViewModelPath)) {
    throw 'PictureSettingsViewModel.cs was not found.'
}

$text = [System.IO.File]::ReadAllText($pictureViewModelPath)
$old = 'public BindingList<CropMode> CropModes { get; } = new BindingList<CropMode> { CropMode.Automatic, CropMode.Loose, CropMode.None, CropMode.Custom };'
$new = 'public BindingList<CropMode> CropModes { get; } = new BindingList<CropMode> { CropMode.None, CropMode.Loose, CropMode.Automatic, CropMode.Custom };'
$text = Replace-ExactlyOnce -Text $text -Old $old -New $new -Description 'PictureSettingsViewModel CropModes order'
Write-Utf8Bom -Path $pictureViewModelPath -Text $text

# Applying an Analyze recommendation must update HandBrake's real Video view-model,
# not merely mutate the shared task and hope every quality binding notices.
$analyzeViewModelPath = Join-Path $source 'win/CS/HandBrakeWPF/ViewModels/TranscencodeAnalyzeViewModel.cs'
if (-not (Test-Path $analyzeViewModelPath)) {
    throw 'TranscencodeAnalyzeViewModel.cs was not found.'
}
$analyzeCode = [System.IO.File]::ReadAllText($analyzeViewModelPath)
$analyzeCode = Replace-ExactlyOnce `
    -Text $analyzeCode `
    -Old '            this.videoViewModel.RefreshTask();' `
    -New '            this.videoViewModel.UpdateTask(this.task);' `
    -Description 'Analyze recommendation native Video refresh'
Write-Utf8Bom -Path $analyzeViewModelPath -Text $analyzeCode

# Transcencode must never read or overwrite the user's official HandBrake settings,
# presets, queue recovery state, or logs. Give the fork its own application-data root.
$directoryUtilitiesPath = Join-Path $source 'win/CS/HandBrake.App.Core/Utilities/DirectoryUtilities.cs'
if (-not (Test-Path $directoryUtilitiesPath)) {
    throw 'DirectoryUtilities.cs was not found.'
}
$directoryCode = [System.IO.File]::ReadAllText($directoryUtilitiesPath)
$directoryCode = Replace-ExactlyOnce `
    -Text $directoryCode `
    -Old '                return Path.Combine(GetStorageDirectory(), "HandBrake", "Nightly");' `
    -New '                return Path.Combine(GetStorageDirectory(), "Transcencode", "Nightly");' `
    -Description 'nightly application-data directory'
$directoryCode = Replace-ExactlyOnce `
    -Text $directoryCode `
    -Old '                return Path.Combine(GetStorageDirectory(), "HandBrake");' `
    -New '                return Path.Combine(GetStorageDirectory(), "Transcencode");' `
    -Description 'release application-data directory'
$directoryCode = Replace-ExactlyOnce `
    -Text $directoryCode `
    -Old '            return Path.Combine(GetStorageDirectory(), "HandBrake", "logs");' `
    -New '            return Path.Combine(GetStorageDirectory(), "Transcencode", "logs");' `
    -Description 'application log directory'
Write-Utf8Bom -Path $directoryUtilitiesPath -Text $directoryCode

# Use a fork-specific worker port so an installed/running HandBrake cannot collide with Transcencode.
$userSettingsPath = Join-Path $source 'win/CS/HandBrakeWPF/Services/UserSettingService.cs'
if (-not (Test-Path $userSettingsPath)) {
    throw 'UserSettingService.cs was not found.'
}
$userSettingsCode = [System.IO.File]::ReadAllText($userSettingsPath)
$userSettingsCode = Replace-ExactlyOnce `
    -Text $userSettingsCode `
    -Old '            defaults.Add(UserSettingConstants.ProcessIsolationPort, 8037);' `
    -New '            defaults.Add(UserSettingConstants.ProcessIsolationPort, 8047);' `
    -Description 'fork-specific process isolation port'
Write-Utf8Bom -Path $userSettingsPath -Text $userSettingsCode

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

# Patch the real application startup path. The prior candidate only exercised an in-process
# test path and could still open HandBrake's blank themed error dialog during a normal launch.
$appPath = Join-Path $source 'win/CS/HandBrakeWPF/App.xaml.cs'
if (-not (Test-Path $appPath)) {
    throw 'App.xaml.cs was not found.'
}
$appCode = [System.IO.File]::ReadAllText($appPath)
$appCode = Replace-ExactlyOnce `
    -Text $appCode `
    -Old @'
    using HandBrakeWPF.Services.Interfaces;
    using HandBrakeWPF.Startup;
'@ `
    -New @'
    using HandBrakeWPF.Services.Interfaces;
    using HandBrakeWPF.Services.Transcencode;
    using HandBrakeWPF.Startup;
'@ `
    -Description 'App startup diagnostics namespace'

$appCode = Replace-ExactlyOnce `
    -Text $appCode `
    -Old @'
        private void ShowError(object exception)
        {
            try
'@ `
    -New @'
        private void ShowError(object exception)
        {
            string diagnosticPath = TranscencodeCrashReporter.RecordHandledError("App.ShowError", exception);
            string exceptionText = exception?.ToString() ?? "No exception details were supplied.";

            try
'@ `
    -Description 'App.ShowError diagnostic capture'

$appCode = Replace-ExactlyOnce `
    -Text $appCode `
    -Old '                        windowManager.ShowDialog<ErrorView>(errorView);' `
    -New @'
                        string visibleMessage = string.IsNullOrWhiteSpace(errorView.ErrorMessage)
                            ? "Transcencode could not start."
                            : errorView.ErrorMessage;
                        string visibleSolution = string.IsNullOrWhiteSpace(errorView.Solution)
                            ? "The startup failure was written to a local diagnostic log."
                            : errorView.Solution;
                        string visibleDetails = string.IsNullOrWhiteSpace(errorView.Details)
                            ? exceptionText
                            : errorView.Details;

                        if (!string.IsNullOrWhiteSpace(diagnosticPath))
                        {
                            visibleDetails += Environment.NewLine + Environment.NewLine + "Diagnostic log: " + diagnosticPath;
                        }

                        if (visibleDetails.Length > 12000)
                        {
                            visibleDetails = visibleDetails.Substring(0, 12000) + Environment.NewLine + "[Details truncated in this dialog; see the diagnostic log.]";
                        }

                        MessageBox.Show(
                            visibleMessage + Environment.NewLine + Environment.NewLine +
                            visibleSolution + Environment.NewLine + Environment.NewLine +
                            visibleDetails,
                            "Transcencode startup error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
'@ `
    -Description 'reliable visible startup error dialog'

# Add a hidden in-process self-test entry point. This executes against the actual App,
# ShellView, MainViewModel, source scan, native Transcencode view-models, and WPF tree.
$appAnchor = @'
            // If we have a file dropped on the icon, try scanning it.
            string[] args = e.Args;
            if (args.Any() && (File.Exists(args[0]) || Directory.Exists(args[0])))
            {
                IMainViewModel mvm = IoCHelper.Get<IMainViewModel>();
                mvm.StartScan(new List<string> { args[0] }, 0);
            }
'@
$appReplacement = @'
            // If we have a file dropped on the icon, try scanning it.
            string[] args = e.Args;
            if (args.Any() && (File.Exists(args[0]) || Directory.Exists(args[0])))
            {
                IMainViewModel mvm = IoCHelper.Get<IMainViewModel>();
                mvm.StartScan(new List<string> { args[0] }, 0);
            }

            const string selfTestPrefix = "--transcencode-self-test-report=";
            string selfTestArgument = e.Args.FirstOrDefault(
                item => item.StartsWith(selfTestPrefix, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(selfTestArgument))
            {
                string selfTestReportPath = selfTestArgument
                    .Substring(selfTestPrefix.Length)
                    .Trim()
                    .Trim('"');
                TranscencodeSelfTestRunner.Schedule(selfTestReportPath);
            }
'@
$appCode = Replace-ExactlyOnce -Text $appCode -Old $appAnchor -New $appReplacement -Description 'App self-test entry point'
Write-Utf8Bom -Path $appPath -Text $appCode

Write-Host 'Transcencode hardening V2 applied: isolated settings/logs, unique worker port, visible startup diagnostics, Same as source ordering, native Video refresh, scaling guards, and self-test support.'
