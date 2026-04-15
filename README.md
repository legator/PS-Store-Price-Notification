# PS Title Price Notification

![Build & Test](https://github.com/yourname/PS-TitlePriceNotification/actions/workflows/build.yml/badge.svg)

Monitors PlayStation Store prices for your favourite games across **all PS Store countries**. The core app runs cross-platform on .NET 10, and on Windows it can also send a **native toast notification** whenever a price drops or changes.

Built with **.NET 10**, with optional Windows toast notifications and OS-specific scheduling helpers.

---

## Features

- Watches **69 PS Store storefronts** — Americas, Europe, Middle East & Africa, Asia Pacific, Oceania
- **Native Windows Toast notification** with a "View on PS Store" quick-action button
- Per-game country override — monitor a title only in the regions you care about
- **SQLite price history** with configurable retention (default 90 days)
- Silent first run — stores baseline prices without flooding you with alerts
- Rich console UI with progress bars and results table powered by [Spectre.Console](https://spectreconsole.net/)
- **Optional PSN authentication** — when your NPSSO token is configured:
  - Automatically merges your **PSN Store wishlist** with `favorites.json`
  - Fetches **PS Plus member prices** (discounts visible to your account) instead of anonymous store prices

---

## Requirements

- Windows, macOS, Linux, or Raspberry Pi OS with a supported [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) or SDK
- Windows 10 version 1809 or newer if you want native toast notifications
- Internet access to reach `store.playstation.com`

---

## Project structure

```
PSPriceNotification.csproj
Program.cs                     ← CLI entry point & orchestration
Properties/
  AssemblyInfo.cs              ← InternalsVisibleTo for test project
Models/
  PriceInfo.cs
  GameEntry.cs
  AppConfig.cs
Services/
  PSStoreClient.cs             ← HTTP fetcher; delegates parsing to PSStoreParser
  PSStoreParser.cs             ← 3-strategy HTML parser (testable, no HTTP)
  PriceStorage.cs              ← SQLite history store & change detection
  PsnAuthService.cs            ← PSN OAuth2 (NPSSO → access token + refresh)
  PsnWishlistService.cs        ← PS Store wishlist scraper (NPSSO cookie auth)
  Notifier.cs                  ← cross-platform notification dispatch
  Notifier.Windows.cs          ← Windows Toast implementation
  Logger.cs                    ← Spectre.Console coloured output + file logger
PSPriceNotification.Tests/
  Helpers/
    FakeHttpMessageHandler.cs  ← test double for HTTP calls
  Models/
    PriceInfoTests.cs
  Services/
    PSStoreParserTests.cs
    PSStoreClientTests.cs
    PriceStorageTests.cs
    PsnWishlistServiceTests.cs
config.yaml                    ← countries, delays, notification settings, locales
favorites.json                 ← your games (add concept IDs here)
setup_task.ps1                 ← registers the daily Task Scheduler job on Windows
setup_task.sh                  ← installs a daily launchd/cron job on macOS/Linux
Update-Locales.ps1             ← updates the locale map in config.yaml from PS Store
Update-Locales.sh              ← POSIX shell equivalent for macOS/Linux/RPi OS
```

---

## Setup

### 1 · Clone and build

```powershell
git clone https://github.com/yourname/PS-TitlePriceNotification.git
cd PS-TitlePriceNotification
```

**Run tests** (optional but recommended before first use):

```powershell
dotnet test PSPriceNotification.Tests\PSPriceNotification.Tests.csproj
```

**Publish** a runnable build to the `publish\` folder:

```powershell
dotnet publish PSPriceNotification.csproj -c Release -r win-x64 -f net10.0-windows10.0.17763.0 -o publish
```

Windows-specific publish:

```powershell
dotnet publish PSPriceNotification.csproj -c Release -r win-x64 -f net10.0-windows10.0.17763.0 -o publish
```

Cross-platform framework-dependent run/publish:

```sh
dotnet run --project . --framework net10.0
dotnet publish PSPriceNotification.csproj -c Release -f net10.0 -o publish
```

The project uses a cross-platform `net10.0` target for the core app and a Windows-specific target for toast notifications.

### 2 · Configure `config.yaml`

```yaml
notification:
  windows_toast:
    enabled: true       # native Windows notification

checking:
  countries:            # comment out to check ALL 69 supported countries
    - us
    - gb
    - de
    - jp

  primary_country: us   # checked first for pricing output/order
  request_delay: 2.0    # seconds between PS Store requests (avoid rate limiting)
  max_concurrency: 5    # parallel requests per game
```

### 3 · (Optional) Enable PSN authentication for PS Plus prices & wishlist sync

Add your **NPSSO token** to `config.yaml`:

```yaml
account:
  npsso: "YourNpssoTokenHere"   # 64-character cookie value
```

**How to get your NPSSO token:**
1. Log in to [playstation.com](https://www.playstation.com) in your browser.
2. Visit [https://ca.account.sony.com/api/v1/ssocookie](https://ca.account.sony.com/api/v1/ssocookie) in the **same** browser tab.
3. Copy the 64-character value next to `npsso`.

**What this unlocks:**
- **PS Plus prices** — the app authenticates and sees the same prices visible to your account.
- **PSN wishlist sync** — items you've saved in the PS Store wishlist are automatically added to the price check, merged with `favorites.json`.

Tokens are cached to `data/psn_tokens.json` and refreshed automatically. You only need to re-paste the NPSSO every ~2 months when the refresh token expires.

---

### 4 · Add your favourite games to `favorites.json`

Find a game's **Concept ID** from its PS Store URL:

```
https://store.playstation.com/en-us/concept/10004409
                                                ^^^^^^^^
                                                concept_id
```

```json
{
  "games": [
    { "name": "God of War Ragnarök", "concept_id": "10004409" },
    { "name": "Marvel's Spider-Man 2", "concept_id": "10007040" }
  ]
}
```

You can also use product-level IDs:

```json
{ "name": "My Game", "product_id": "UP0001-CUSA00572_00-STARDEWVALLEY001" }
```

To check a game in specific countries only (overrides the global list):

```json
{ "name": "My Game", "concept_id": "12345678", "countries": ["us", "gb"] }
```

### 5 · Test a manual run

```powershell
.\publish\PSPriceNotification.exe

# Or run the cross-platform target directly from source
dotnet run --project . --framework net10.0
```

The first run records baseline prices silently. Subsequent runs compare against stored prices and fire notifications on any change.

---

## Schedule daily checks

Run the setup script **once as Administrator**:

```powershell
# Default: runs at 09:00 every day
.\setup_task.ps1

# Custom time
.\setup_task.ps1 -RunAt "08:00"
```

This creates a Windows Task Scheduler task called `PSTitlePriceNotification` that points at `publish\PSPriceNotification.exe` (falls back to `dotnet run` if the exe is not found).

**Useful Task Scheduler commands:**

```powershell
# Trigger immediately (for testing)
Start-ScheduledTask -TaskName "PSTitlePriceNotification"

# View last run result
Get-ScheduledTask -TaskName "PSTitlePriceNotification" | Get-ScheduledTaskInfo

# Remove the task
Unregister-ScheduledTask -TaskName "PSTitlePriceNotification" -Confirm:$false
```

### macOS / Linux / Raspberry Pi OS

There is also a POSIX shell scheduler helper:

```sh
chmod +x ./setup_task.sh
./setup_task.sh --run-at 08:00
```

On macOS it installs a per-user `launchd` agent. On Linux and Raspberry Pi OS it installs a per-user `crontab` entry.

If no native non-Windows published binary exists at `publish/PSPriceNotification`, the Unix scheduler falls back to `dotnet run --framework net10.0`.

---

## Update locale map

Windows PowerShell:

```powershell
.\Update-Locales.ps1 -DryRun
```

POSIX shell:

```sh
chmod +x ./Update-Locales.sh
./Update-Locales.sh --dry-run
```

---

## CLI reference

```
PSPriceNotification.exe [options]

Options:
  --countries CODE,CODE,...  Check only these country codes (overrides config.yaml)
  --list-countries           Print all 69 supported country codes and exit
  --config FILE              Config file path (default: config.yaml)
  --help                     Show help
```

Examples:

```powershell
.\publish\PSPriceNotification.exe --list-countries
.\publish\PSPriceNotification.exe --countries us,gb,jp,de
```

---

## Windows Toast notification

When a price drop is detected, a native notification appears:

```
🎮 God of War Ragnarök — US
Was: $69.99  •  Now: $34.99 (-50%)      [View on PS Store]
                                          PlayStation Store
```

Clicking **View on PS Store** opens the game page in your browser.

On non-Windows platforms, notification delivery is currently limited to console and log output.

---

## Data storage

Price history is saved to `data/prices.db` (SQLite, WAL mode). Entries older than `max_history_days` (default 90 days) are pruned automatically on each run.

Logs are written to `data/ps_price_check.log` (UTF-8).

---

## Notes on PS Store scraping

The app parses the server-side rendered HTML from PS Store product/concept pages using three strategies in order:

1. **Apollo GraphQL cache** — extracted from the `__NEXT_DATA__` script tag (most reliable)
2. **JSON-LD structured data** — schema.org `Product` / `VideoGame` markup
3. **Raw HTML regex** — last-resort fallback

All parsing logic lives in [Services/PSStoreParser.cs](Services/PSStoreParser.cs), separated from the HTTP client so it can be unit-tested independently without network calls.

Sony occasionally updates their page structure. If prices are not being detected:

1. Check `data/ps_price_check.log` for parse warnings.
2. Inspect `https://store.playstation.com/en-us/concept/<id>` in browser DevTools.
3. Update the extraction logic in [Services/PSStoreParser.cs](Services/PSStoreParser.cs).

---

## Unit tests

The test project targets `net10.0` and uses [xUnit](https://xunit.net/). HTTP calls are intercepted by `FakeHttpMessageHandler` — no network access needed.

**Run all tests:**

```powershell
dotnet test PSPriceNotification.Tests\PSPriceNotification.Tests.csproj
```

**Run with detailed output:**

```powershell
dotnet test PSPriceNotification.Tests\PSPriceNotification.Tests.csproj --logger "console;verbosity=normal"
```

**Run a specific test class:**

```powershell
dotnet test PSPriceNotification.Tests\PSPriceNotification.Tests.csproj --filter "FullyQualifiedName~PSStoreParserTests"
```

| Test class | What it covers |
|---|---|
| `PriceInfoTests` | `CurrentPrice`, `ToString()` variants, record equality |
| `PSStoreParserTests` | All 3 parsing strategies, edge cases (empty/malformed HTML) |
| `PSStoreClientTests` | URL generation, 404/429/5xx handling, locale dictionary |
| `PriceStorageTests` | Silent baseline, change detection, save/reload, country isolation, pruning |
| `PsnWishlistServiceTests` | `__NEXT_DATA__` wishlist parsing, deduplication, promo filtering, fallback handling |

62 tests total, all passing.
