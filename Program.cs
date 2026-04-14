using System.Text.Json;
using PSPriceNotification.Models;
using PSPriceNotification.Services;
using Spectre.Console;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

// ─── Banner ───────────────────────────────────────────────────────────────────
AnsiConsole.Write(new FigletText("PS Price Watch").LeftJustified().Color(Color.DeepSkyBlue1));
AnsiConsole.MarkupLine("[grey]PlayStation Store price monitor — github.com/yourname/PS-TitlePriceNotification[/]");
AnsiConsole.WriteLine();

if (args.Contains("--help") || args.Contains("-h")) { PrintHelp(); return; }

// ─── Bootstrap ───────────────────────────────────────────────────────────────
var config  = LoadConfig(args);
Logger.Configure(config.Logging.File, config.Logging.Level);

var locales = config.Locales is { Count: > 0 }
    ? new Dictionary<string, string>(config.Locales, StringComparer.OrdinalIgnoreCase) as IReadOnlyDictionary<string, string>
    : PSStoreClient.DefaultLocales;

if (args.Contains("--list-countries")) { PrintCountries(locales, config); return; }

const string FavoritesPath = "favorites.json";
var favorites  = LoadFavorites(FavoritesPath);
var countries  = ResolveCountries(args, config, locales);

// ─── Cancellation ────────────────────────────────────────────────────────────
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    AnsiConsole.MarkupLine("\n[yellow]Cancellation requested — finishing current requests...[/]");
};
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    try { cts.Cancel(); } catch (ObjectDisposedException) { }
};

// ─── PSN auth + wishlist ──────────────────────────────────────────────────────
PsnAuthService? auth = await AuthenticateAsync(config, cts.Token);
favorites = await MergeWishlistAsync(favorites, config, locales, cts.Token);

// ─── Guard ───────────────────────────────────────────────────────────────────
if (favorites.Count == 0)
{
    AnsiConsole.MarkupLine($"[yellow]No games found in {FavoritesPath} (and PSN wishlist is empty) — nothing to check.[/]");
    return;
}

// ─── Run ─────────────────────────────────────────────────────────────────────
AnsiConsole.MarkupLine(
    $"Checking [bold]{favorites.Count}[/] game(s) across [bold]{countries.Count}[/] country/countries " +
    $"([bold]{config.Checking.MaxConcurrency}[/] parallel)...\n");

using var client  = new PSStoreClient(locales, auth);
using var storage = new PriceStorage(config.Storage.PriceHistoryDb, config.Storage.MaxHistoryDays);
var notifier      = new Notifier(config.Notification);

var (totalChecked, totalChanges, summaryRows) =
    await RunChecksAsync(favorites, countries, locales, config, client, storage, notifier, cts.Token);

storage.Save();
PrintSummaryTable(summaryRows, totalChecked, totalChanges);
Logger.Info($"Done. Checked {totalChecked} game*country pairs, found {totalChanges} price change(s).");

// ═════════════════════════════════════════════════════════════════════════════
// Local functions
// ═════════════════════════════════════════════════════════════════════════════

static void PrintHelp()
{
    AnsiConsole.Write(new Panel(
        "[bold]Usage:[/] PSPriceNotification [[options]]\n\n" +
        "[deepskyblue1]--countries[/] CODE,CODE,...  Check only these country codes\n" +
        "[deepskyblue1]--list-countries[/]           Print all supported country codes and locales\n" +
        "[deepskyblue1]--config[/] FILE              Config file path [grey](default: config.yaml)[/]\n" +
        "[deepskyblue1]--help[/]                     Show this help")
    {
        Header = new PanelHeader(" Help "),
        Border = BoxBorder.Rounded,
    });
}

static AppConfig LoadConfig(string[] args)
{
    var path = GetArg(args, "--config") ?? "config.yaml";
    try
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        return deserializer.Deserialize<AppConfig>(File.ReadAllText(path));
    }
    catch (Exception ex)
    {
        AnsiConsole.WriteException(ex, ExceptionFormats.ShortenPaths);
        AnsiConsole.MarkupLine($"[red]Failed to load config:[/] {path}");
        Environment.Exit(1);
        return null!;
    }
}

static List<GameEntry> LoadFavorites(string path)
{
    try
    {
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        return JsonSerializer.Deserialize<FavoritesFile>(File.ReadAllText(path), opts)?.Games ?? [];
    }
    catch (Exception ex)
    {
        AnsiConsole.WriteException(ex, ExceptionFormats.ShortenPaths);
        Environment.Exit(1);
        return null!;
    }
}

static List<string> ResolveCountries(string[] args, AppConfig config, IReadOnlyDictionary<string, string> locales)
{
    var countriesArg = GetArg(args, "--countries");
    var countries = countriesArg != null
        ? countriesArg
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.ToLowerInvariant())
            .ToList()
        : config.Checking.Countries ?? locales.Keys.ToList();

    var primary = config.Checking.PrimaryCountry?.ToLowerInvariant();
    if (primary != null && locales.ContainsKey(primary))
        countries = countries.Where(c => c != primary).Prepend(primary).ToList();

    var invalid = countries.Where(c => !locales.ContainsKey(c)).ToList();
    if (invalid.Count > 0)
        AnsiConsole.MarkupLine($"[yellow]Unknown country codes (skipped):[/] {string.Join(", ", invalid)}");

    return countries.Where(c => locales.ContainsKey(c)).ToList();
}

static void PrintCountries(IReadOnlyDictionary<string, string> locales, AppConfig config)
{
    var source = config.Locales is { Count: > 0 } ? "[green]config.yaml[/]" : "[grey]built-in[/]";
    var table  = new Table()
        .Border(TableBorder.Rounded)
        .AddColumn(new TableColumn("[bold]Code[/]").Centered())
        .AddColumn("[bold]Locale[/]")
        .AddColumn("[grey]Source[/]");

    foreach (var (code, locale) in locales.OrderBy(kv => kv.Key))
        table.AddRow($"[deepskyblue1]{code.ToUpperInvariant()}[/]", locale, source);

    AnsiConsole.Write(table);
}

static async Task<PsnAuthService?> AuthenticateAsync(AppConfig config, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(config.Account.Npsso)) return null;

    var auth = new PsnAuthService(config.Account.Npsso, config.Account.TokenCacheFile);
    try
    {
        await auth.EnsureAuthenticatedAsync(ct);
        AnsiConsole.MarkupLine("[green]✓ Authenticated with PSN (PS Plus prices enabled)[/]");
        return auth;
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[yellow]⚠ PSN authentication failed — continuing anonymously.[/]\n  {Markup.Escape(ex.Message)}");
        auth.Dispose();
        return null;
    }
}

static async Task<List<GameEntry>> MergeWishlistAsync(
    List<GameEntry> favorites,
    AppConfig config,
    IReadOnlyDictionary<string, string> locales,
    CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(config.Account.Npsso)) return favorites;

    var primary        = config.Checking.PrimaryCountry?.ToLowerInvariant();
    var wishlistLocale = primary != null && locales.TryGetValue(primary, out var pl) ? pl : "en-us";

    using var svc     = new PsnWishlistService(config.Account.Npsso);
    var wishlistItems = await svc.GetWishlistAsync(wishlistLocale, ct);

    if (wishlistItems.Count == 0)
    {
        AnsiConsole.MarkupLine("[grey]PSN wishlist: empty or unavailable — using favorites.json only.[/]");
        return favorites;
    }

    var knownIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var g in favorites)
    {
        if (g.ConceptId != null) knownIds.Add(g.ConceptId);
        if (g.ProductId != null) knownIds.Add(g.ProductId);
    }

    var newItems = wishlistItems
        .Where(w => (w.ConceptId == null || !knownIds.Contains(w.ConceptId))
                 && (w.ProductId == null || !knownIds.Contains(w.ProductId)))
        .ToList();

    if (newItems.Count > 0)
    {
        List<GameEntry> merged = [.. favorites, .. newItems];
        AnsiConsole.MarkupLine(
            $"[deepskyblue1]+ {newItems.Count} title(s) added from PSN wishlist[/] [grey]({merged.Count} total)[/]");
        return merged;
    }

    AnsiConsole.MarkupLine($"[grey]PSN wishlist: {wishlistItems.Count} item(s) — all already in favorites.json.[/]");
    return favorites;
}

static async Task<(int TotalChecked, int TotalChanges, List<(string Game, string Country, string OldPrice, string NewPrice, bool Changed)> Rows)>
    RunChecksAsync(
        List<GameEntry> games,
        List<string> countries,
        IReadOnlyDictionary<string, string> locales,
        AppConfig config,
        PSStoreClient client,
        PriceStorage storage,
        Notifier notifier,
        CancellationToken ct)
{
    int totalChecked = 0;
    int totalChanges = 0;
    var summaryRows  = new List<(string Game, string Country, string OldPrice, string NewPrice, bool Changed)>();
    var storageLock  = new object();

    try
    {
        foreach (var game in games)
        {
            if (ct.IsCancellationRequested) break;

            if (game.Id == null)
            {
                AnsiConsole.MarkupLine($"[yellow]Skipping '{Markup.Escape(game.Name)}' — no concept_id or product_id.[/]");
                continue;
            }

            var validCountries = (game.Countries ?? countries).Where(c => locales.ContainsKey(c)).ToList();
            var skipped        = new System.Collections.Concurrent.ConcurrentBag<string>();
            var perGameRows    = new System.Collections.Concurrent.ConcurrentBag<(string, string, string, string, bool)>();

            await AnsiConsole.Progress()
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask($"[deepskyblue1]{Markup.Escape(game.Name)}[/]", maxValue: validCountries.Count);
                    using var sem = new SemaphoreSlim(config.Checking.MaxConcurrency);

                    await Task.WhenAll(validCountries.Select(async country =>
                    {
                        await sem.WaitAsync(ct);
                        try
                        {
                            if (config.Checking.RequestDelay > 0)
                                await Task.Delay(TimeSpan.FromSeconds(config.Checking.RequestDelay), ct);

                            var price = await client.GetPriceAsync(game.Id, game.IdType, country, ct);
                            task.Increment(1);
                            Interlocked.Increment(ref totalChecked);

                            if (price == null)          { skipped.Add(country.ToUpperInvariant()); return; }
                            if (!price.IsAvailable)     { Logger.Debug($"[{country.ToUpperInvariant()}] Not available"); return; }
                            if (price.CurrentPrice == null) Logger.Warn($"[{country.ToUpperInvariant()}] Price could not be parsed");

                            bool hasChanged;
                            PriceInfo? oldPrice;
                            lock (storageLock)
                            {
                                hasChanged = storage.HasPriceChanged(game.Id, country, price);
                                oldPrice   = storage.GetLastPrice(game.Id, country);
                                storage.UpdatePrice(game.Id, country, price, game.Name);
                            }

                            if (hasChanged)
                            {
                                var url = client.GetStoreUrl(game.Id, game.IdType, country);
                                Logger.Info($"[{country.ToUpperInvariant()}] PRICE CHANGE: {oldPrice} -> {price}");
                                await notifier.NotifyPriceChangeAsync(game.Name, country, url, oldPrice, price);
                                Interlocked.Increment(ref totalChanges);
                            }

                            perGameRows.Add((game.Name, country.ToUpperInvariant(), oldPrice?.ToString() ?? "-", price.ToString(), hasChanged));
                        }
                        catch (OperationCanceledException) { }
                        finally { sem.Release(); }
                    }));
                });

            summaryRows.AddRange(perGameRows);

            if (!skipped.IsEmpty)
                AnsiConsole.MarkupLine(
                    $"  [yellow]⚠ Could not retrieve price for:[/] [grey]{string.Join(", ", skipped.OrderBy(c => c))}[/]");
        }
    }
    catch (OperationCanceledException)
    {
        AnsiConsole.MarkupLine("[yellow]Run cancelled.[/]");
    }

    return (totalChecked, totalChanges, summaryRows);
}

static void PrintSummaryTable(
    List<(string Game, string Country, string OldPrice, string NewPrice, bool Changed)> rows,
    int totalChecked,
    int totalChanges)
{
    AnsiConsole.WriteLine();

    var changedRows = rows.Where(r => r.Changed).ToList();
    if (changedRows.Count > 0)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold yellow] Price Changes Detected [/]")
            .AddColumn("[bold]Game[/]")
            .AddColumn(new TableColumn("[bold]Country[/]").Centered())
            .AddColumn("[bold]Previous[/]")
            .AddColumn("[bold]Current[/]");

        foreach (var (game, country, old, now, _) in changedRows)
            table.AddRow(
                Markup.Escape(game),
                $"[deepskyblue1]{country}[/]",
                $"[grey]{Markup.Escape(old)}[/]",
                $"[bold green]{Markup.Escape(now)}[/]");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    var statusColour = totalChanges > 0 ? "green" : "grey";
    var statusMsg    = totalChanges > 0
        ? $"[bold green]{totalChanges} price change(s) found![/]"
        : "[grey]No price changes.[/]";

    AnsiConsole.Write(new Rule($"Checked [bold]{totalChecked}[/] pairs · {statusMsg}").RuleStyle(statusColour));
}

static string? GetArg(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}

