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
$wrapper = Join-Path $installDir 'jmw-agent-launcher.cmd'

# Launcher wrapper: promotes any staged auto-update (.new file) before
# launching the agent. The agent writes <exe>.new and exits when it
# receives an update; Scheduled Task restart-on-failure runs this wrapper
# again, which renames .new over the real exe and starts the new image.
$wrapperBody = @"
@echo off
setlocal
set ""EXE=$exe""
set ""NEW=$exe.new""
set ""CFG=$cfg""
set ""LOG=$logFile""

:loop
if exist ""%NEW%"" (
  echo [%date% %time%] applying staged update>> ""%LOG%""
  move /Y ""%NEW%"" ""%EXE%"" >> ""%LOG%"" 2>&1
)
""%EXE%"" -config ""%CFG%"" >> ""%LOG%"" 2>&1
echo [%date% %time%] agent exited with code %ERRORLEVEL%, restarting in 5s>> ""%LOG%""
timeout /t 5 /nobreak >nul
goto loop
"@

Set-Content -Path $wrapper -Value $wrapperBody -Encoding ASCII

$action    = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument ('/c "' + $wrapper + '"')
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
