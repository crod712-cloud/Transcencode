#requires -version 5.1
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$script:Log = New-Object System.Collections.Generic.List[string]

function Write-InstallLog {
    param([Parameter(Mandatory = $true)][string]$Text)
    $script:Log.Add($Text)
}

function Test-PathUnderRoot {
    param([string]$Path, [string[]]$Roots)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    foreach ($root in $Roots) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        $prefix = $root.TrimEnd('\') + '\'
        if ($Path.Equals($root.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) -or
            $Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return $false
}

function Get-TranscencodeProcesses {
    param([Parameter(Mandatory = $true)][string[]]$RootPaths)

    $roots = @($RootPaths | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object {
        try { [IO.Path]::GetFullPath($_).TrimEnd('\') } catch { $_.TrimEnd('\') }
    } | Select-Object -Unique)

    try { $rows = @(Get-CimInstance -ClassName Win32_Process -ErrorAction Stop) }
    catch { $rows = @(Get-WmiObject -Class Win32_Process -ErrorAction Stop) }

    $matches = @()
    foreach ($row in $rows) {
        $processId = [int]$row.ProcessId
        if ($processId -eq $PID) { continue }
        $name = [string]$row.Name
        $path = [string]$row.ExecutablePath
        $command = [string]$row.CommandLine

        $namedGui = $name.Equals('Transcencode.exe', [StringComparison]::OrdinalIgnoreCase)
        $scriptHost = ($name.Equals('powershell.exe', [StringComparison]::OrdinalIgnoreCase) -or
                       $name.Equals('pwsh.exe', [StringComparison]::OrdinalIgnoreCase)) -and
                      (-not [string]::IsNullOrWhiteSpace($command)) -and
                      ($command.IndexOf('Launch-Transcencode.ps1', [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                       $command.IndexOf('Transcencode.ps1', [StringComparison]::OrdinalIgnoreCase) -ge 0)
        $engineUnderInstall = $name.Equals('HandBrakeCLI.exe', [StringComparison]::OrdinalIgnoreCase) -and
                              (Test-PathUnderRoot -Path $path -Roots $roots)
        $anyExecutableUnderInstall = Test-PathUnderRoot -Path $path -Roots $roots

        if ($namedGui -or $scriptHost -or $engineUnderInstall -or $anyExecutableUnderInstall) {
            $matches += [pscustomobject]@{
                ProcessId = $processId
                Name = $name
                ExecutablePath = $path
                CommandLine = $command
            }
        }
    }
    return @($matches)
}

function Stop-TranscencodeProcesses {
    param([Parameter(Mandatory = $true)][string[]]$RootPaths)
    $processes = @(Get-TranscencodeProcesses -RootPaths $RootPaths)
    foreach ($item in $processes) {
        try {
            $process = [Diagnostics.Process]::GetProcessById([int]$item.ProcessId)
            if ($process.MainWindowHandle -ne [IntPtr]::Zero) { [void]$process.CloseMainWindow() }
            $process.Dispose()
        } catch { }
    }
    Start-Sleep -Milliseconds 700
    foreach ($item in @(Get-TranscencodeProcesses -RootPaths $RootPaths)) {
        try { Stop-Process -Id ([int]$item.ProcessId) -Force -ErrorAction Stop } catch { }
    }
    for ($attempt = 1; $attempt -le 40; $attempt++) {
        if (@(Get-TranscencodeProcesses -RootPaths $RootPaths).Count -eq 0) {
            Start-Sleep -Milliseconds 300
            return
        }
        Start-Sleep -Milliseconds 250
    }
    throw 'Matching Transcencode processes did not exit.'
}

function Invoke-InstallFileOperation {
    param(
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][scriptblock]$Operation,
        [int]$MaximumAttempts = 25,
        [int]$DelayMilliseconds = 250
    )
    $lastError = $null
    for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
        try { & $Operation; return }
        catch {
            $lastError = $_
            Write-InstallLog ('{0} failed on attempt {1}: {2}' -f $Description, $attempt, $_.Exception.Message)
            if ($attempt -lt $MaximumAttempts) { Start-Sleep -Milliseconds $DelayMilliseconds }
        }
    }
    throw ('{0} failed after {1} attempts: {2}' -f $Description, $MaximumAttempts, $lastError.Exception.Message)
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$root = Join-Path $env:TEMP ('Transcencode-lock-v3-' + [guid]::NewGuid().ToString('N'))
$install = Join-Path $root 'Programs\Transcencode'
$backup = $install + '.previous'
$outside = Join-Path $root 'outside'
New-Item -ItemType Directory -Path $install, $outside -Force | Out-Null
$launch = Join-Path $install 'Launch-Transcencode.ps1'
'Start-Sleep -Seconds 300' | Set-Content -LiteralPath $launch -Encoding ASCII
$unrelated = Join-Path $outside 'Unrelated.ps1'
'Start-Sleep -Seconds 300' | Set-Content -LiteralPath $unrelated -Encoding ASCII
$ps = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$app = $null
$other = $null
$locker = $null

try {
    $appInfo = New-Object Diagnostics.ProcessStartInfo
    $appInfo.FileName = $ps
    $appInfo.Arguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -File "{0}"' -f $launch
    $appInfo.WorkingDirectory = $install
    $appInfo.UseShellExecute = $false
    $appInfo.CreateNoWindow = $true
    $app = [Diagnostics.Process]::Start($appInfo)

    $otherInfo = New-Object Diagnostics.ProcessStartInfo
    $otherInfo.FileName = $ps
    $otherInfo.Arguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -File "{0}"' -f $unrelated
    $otherInfo.WorkingDirectory = $outside
    $otherInfo.UseShellExecute = $false
    $otherInfo.CreateNoWindow = $true
    $other = [Diagnostics.Process]::Start($otherInfo)

    Start-Sleep -Milliseconds 1000
    Assert-True (-not $app.HasExited) 'Simulated Transcencode process exited too early.'
    $detected = @(Get-TranscencodeProcesses -RootPaths @($install, $backup))
    $ids = @($detected | ForEach-Object { [int]$_.ProcessId })
    Assert-True ($ids -contains $app.Id) 'Transcencode PowerShell host was not detected.'
    Assert-True ($ids -notcontains $other.Id) 'Unrelated PowerShell host was incorrectly detected.'

    Stop-TranscencodeProcesses -RootPaths @($install, $backup)
    Assert-True ($app.WaitForExit(10000)) 'Transcencode PowerShell host did not stop.'
    Assert-True (-not $other.HasExited) 'Unrelated PowerShell host was stopped.'

    Invoke-InstallFileOperation -Description 'Move prior install' -Operation {
        Move-Item -LiteralPath $install -Destination $backup -Force -ErrorAction Stop
    }
    Assert-True (Test-Path -LiteralPath $backup) 'Prior install was not moved after process shutdown.'

    $locked = Join-Path $outside 'locked.bin'
    [IO.File]::WriteAllBytes($locked, [byte[]](1,2,3,4))
    $lockScript = Join-Path $outside 'Hold.ps1'
    @'
param([string]$Path)
$stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try { Start-Sleep -Seconds 2 } finally { $stream.Dispose() }
'@ | Set-Content -LiteralPath $lockScript -Encoding ASCII
    $lockInfo = New-Object Diagnostics.ProcessStartInfo
    $lockInfo.FileName = $ps
    $lockInfo.Arguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -File "{0}" -Path "{1}"' -f $lockScript, $locked
    $lockInfo.WorkingDirectory = $outside
    $lockInfo.UseShellExecute = $false
    $lockInfo.CreateNoWindow = $true
    $locker = [Diagnostics.Process]::Start($lockInfo)
    Start-Sleep -Milliseconds 600

    Invoke-InstallFileOperation -Description 'Remove transiently locked file' -MaximumAttempts 20 -Operation {
        Remove-Item -LiteralPath $locked -Force -ErrorAction Stop
    }
    Assert-True (-not (Test-Path -LiteralPath $locked)) 'Locked file remained after lock release and retries.'
    Assert-True (($script:Log | Where-Object { $_ -like 'Remove transiently locked file failed on attempt*' }).Count -gt 0) 'A real retry was not exercised.'

    Invoke-InstallFileOperation -Description 'Restore prior install' -Operation {
        Move-Item -LiteralPath $backup -Destination $install -Force -ErrorAction Stop
    }
    Assert-True (Test-Path -LiteralPath $install) 'Rollback restore did not complete.'

    Write-Host 'TRANSCENCODE_0292_INSTALLER_LOCK_TEST_PASSED'
    Write-Host ('Stopped Transcencode PID {0}; preserved unrelated PID {1}; retried a genuine Windows file lock.' -f $app.Id, $other.Id)
}
finally {
    foreach ($process in @($app, $other, $locker)) {
        if ($null -ne $process) {
            try { if (-not $process.HasExited) { $process.Kill() } } catch { }
            try { $process.Dispose() } catch { }
        }
    }
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
