<#
.SYNOPSIS
    Fetches up-to-date PS Store locale codes from the PlayStation country selector
    and updates the 'locales:' section in config.yaml.

.DESCRIPTION
    Scrapes https://www.playstation.com/country-selector/index.html, extracts all
    country-code → locale mappings, compares with the current config.yaml, and
    writes the merged/updated result back.

    Deduplication policy: when a country has multiple locale variants (e.g. Ukraine
    has ru-ua and uk-ua), the first one listed on the country selector page is kept.
    Review the diff output and edit config.yaml manually if a different variant is
    needed for a specific country.

.PARAMETER ConfigPath
    Path to config.yaml. Defaults to config.yaml in the same directory as this script.

.PARAMETER DryRun
    Print the discovered locales and diff without writing any changes.

.EXAMPLE
    .\Update-Locales.ps1
    .\Update-Locales.ps1 -DryRun
    .\Update-Locales.ps1 -ConfigPath "C:\path\to\config.yaml"
#>
param(
    [string] $ConfigPath = (Join-Path $PSScriptRoot "config.yaml"),
    [switch] $DryRun
)

$ErrorActionPreference = "Stop"

# ─── Fetch country selector ────────────────────────────────────────────────────

Write-Host "Fetching PlayStation country selector..." -ForegroundColor Cyan
try {
    $response = Invoke-WebRequest `
        -Uri "https://www.playstation.com/country-selector/index.html" `
        -UseBasicParsing `
        -TimeoutSec 30
} catch {
    Write-Error "Failed to fetch country selector: $_"
}
$html = $response.Content

# ─── Parse locales ────────────────────────────────────────────────────────────

# Matches href="https://www.playstation.com/{locale}/"
# Locale examples: en-us, ru-ua, zh-hant-tw, zh-hans-cn, sr-rs
$pattern = 'href="https://www\.playstation\.com/([a-z]{2,}(?:-[a-z0-9]+)+)/"'
$found   = [regex]::Matches($html, $pattern)

if ($found.Count -eq 0) {
    Write-Error "No locales found — the page structure may have changed."
}

# Build ordered map: country code (last 2 chars of locale) → first locale seen
$localeMap = [ordered]@{}
foreach ($m in $found) {
    $locale  = $m.Groups[1].Value
    # Extract country code: last hyphen-segment, always 2 chars
    if ($locale -match '-([a-z]{2})$') {
        $country = $Matches[1]
        if (-not $localeMap.ContainsKey($country)) {
            $localeMap[$country] = $locale
        }
    }
}

Write-Host "Found $($localeMap.Count) country codes." -ForegroundColor Green

# ─── Read existing config ──────────────────────────────────────────────────────

if (-not (Test-Path $ConfigPath)) {
    Write-Error "Config file not found: $ConfigPath"
}

$configText = [System.IO.File]::ReadAllText($ConfigPath) -replace "`r`n", "`n"

# Extract existing locales from the  'locales:' block (lines like "  xy: locale")
$existingMap = @{}
$existingMatches = [regex]::Matches($configText, '(?m)^ {2}([a-z]{2}): ([a-z][a-z0-9\-]+)')
foreach ($m in $existingMatches) {
    $existingMap[$m.Groups[1].Value] = $m.Groups[2].Value
}

# ─── Diff ─────────────────────────────────────────────────────────────────────

$added   = @($localeMap.Keys | Where-Object { -not $existingMap.ContainsKey($_) } | Sort-Object)
$removed = @($existingMap.Keys | Where-Object { -not $localeMap.ContainsKey($_) } | Sort-Object)
$changed = @($localeMap.Keys | Where-Object {
    $existingMap.ContainsKey($_) -and $existingMap[$_] -ne $localeMap[$_]
} | Sort-Object)

if ($added.Count -eq 0 -and $removed.Count -eq 0 -and $changed.Count -eq 0) {
    Write-Host "No changes detected — config.yaml is already up to date." -ForegroundColor Green
    exit 0
}

if ($added) {
    foreach ($c in $added) {
        Write-Host "  + $c : $($localeMap[$c])" -ForegroundColor Green
    }
}
if ($changed) {
    foreach ($c in $changed) {
        Write-Host "  ~ $c : $($existingMap[$c]) → $($localeMap[$c])" -ForegroundColor Cyan
    }
}
if ($removed) {
    foreach ($c in $removed) {
        Write-Host "  - $c : $($existingMap[$c])" -ForegroundColor Yellow
    }
}

if ($DryRun) {
    Write-Host "`nDry run — no changes written." -ForegroundColor Yellow
    exit 0
}

# ─── Build new locales YAML block ─────────────────────────────────────────────

$sorted    = $localeMap.GetEnumerator() | Sort-Object Key
$yamlLines = @("locales:")
foreach ($entry in $sorted) {
    $yamlLines += "  $($entry.Key): $($entry.Value)"
}
$newBlock = ($yamlLines -join "`n") + "`n"

# ─── Replace locales block in config ─────────────────────────────────────────
# Matches 'locales:' followed by its indented content (stops at blank line or EOF)

$blockPattern = '(?m)^locales:\n((?:[ \t][^\n]*\n?)*)'

if ([regex]::IsMatch($configText, $blockPattern)) {
    $updated = [regex]::Replace($configText, $blockPattern, $newBlock)
} else {
    # No existing locales block — append it
    $updated = $configText.TrimEnd() + "`n`n$newBlock"
}

[System.IO.File]::WriteAllText($ConfigPath, $updated, [System.Text.Encoding]::UTF8)
Write-Host "`nconfig.yaml updated ($($localeMap.Count) countries)." -ForegroundColor Green
Write-Host "Review any 'changed' or 'removed' entries above before committing," -ForegroundColor Yellow
Write-Host "especially countries with multiple locale variants (e.g. ua: ru-ua vs uk-ua)." -ForegroundColor Yellow
