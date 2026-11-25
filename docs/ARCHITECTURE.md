# Architecture

One page on how the code is laid out and where a change belongs. The why is in [MISSION.md](MISSION.md); how to
run things is in the [README](../README.md).

## Shape

A **modular monolith** in .NET 10: one PostgreSQL database, one schema per module, two hosts that load the modules.

```
src/
  Modules/Catalog     shops, products, daily prices, every way a price gets in, deals   (schema: catalog)
  Modules/Wardrobe    each person: style profile + quiz matching, fit ledger, size advice (schema: wardrobe)
  SmartStylist.Worker Hangfire jobs + the CLI (collect, newsletters, awin, wayback, watch...)
  SmartStylist.Web    Blazor pages (/, /style, /deals, /fit, /shops/{key}), the extension and price-history APIs
extension/            Chrome extension: sends the structured data of a shop page you open to the web app
tests/                xUnit per module; SQLite in memory, plus a few Postgres tests via Testcontainers (need Docker)
```

Modules do not reference each other. A module exposes `Add<Name>Module(...)` and its services; hosts never touch
its tables directly. New business areas (picks, landed cost, forwarders) become new modules with their own schema
rather than growing Catalog. The style profile went into Wardrobe instead of its own module: it is the person the fit
ledger belongs to, and the fit ledger needs its id.

## The person (Wardrobe)

`StyleProfile` is one person (no sign-in yet; pages pick the person with `?person=ID` via `PersonPicker`). It holds
liked / avoided style words, wanted kinds of item, no-go words, loved / skipped brands and usual sizes. Every `FitEntry`
belongs to a profile. `Style/StyleQuiz` is pure: `Detect` reads style words from loved and skipped brands, `Match`
scores a brand (and optionally an item's words) for a profile. Wardrobe does not see the catalogue: the web host turns
catalogue brands into `BrandCard`s (`BrandCards.From`). `FitAdvisor` falls back to the quiz's usual size when the
ledger has nothing for a brand and the kind of item is known.

## The price pipeline (Catalog)

Everything ends as a `PriceSnapshot`: one price per product, variant (colour + size) and day, with its `Source`.

| Way in | Code | Source |
| --- | --- | --- |
| Daily crawl: Shopify feed, sitemap + JSON-LD, Uniqlo API | `Collection/PriceCollectionService` + `IShopCollector`s | `Crawl` |
| Watched pages in a real Chrome (J.Crew, lululemon) | `Collection/BrowserWatchService` | `Crawl` |
| A page the user opens, via the extension | `Collection/PageCaptureService` | `Extension` |
| Awin affiliate product feeds | `Affiliates/AwinFeedImportService` | `AffiliateFeed` |
| Sale emails, read by Claude and checked by code | `Newsletters/NewsletterImportService` + `ExtractionGuard` | `Newsletter` |
| Old prices: Wayback Machine, written-down tables | `Evidence/*` via `OutsideEvidenceWriter` | `Archive`, `ManualHistory` |

The first four share **`PriceWriter`**: it matches a product by id or page address, runs **`PriceSanityCheck`**
(refuses placeholders and teasers) and replaces today's price of each variant. Emails and outside evidence have
their own writers because they are dated in the past and must never overwrite a price the app saw itself.

Page parsing is shared too: `JsonLdProductParser` reads JSON-LD, microdata, embedded app state
(`EmbeddedStateParser`) and Open Graph tags for the crawl, the extension, the watch and `probe`.

`Catalogue/QualityCatalogue` holds the curated brands: which shops sell each one, which price route works for it
(feed, page data, sale email, extension; best first) and `FindBrand` to tell whether a product is a catalogue brand.
`Reporting/CatalogueReport` adds what was actually priced per brand in the last 30 days (`catalogue` CLI, `/catalogue`).

Reading prices back: `Reporting/DealsReport` (buy now / wait / fake sale / still learning, against the item's own
history, no % thresholds), `PriceHistoryService` (the `/api/products/{id}/history` and `/api/drops` endpoints)
and `DataQualityReport` (which shops give clean data).

## Rules the code keeps

- A wrong price is worse than no price: every source is checked before it is saved, and a verdict leaning on
  outside evidence says "less certain".
- Shops that block automated visits are named (`BotWall`) and left alone: no CAPTCHA solving, no fingerprint
  faking, no logins, no checkout. Geo-blocked shops go through a configured egress (`EgressRouter`).
- Brands and shops live in `catalogue.json` in the Catalog module (the quality catalogue, loaded by both hosts with
  `AddQualityCatalogue`), are checked on start (`QualityCatalogue.Problems`) and shops are synced to the database
  (`ShopCatalogSync`). A shop that blocks crawling stays listed with `Enabled: false` so its emails, feed and
  extension prices still match it.
- Secrets only in user-secrets or environment variables.

## Known debt

- `CollectorStrategy.BrowserJsonLd` (crawl a whole shop in the browser) is not used by any shop. It stays because
  past runs in the database store it by name.
- Hangfire setup is repeated in both hosts' `Program.cs`.
- Some services keep `internal` constructors and are registered by hand in `CatalogModule` because they take
  internal types (fetchers, collectors).
- Newsletter signups and extension results in `catalogue.json` are written by hand; nothing checks them against the
  mailbox yet.
