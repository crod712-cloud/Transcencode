#requires -version 5.1
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$Silent = $true
$script:Log = New-Object System.Collections.Generic.List[string]

function Write-InstallLog {
    param([Parameter(Mandatory = $true)][string]$Text)
    $script:Log.Add($Text)
}

function Set-InstallStatus {
    param(
        [Parameter(Mandatory = $true)][string]$Status,
        [Parameter(Mandatory = $true)][int]$Percent,
        [string]$Detail = ''
    )
    if ($Detail) { Write-InstallLog $Detail }
}

function Get-TranscencodeProcesses {
    param([Parameter(Mandatory = $true)][string[]]$RootPaths)

    $normalizedRoots = @()
    foreach ($rootPath in $RootPaths) {
        if ([string]::IsNullOrWhiteSpace($rootPath)) { continue }
        try {
            $fullRoot = [IO.Path]::GetFullPath($rootPath)
            $fullRoot = $fullRoot.TrimEnd([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
            if ($normalizedRoots -notcontains $fullRoot) { $normalizedRoots += $fullRoot }
        }
        catch { }
    }

    if ($normalizedRoots.Count -eq 0) { return @() }

    try {
        $rows = @(Get-CimInstance -ClassName Win32_Process -ErrorAction Stop)
    }
    catch {
        try { $rows = @(Get-WmiObject -Class Win32_Process -ErrorAction Stop) }
        catch {
            Write-InstallLog ('Process detection was unavailable: {0}' -f $_.Exception.Message)
            return @()
        }
    }

    $matches = @()
    foreach ($row in $rows) {
        $processId = [int]$row.ProcessId
        if ($processId -eq $PID) { continue }

        $executablePath = [string]$row.ExecutablePath
        $commandLine = [string]$row.CommandLine
        $belongsToTranscencode = $false
        foreach ($rootPath in $normalizedRoots) {
            $prefix = $rootPath + [IO.Path]::DirectorySeparatorChar
            $pathMatch = -not [string]::IsNullOrWhiteSpace($executablePath) -and
                ($executablePath.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase) -or
                 $executablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))
            $commandRootMatch = -not [string]::IsNullOrWhiteSpace($commandLine) -and
                $commandLine.IndexOf($rootPath, [StringComparison]::OrdinalIgnoreCase) -ge 0
            $commandAppMatch = $commandRootMatch -and (
                $commandLine.IndexOf('Launch-Transcencode.ps1', [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $commandLine.IndexOf('Transcencode.ps1', [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $commandLine.IndexOf('HandBrakeCLI.exe', [StringComparison]::OrdinalIgnoreCase) -ge 0)
            if ($pathMatch -or $commandAppMatch) {
                $belongsToTranscencode = $true
                break
            }
        }

        if ($belongsToTranscencode) {
            $matches += [pscustomobject]@{
                ProcessId = $processId
                Name = [string]$row.Name
                ExecutablePath = $executablePath
                CommandLine = $commandLine
            }
        }
    }
    return @($matches)
}

function Stop-TranscencodeProcesses {
    param([Parameter(Mandatory = $true)][string[]]$RootPaths)

    $processes = @(Get-TranscencodeProcesses -RootPaths $RootPaths)
    if ($processes.Count -eq 0) { return }

    $processSummary = ($processes | ForEach-Object { '{0} (PID {1})' -f $_.Name, $_.ProcessId }) -join ', '
    Write-InstallLog ('Running Transcencode processes detected: {0}' -f $processSummary)
    Set-InstallStatus 'Closing running Transcencode processes' 72 ('Setup found: {0}' -f $processSummary)

    if (-not $Silent) {
        $answer = [System.Windows.MessageBox]::Show(
            "Transcencode or its encoding engine is still running.`n`nSetup must close it before updating the application files. Any active encode, scan, or Deep Analyze run will stop.`n`nClose it now and continue?",
            'Transcencode Setup',
            [System.Windows.MessageBoxButton]::YesNo,
            [System.Windows.MessageBoxImage]::Warning
        )
        if ($answer -ne [System.Windows.MessageBoxResult]::Yes) {
            throw 'Installation was canceled because Transcencode is still running.'
        }
    }

    foreach ($item in $processes) {
        try {
            $process = [System.Diagnostics.Process]::GetProcessById([int]$item.ProcessId)
            try { $process.Refresh() } catch { }
            if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
                [void]$process.CloseMainWindow()
            }
            $process.Dispose()
        }
        catch { }
    }

    Start-Sleep -Milliseconds 1500
    $remaining = @(Get-TranscencodeProcesses -RootPaths $RootPaths)
    foreach ($item in $remaining) {
        try {
            Write-InstallLog ('Force-stopping {0} (PID {1}).' -f $item.Name, $item.ProcessId)
            Stop-Process -Id ([int]$item.ProcessId) -Force -ErrorAction Stop
        }
        catch {
            Write-InstallLog ('Could not stop PID {0}: {1}' -f $item.ProcessId, $_.Exception.Message)
        }
    }

    for ($attempt = 1; $attempt -le 40; $attempt++) {
        $remaining = @(Get-TranscencodeProcesses -RootPaths $RootPaths)
        if ($remaining.Count -eq 0) {
            Write-InstallLog 'All matching Transcencode processes exited.'
            Start-Sleep -Milliseconds 350
            return
        }
        Start-Sleep -Milliseconds 250
    }

    $remaining = @(Get-TranscencodeProcesses -RootPaths $RootPaths)
    $remainingText = ($remaining | ForEach-Object { '{0} (PID {1})' -f $_.Name, $_.ProcessId }) -join ', '
    throw ('Setup could not close all running Transcencode processes. Remaining: {0}' -f $remainingText)
}

function Invoke-InstallFileOperation {
    param(
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][scriptblock]$Operation,
        [int]$MaximumAttempts = 25,
        [int]$DelayMilliseconds = 400
    )

    $lastError = $null
    for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
        try {
            & $Operation
            return
        }
        catch {
            $lastError = $_
            Write-InstallLog ('{0} failed on attempt {1} of {2}: {3}' -f $Description, $attempt, $MaximumAttempts, $_.Exception.Message)
            if ($attempt -ge $MaximumAttempts) { break }
            [GC]::Collect()
            [GC]::WaitForPendingFinalizers()
            Start-Sleep -Milliseconds $DelayMilliseconds
        }
    }

    $message = if ($lastError) { $lastError.Exception.Message } else { 'Unknown file-system error.' }
    throw ('{0} could not complete after {1} attempts because Windows still reported a file or folder in use. Last error: {2}' -f $Description, $MaximumAttempts, $message)
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$testRoot = Join-Path $env:TEMP ('Transcencode-0292-lock-test-' + [guid]::NewGuid().ToString('N'))
$installDirectory = Join-Path $testRoot 'Programs\Transcencode'
$backupDirectory = $installDirectory + '.previous'
$outsideDirectory = Join-Path $testRoot 'outside'
New-Item -ItemType Directory -Path $installDirectory, $outsideDirectory -Force | Out-Null
$launchScript = Join-Path $installDirectory 'Launch-Transcencode.ps1'
@'
Set-Location -LiteralPath (Split-Path -Parent $MyInvocation.MyCommand.Path)
Start-Sleep -Seconds 300
'@ | Set-Content -LiteralPath $launchScript -Encoding ASCII

$unrelatedScript = Join-Path $outsideDirectory 'Unrelated.ps1'
'Start-Sleep -Seconds 300' | Set-Content -LiteralPath $unrelatedScript -Encoding ASCII
$powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'

$appStart = New-Object System.Diagnostics.ProcessStartInfo
$appStart.FileName = $powershell
$appStart.Arguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -File "{0}"' -f $launchScript
$appStart.WorkingDirectory = $installDirectory
$appStart.UseShellExecute = $false
$appStart.CreateNoWindow = $true
$appProcess = [System.Diagnostics.Process]::Start($appStart)

$unrelatedStart = New-Object System.Diagnostics.ProcessStartInfo
$unrelatedStart.FileName = $powershell
$unrelatedStart.Arguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -File "{0}"' -f $unrelatedScript
$unrelatedStart.WorkingDirectory = $outsideDirectory
$unrelatedStart.UseShellExecute = $false
$unrelatedStart.CreateNoWindow = $true
$unrelatedProcess = [System.Diagnostics.Process]::Start($unrelatedStart)

try {
    Start-Sleep -Milliseconds 1200
    $detected = @(Get-TranscencodeProcesses -RootPaths @($installDirectory, $backupDirectory))
    Assert-True ($detected.ProcessId -contains $appProcess.Id) 'The running Transcencode PowerShell process was not detected.'
    Assert-True ($detected.ProcessId -notcontains $unrelatedProcess.Id) 'An unrelated PowerShell process was incorrectly matched.'

    Stop-TranscencodeProcesses -RootPaths @($installDirectory, $backupDirectory)
    Assert-True ($appProcess.WaitForExit(10000)) 'The matching Transcencode process did not exit.'
    Assert-True (-not $unrelatedProcess.HasExited) 'The unrelated PowerShell process was stopped.'

    Invoke-InstallFileOperation -Description 'Backing up the existing Transcencode installation' -Operation {
        Move-Item -LiteralPath $installDirectory -Destination $backupDirectory -Force -ErrorAction Stop
    }
    Assert-True (Test-Path -LiteralPath $backupDirectory) 'The previously locked installation directory was not moved after shutdown.'

    # Exercise retry behavior against a genuine exclusive Windows file handle.
    $lockedFile = Join-Path $outsideDirectory 'transient-lock.bin'
    [IO.File]::WriteAllBytes($lockedFile, [byte[]](1,2,3,4))
    $lockScript = Join-Path $outsideDirectory 'Hold-Lock.ps1'
    @'
param([string]$Path)
$stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try { Start-Sleep -Seconds 2 } finally { $stream.Dispose() }
'@ | Set-Content -LiteralPath $lockScript -Encoding ASCII
    $lockStart = New-Object System.Diagnostics.ProcessStartInfo
    $lockStart.FileName = $powershell
    $lockStart.Arguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -File "{0}" -Path "{1}"' -f $lockScript, $lockedFile
    $lockStart.WorkingDirectory = $outsideDirectory
    $lockStart.UseShellExecute = $false
    $lockStart.CreateNoWindow = $true
    $lockProcess = [System.Diagnostics.Process]::Start($lockStart)
    Start-Sleep -Milliseconds 700

    Invoke-InstallFileOperation -Description 'Removing a transiently locked test file' -MaximumAttempts 20 -DelayMilliseconds 250 -Operation {
        Remove-Item -LiteralPath $lockedFile -Force -ErrorAction Stop
    }
    Assert-True (-not (Test-Path -LiteralPath $lockedFile)) 'The retry operation did not remove the file after its lock was released.'
    Assert-True (($script:Log | Where-Object { $_ -like 'Removing a transiently locked test file failed on attempt*' }).Count -gt 0) 'The test did not exercise a real retry.'

    Invoke-InstallFileOperation -Description 'Restoring interrupted update state' -Operation {
        Move-Item -LiteralPath $backupDirectory -Destination $installDirectory -Force -ErrorAction Stop
    }
    Assert-True (Test-Path -LiteralPath $installDirectory) 'The interrupted-update recovery move failed.'

    Write-Host 'TRANSCENCODE_0292_INSTALLER_LOCK_TEST_PASSED'
    Write-Host ('Detected and stopped PID {0}; unrelated PID {1} remained alive; real locked-file retry completed.' -f $appProcess.Id, $unrelatedProcess.Id)
}
finally {
    foreach ($process in @($appProcess, $unrelatedProcess, $lockProcess)) {
        if ($null -ne $process) {
            try { if (-not $process.HasExited) { $process.Kill() } } catch { }
            try { $process.Dispose() } catch { }
        }
    }
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
