---
name: ps-store-scrape-change
description: 'Update or review PS Store scraping, wishlist parsing, NPSSO auth, locale extraction, or parser regression coverage. Use for HTML parsing changes, __NEXT_DATA__ changes, wishlist extraction bugs, locale-map refresh logic, and scraper test updates.'
argument-hint: 'Describe the scraping or parser issue to investigate'
user-invocable: true
---

# PS Store Scrape Change

## When to Use
- PS Store product pages changed shape and prices are not being parsed.
- Wishlist extraction from `https://library.playstation.com/wishlist` needs to change.
- NPSSO-authenticated behavior or PS Plus pricing needs adjustment.
- Locale discovery or locale-map maintenance changed.
- A scraping-related change needs tests before merging.

## Primary Files
- `Services/PSStoreParser.cs`
- `Services/PsnWishlistService.cs`
- `Services/PSStoreClient.cs`
- `Services/PsnAuthService.cs`
- `PSPriceNotification.Tests/Services/PSStoreParserTests.cs`
- `PSPriceNotification.Tests/Services/PsnWishlistServiceTests.cs`
- `PSPriceNotification.Tests/Services/PSStoreClientTests.cs`
- `Update-Locales.ps1`
- `Update-Locales.sh`

## Procedure
1. Reproduce the issue from the failing behavior, log message, or example payload.
2. Decide whether the problem is fetch/auth, HTML selection, JSON traversal, or ID normalization.
3. Prefer robust fallbacks over brittle exact-shape assumptions.
4. Keep extraction logic inside the parser/service layer, not in `Program.cs`.
5. Add or update focused tests in the matching `PSPriceNotification.Tests/Services` file.
6. Run targeted tests first, then a wider build/test pass if the change touches shared logic.

## Validation Checklist
- Parser fallback order still makes sense.
- Empty, malformed, and unexpected payloads fail safely.
- Wishlist merging still deduplicates by concept or product ID.
- Logs remain actionable without exposing secrets.

## Suggested Commands
- `dotnet test PSPriceNotification.Tests/PSPriceNotification.Tests.csproj --filter "FullyQualifiedName~PSStoreParserTests"`
- `dotnet test PSPriceNotification.Tests/PSPriceNotification.Tests.csproj --filter "FullyQualifiedName~PsnWishlistServiceTests"`
- `dotnet build PSPriceNotification.csproj -c Debug`