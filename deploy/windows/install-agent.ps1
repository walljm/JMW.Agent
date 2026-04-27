# install-agent.ps1
# Installs jmw-agent as a SYSTEM Scheduled Task that auto-starts at boot.
#
# Expects to find the staged files in $HOME:
#   $HOME\jmw-agent.exe
#   $HOME\agent.toml
#
# Layout produced:
#   C:\Program Files\jmw-agent\jmw-agent.exe
#   C:\ProgramData\jmw-agent\agent.toml
#   C:\ProgramData\jmw-agent\logs\agent.log

$ErrorActionPreference = 'Stop'

$installDir = 'C:\Program Files\jmw-agent'
$dataDir    = 'C:\ProgramData\jmw-agent'
$logDir     = Join-Path $dataDir 'logs'

New-Item -ItemType Directory -Force -Path $installDir, $dataDir, $logDir | Out-Null

# Stop existing task if present so we can replace the binary.
if (Get-ScheduledTask -TaskName 'JMW Agent' -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName 'JMW Agent' -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

$stagedBin = Join-Path $HOME 'jmw-agent.exe'
$stagedCfg = Join-Path $HOME 'agent.toml'

if (-not (Test-Path $stagedBin)) { throw "missing $stagedBin" }
if (-not (Test-Path $stagedCfg)) { throw "missing $stagedCfg" }

Move-Item -Force $stagedBin (Join-Path $installDir 'jmw-agent.exe')
Move-Item -Force $stagedCfg (Join-Path $dataDir    'agent.toml')

$exe     = Join-Path $installDir 'jmw-agent.exe'
$cfg     = Join-Path $dataDir    'agent.toml'
$logFile = Join-Path $logDir     'agent.log'

# Use cmd.exe wrapper so we can redirect stderr to a log file.
$argLine = '/c "' + '"' + $exe + '"' + ' -config "' + $cfg + '" >> "' + $logFile + '" 2>&1"'

$action    = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument $argLine
$trigger   = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet `
    -RestartCount 999 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero)

Register-ScheduledTask -TaskName 'JMW Agent' `
    -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
    -Force | Out-Null

Start-ScheduledTask -TaskName 'JMW Agent'
Start-Sleep -Seconds 2

Get-ScheduledTask -TaskName 'JMW Agent' |
    Select-Object TaskName, State |
    Format-Table -AutoSize

if (Test-Path $logFile) {
    Write-Host "--- last 20 lines of $logFile ---"
    Get-Content -Tail 20 $logFile
} else {
    Write-Host "no log file yet at $logFile"
}
