#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Registers a daily Windows Task Scheduler job to run the PS price checker.

.DESCRIPTION
    Creates a scheduled task named "PSTitlePriceNotification" that runs once
    per day at the configured time.

    Execution order:
      1. publish\PSPriceNotification.exe  (recommended — build with: dotnet publish -c Release -o publish)
      2. dotnet run --project <dir>        (requires .NET SDK)

.PARAMETER RunAt
    Time of day to run the check, e.g. "09:00". Defaults to "09:00".

.PARAMETER TaskName
    Name for the scheduled task. Defaults to "PSTitlePriceNotification".

.EXAMPLE
    .\setup_task.ps1
    .\setup_task.ps1 -RunAt "08:30"

.NOTES
    Run this script once as Administrator.
    To remove the task later:
        Unregister-ScheduledTask -TaskName "PSTitlePriceNotification" -Confirm:$false
#>
param(
    [string] $RunAt    = "09:00",
    [string] $TaskName = "PSTitlePriceNotification"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ScriptDir = $PSScriptRoot

# ---------------------------------------------------------------------------
# Resolve the .NET executable
# ---------------------------------------------------------------------------

$publishedExe = Join-Path $ScriptDir "publish\PSPriceNotification.exe"
if (Test-Path $publishedExe) {
    $chosen = @{ Execute = $publishedExe; Argument = ""; Label = ".NET (published exe)" }
} else {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    $csproj = Join-Path $ScriptDir "PSPriceNotification.csproj"
    if ($dotnet -and (Test-Path $csproj)) {
        $chosen = @{
            Execute  = $dotnet.Source
            Argument = "run --project `"$ScriptDir`" --configuration Release"
            Label    = ".NET (dotnet run)"
        }
    } else {
        Write-Error @"
No executable found. Publish the app first:
  dotnet publish -c Release -o publish
or install the .NET SDK so 'dotnet run' is available.
"@
        exit 1
    }
}

Write-Host "Runtime selected: $($chosen.Label)" -ForegroundColor Cyan

# ---------------------------------------------------------------------------
# Build the scheduled task
# ---------------------------------------------------------------------------

$Action = New-ScheduledTaskAction `
    -Execute          $chosen.Execute `
    -Argument         $chosen.Argument `
    -WorkingDirectory $ScriptDir

$Trigger = New-ScheduledTaskTrigger -Daily -At $RunAt

$Settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit     (New-TimeSpan -Hours 4) `
    -RestartCount           3 `
    -RestartInterval        (New-TimeSpan -Minutes 15) `
    -StartWhenAvailable `
    -RunOnlyIfNetworkAvailable

$Principal = New-ScheduledTaskPrincipal `
    -UserId    "$env:USERDOMAIN\$env:USERNAME" `
    -LogonType Interactive `
    -RunLevel  Limited

try {
    $Task = Register-ScheduledTask `
        -TaskName    $TaskName `
        -Action      $Action `
        -Trigger     $Trigger `
        -Settings    $Settings `
        -Principal   $Principal `
        -Description "Daily PlayStation Store price notification check ($($chosen.Label))." `
        -Force

    Write-Host ""
    Write-Host "Task '$($Task.TaskName)' registered successfully." -ForegroundColor Green
    Write-Host "  Runs daily at : $RunAt"
    Write-Host "  Runtime       : $($chosen.Label)"
    Write-Host "  Executable    : $($chosen.Execute)"
    if ($chosen.Argument) {
        Write-Host "  Arguments     : $($chosen.Argument)"
    }
    Write-Host "  Working dir   : $ScriptDir"
    Write-Host ""
    Write-Host "Useful commands:" -ForegroundColor DarkGray
    Write-Host "  Start-ScheduledTask -TaskName '$TaskName'                           # run now"
    Write-Host "  Get-ScheduledTask   -TaskName '$TaskName' | Get-ScheduledTaskInfo   # last result"
    Write-Host "  Unregister-ScheduledTask -TaskName '$TaskName' -Confirm:`$false      # remove"
} catch {
    Write-Error "Failed to register scheduled task: $_"
    exit 1
}

