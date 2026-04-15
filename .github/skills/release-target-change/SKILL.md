---
name: release-target-change
description: 'Update or review framework targets, publish commands, GitHub Actions, scheduler scripts, or platform-specific runtime behavior. Use for net10.0 vs Windows target changes, launchd or cron setup, Task Scheduler setup, CI fixes, and release documentation updates.'
argument-hint: 'Describe the platform, release, or CI change needed'
user-invocable: true
---

# Release Target Change

## When to Use
- Target frameworks, Windows targeting, or publish commands change.
- GitHub Actions builds fail on Windows or Ubuntu.
- `setup_task.ps1` or `setup_task.sh` need updates.
- Windows-only notification behavior changes.
- README build, publish, or scheduling instructions need to stay in sync.

## Primary Files
- `PSPriceNotification.csproj`
- `PSPriceNotification.Tests/PSPriceNotification.Tests.csproj`
- `.github/workflows/build.yml`
- `Services/Notifier.cs`
- `Services/Notifier.Windows.cs`
- `setup_task.ps1`
- `setup_task.sh`
- `README.md`

## Procedure
1. Inspect the project file, workflow, runtime-specific code, and scripts as one unit.
2. Decide which behavior is cross-platform and which is intentionally Windows-only.
3. Keep explicit framework arguments in scripts whenever multi-targeting can make `dotnet run` ambiguous.
4. Update CI to match supported hosts and syntax/runtime prerequisites.
5. Refresh README examples if commands or platform support changed.
6. Validate with build/test commands available in the repo.

## Validation Checklist
- `net10.0` remains runnable for cross-platform scenarios.
- Windows-only code stays isolated to Windows-targeted files or package conditions.
- CI paths and shell invocations are portable across the intended runners.
- Script help text matches actual behavior.

## Suggested Commands
- `dotnet build PSPriceNotification.csproj -c Debug`
- `dotnet test PSPriceNotification.Tests/PSPriceNotification.Tests.csproj -c Debug`
- Review `.github/workflows/build.yml` together with `setup_task.ps1`, `setup_task.sh`, and `README.md`