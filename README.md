# Smart Stylist

An AI personal stylist that plans a wardrobe and buys only real deals, with delivered cost to Georgia.
The goal and the next steps are in [docs/MISSION.md](docs/MISSION.md); how the code is laid out is in
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). Today the app collects prices from shops, sale emails, Awin feeds and
a browser extension, judges whether a sale is real, and keeps a fit ledger.

## What is here

| Part | What it does |
| --- | --- |
| `src/Modules/Catalog` | The Catalog module: shops, products, daily price snapshots, collectors, the data-quality report. |
| `src/Modules/Wardrobe` | The Wardrobe module: each person's style profile, fit ledger and size advice. |
| `src/SmartStylist.Worker` | Background host. Runs Hangfire and one daily price job per shop. Also a small CLI (`collect`, `report`). |
| `src/SmartStylist.Web` | Blazor web app: data quality (`/`), the quality catalogue (`/catalogue`), style profiles and quiz (`/style`), deals (`/deals`), fit ledger (`/fit`), the extension and price-history APIs, the Hangfire dashboard. |
| `tests/` | Parser tests with fixture pages, end-to-end tests against in-memory SQLite, and a few on real Postgres (need Docker). |

It is a modular monolith: each business area becomes a module under `src/Modules` with its own
PostgreSQL schema (`catalog`, `wardrobe`, later `orders`, `landedcost`), and both hosts load the modules they need.

## Quick start

You need the .NET 10 SDK and Docker.

```bash
docker compose up -d                                         # PostgreSQL 16 on localhost:5432 (POSTGRES_PORT=5433 if taken)
dotnet run --project src/SmartStylist.Worker -- install-browser   # once: headless Chromium for shops that block plain HTTP
dotnet run --project src/SmartStylist.Worker -- collect --limit 20   # quick test: 20 products per shop, then prints the report
```

If port 5432 is taken, start Postgres with `POSTGRES_PORT=5433 docker compose up -d` and set the same
`POSTGRES_PORT=5433` when you run the worker and the web app; both apps read it and no JSON edit is needed.

The **Deals** page (`/deals`) lists recent prices that are on sale or below their usual price, with a verdict:
buy now (never been cheaper, or a rare sale on an item usually at full price), wait (it has been cheaper), fake sale (the "was" price is fiction), or still learning (under 14 days of history). No percentage thresholds: the history decides.

That run takes roughly 10 to 20 minutes for all 15 shops, because the collector waits 1.5 s between pages.
It applies database migrations itself.

Other commands:

```bash
dotnet run --project src/SmartStylist.Worker -- collect levis uniqlo   # only some shops (full 300 products)
dotnet run --project src/SmartStylist.Worker -- report                 # print the report again
dotnet run --project src/SmartStylist.Worker -- detect [shop ...]                 # find each shop's platform (Shopify, Salesforce, Magento, Next.js...)
dotnet run --project src/SmartStylist.Worker -- probe <product url> [--browser]   # what the parser reads from one page; saves the HTML
dotnet run --project src/SmartStylist.Worker -- newsletters [--folder <dir>]           # price new sale emails (mailbox, or saved .eml files)
dotnet run --project src/SmartStylist.Worker                           # leave running: collects every shop daily at 05:00 UTC
dotnet run --project src/SmartStylist.Web                              # http://localhost:5227 (/deals, report, /hangfire jobs, browser extension)
```

## How prices are collected

Each shop is tried with the cheapest source first, and the one that works is remembered for the next day:

1. **Shopify**: the public `/products.json` feed. Every product and size, with the "compare at" price. Cleanest data.
2. **SitemapJsonLd**: product URLs from `robots.txt` → sitemaps, then the schema.org JSON-LD (or Open Graph
   price tags) on each product page, over plain HTTP. Most big shops publish this for Google Shopping.
3. **BrowserJsonLd**: the same, but pages are opened in a browser. Not tried automatically any more: set
   `"Strategy": "BrowserJsonLd"` on a shop to use it. Blocked shops get the browser watch below instead.
4. **UniqloApi** (set per shop): Uniqlo's own commerce API (`.../products/<code>/price-groups/00/l2s` and
   `/details`), every colour and size with base and promo price and stock. The page's JSON-LD shows a $1.90-$3.90
   placeholder on sold-out items, so the page is not used.

Every price from a crawl, the extension, the browser watch, an Awin feed or the archive passes a **sanity check** before it is saved (`Catalog:PriceSanity`): it is refused
when it is under 15% of the SKU's usual (median) price or of its own crossed-out price, or under the floor for
its category (tees $5, outerwear $20, footwear $15..., matched on title words; floors apply to USD prices).
`collect` prints how many were refused and why.

### Geo-blocked shops and the US egress

Kohl's, Levi's and brooksbrothers.com load from a US address but block (Akamai 403) or redirect (to
brooksbrothers.eu) visitors from Georgia. That is geography, not a bot check. Their shops name an egress
(`"Egress": "us"`), and their plain HTTP requests (collect, probe, detect, sitemaps) go out through it, at most
one request per `MinIntervalMs` (5 s) across those shops. No other shop uses it. Any other shop that redirects to
its country site is retried once through `Catalog:GeoRetryEgress`, or reported as "blocked by geography".

Point the egress at an HTTP or SOCKS proxy in the US. A small US VPS works with an SSH tunnel:

```bash
ssh -N -D 1080 me@my-us-vps &
dotnet user-secrets set "Catalog:Egresses:us:ProxyUrl" "socks5://127.0.0.1:1080" --project src/SmartStylist.Worker
```

Until a ProxyUrl is set, those shops are not scheduled and `collect` says which setting is missing.

### The browser watch (J.Crew, lululemon)

Shops with `"BrowserWatch": true` load in Chrome but not over plain HTTP. They are never crawled; only the
product pages you watch are visited, in the visible Chrome window (`Catalog:Browser`), at most once per 4 hours
and on `Catalog:BrowserWatch:Cron` (7:00, 13:00, 19:00 UTC). A bot check stops the shop for that run.

```bash
dotnet run --project src/SmartStylist.Worker -- watch add "https://www.jcrew.com/p/BE996" --size L
dotnet run --project src/SmartStylist.Worker -- watch list
dotnet run --project src/SmartStylist.Worker -- watch run     # visit what is due now
```

Under Armour is `"Dropped": true` (it cancels forwarder orders): no collection, its emails are not priced and its
products are left off /deals. adidas is never crawled; its prices come from emails, feeds and the extension.

### Blocked shops and a real Chrome window

When a shop answers with a bot-protection page, the collectors, `probe` and `detect` say who blocked it
(Akamai, Cloudflare, PerimeterX, DataDome, Imperva, Kasada...) and stop. The app never solves a challenge or
CAPTCHA, never logs in and never checks out. To try a shop the way a person would, from the home connection,
drive the installed Google Chrome in a visible window with a profile kept between runs, at a slow pace:

```bash
dotnet user-secrets set "Catalog:Browser:Channel" "chrome" --project src/SmartStylist.Worker
dotnet user-secrets set "Catalog:Browser:Headless" "false" --project src/SmartStylist.Worker
dotnet user-secrets set "Catalog:Browser:UserDataDir" "$HOME/.smart-stylist-chrome" --project src/SmartStylist.Worker
dotnet user-secrets set "Catalog:RequestDelayMs" "6000" --project src/SmartStylist.Worker
dotnet run --project src/SmartStylist.Worker -- probe "https://www.levi.com/US/en_US/.../p/045415" --browser
```

Use a separate profile folder, not your everyday Chrome profile: Chrome locks a profile while it is open.
The user agent and fingerprint are left as Chrome reports them; faking them is the aggressive evasion this app
stays away from. A shop that still blocks a real Chrome is a job for an affiliate feed, sale emails or the extension.

Up to 300 products per shop are priced each day (`Catalog:MaxProductsPerShop`). Products priced in the last
week are revisited first, so the same items build a continuous history. One price is stored per product size
per day; a second run on the same day overwrites it.

## The data-quality report

`collect` and `report` print a table, and the web app shows the same on its home page:

| Column | Meaning |
| --- | --- |
| Grade | **Clean**: 90%+ of pages gave a price and half or more of prices have a size. **Usable**: 60%+ of pages gave a price. **Poor**: less. **Failing**: no source worked on the last run. |
| Strategy | Which source worked. |
| Pages ok | Share of product pages on the last run that gave a price. |
| Sizes / Stock | Share of today's prices that come with a size / an in-stock flag. Per-size prices matter for fit. |
| On sale | Share of today's prices with a crossed-out "was" price. |
| Days | Days of history collected. Verdicts need about 30. |
| Moved | Products whose price changed across days, the signal we are after. |

Failing shops list the reason under the table, such as "HTTP 403" (blocked) or "no product URLs in the sitemap"
(the URL pattern needs tuning).

## The quality catalogue: brands and shops

`src/Modules/Catalog/SmartStylist.Modules.Catalog/catalogue.json` is the curated list: **brands** worth owning (men's
and women's, with categories, style words and one line on why the quality is real) and the **shops** that sell them,
the brand's own store first, then retailers such as Mr Porter or REI. Both hosts load it; the app refuses to start
when it has a mistake (a brand naming a missing shop, two shops on one site, an unknown inbox) and says which.

Each brand is read by the best price source that works at any of its shops, in this order:

1. **Feed**: a joined Awin programme, a Shopify `products.json` or the Uniqlo API. Every product, every day.
2. **PageData**: the daily JSON-LD crawl, or the real-Chrome watch for J.Crew and lululemon.
3. **SaleEmail**: the shop's newsletter (`"NewsletterInbox": "personal"` = it reaches your Gmail). Prices only when a sale is on.
4. **Extension**: the browser extension while you look at a page (`"ExtensionWorks": true` once it saved a price there).

```bash
dotnet run --project src/SmartStylist.Worker -- catalogue    # each brand's best working source and what was priced in 30 days
```

The same view is on the web app at `/catalogue`. The command also lists what is waiting (Awin applications, the US
egress) and the shops nobody has tried yet.

Shop fields that matter here: `"Enabled": false` with `"BlockedBy"` (who blocks the crawl) for blocked shops; `false`
without it for shops added but not tried yet (run `detect` and `probe`, then turn them on). Blocked and untried shops
stay listed so their sale emails, Awin feed and extension prices still match them. A brand's Awin applications sit on
the brand (`"Awin": [{"Programme": "Lacoste UK", "Status": "Pending"}]`), because they are often a country store.

Not in the catalogue on purpose: Under Armour (cancels forwarder orders, kept as `Dropped`), and fast-fashion or
mass-market senders such as Mango, GU, Nike, Calvin Klein and Decathlon. Dressup.ge stays as a local shop for the
extension until the brands it carries are added.

The `ProductUrlPattern` values are educated guesses at each shop's product URL shape. If a shop fails with "no
product URLs in the sitemap", open its sitemap in a browser and fix the pattern, or set `SitemapUrl` directly. If
pages load but give no price, run `probe` on one product URL and look at the saved HTML.

### Adding or tuning a shop

```json
{ "Key": "arket", "Name": "Arket", "BaseUrl": "https://www.arket.com/en-us", "Currency": "USD",
  "Strategy": "Auto", "ProductUrlPattern": "/en-us/product/", "SitemapUrl": null }
```

`Strategy` can be `Auto` (Shopify feed, then sitemap + JSON-LD), `Shopify`, `SitemapJsonLd`, `BrowserJsonLd` or
`UniqloApi`. Set `"Enabled": false` to stop collecting a shop; its history is kept.

## Prices from your own browser

`extension/` is a Chrome extension for the shops that block the collectors. When you open a page on one of your
shops, it sends the page's structured data (JSON-LD, microdata, price meta tags), never the rest of the page, to
`POST http://localhost:5227/api/captures`. `SmartStylist.Web` parses it like a crawled page and stores the price
with `Source = Extension`. The popup shows the price, sizes in stock and the lowest price seen from any source.

To use it, start the web app, open `chrome://extensions`, turn on Developer mode, choose **Load unpacked** and pick
the `extension` folder. Nothing is saved while the app is off; the toolbar icon then shows a red `!`.

A new shop has to be added both to `catalogue.json` and to `matches` in `extension/manifest.json`; a test fails when one is missing.

## Outside evidence: history before two weeks

A verdict normally needs 14 days of the app's own prices. To judge sales sooner (before Black Friday), seed
history from prices the app did not see itself. They are stored with source `Archive` or `ManualHistory`, never
overwrite a price the app saw on the same day, and a verdict that leans on them says "Less certain" and shows
a "less certain" tag on /deals. Outside evidence counts once it gives earlier days spread over 21+ days worth
3 archived days; written-down prices count less (confidence 0.4 against 0.6), so they need 5 days.

```bash
# Old copies of product pages from the Wayback Machine (archive.org), up to 12 per product, one request every 4 s.
dotnet run --project src/SmartStylist.Worker -- wayback taylorstitch --limit 20      # a shop's products with < 14 days
dotnet run --project src/SmartStylist.Worker -- wayback "https://www.jcrew.com/p/..."  # one page (added if new)

# Long-running core products first (Catalog:CoreProducts: Levi's 541, Reigning Champ Midweight crew, Uniqlo
# Extra Fine Merino, Patagonia Better Sweater). Plain `wayback` runs core, then every shop.
dotnet run --project src/SmartStylist.Worker -- wayback core

# Dated prices written down elsewhere: a CSV, or the Wardrobe doc's Recommended and Near-misses tables pasted
# into a .md file as they are.
dotnet run --project src/SmartStylist.Worker -- import-prices prices.csv
dotnet run --project src/SmartStylist.Worker -- import-prices wardrobe-tables.md --date 2026-11-20
```

The Wayback lookup asks for the exact product page, including copies saved with a query string (`?variant=...`),
which is most of the archived history for Shopify and Patagonia pages.

Columns are found by name: `price` is required ("~~$98~~ $54" and "$54 (was $98)" also give the was price),
plus `date` (or `--date`), and a page address in a `url` column or as a link in any cell. Without an address a
row needs a `shop`/`store` and a `title`/`item` (or `brand` + `model`, with `colour` and `style_code` added to the
title). `list_price`/`was`, `size`, `colour` and `currency` are optional; `price_after_code` is read but not stored.
A store that is not one of the shops (REI, END.) gets its own disabled shop (`manual-rei`) so its prices still
count; "Levi's US" matches the Levi's shop when the currency agrees. Dates
like `2026-10-14`, `Oct 14 2026` or `Nov 18` (the latest such day). Keepa covers Amazon price history but needs a paid
API key, so it is not wired in.

## Price-history API

The web app serves the numbers the stylist layer needs:

| Endpoint | Returns |
| --- | --- |
| `GET /api/products/{id}/history?colour=&size=` | Current price, all-time low (and when), 90-day low, median, how many days at each price level, and the daily series. |
| `GET /api/drops?since=2026-11-20&sizes=M,L,32` | SKUs (colour + size) that hit a new low or came back in stock since that day; `sizes` keeps only yours. |

Page reading tries, in order: schema.org JSON-LD, microdata, the app state the page embeds (`__NEXT_DATA__`,
`__INITIAL_STATE__`, `__PRELOADED_STATE__`, JSON `__NUXT__`), then Open Graph tags. Each price is stored per SKU:
product + colour + size. Shops in `catalogue.json` can carry `AcceptsForwarder`, `AcceptsForeignCard` and `CheckoutNote`,
learned from real orders; the data-quality page shows them with the detected platform.

## Style profile and quiz

Everything personal belongs to one person's **style profile** (no sign-in yet: Fit and Deals have a "For" picker that
keeps `?person=ID` in the address). **Style** (`/style`) lists people; **Take the style quiz** (`/style/new`) has five steps:
who it is for (men's / women's / both) and a style goal; love or skip catalogue brands; the style words read from those
brands (tap to like / avoid); kinds of item to want or never buy plus free no-go words (`logo`, `polyester`); usual sizes.
The page shows live which catalogue brands fit the person and why (`StyleQuiz.Match`: loved brand +2, each liked style
word +1, each avoided one -2, a wanted kind of item +1; a skipped brand, a no-go word or the wrong audience excludes it).
Retake the quiz whenever a style changes. Fits recorded before profiles existed now belong to a profile called "Me":
rename it in the quiz.

## Fit ledger

The **Fit** page (`/fit`) records how each item fitted after delivery: brand, model, kind of item, size, and
too small / snug / perfect / roomy / too big. "Which size should I buy?" answers from these records: the same model
first, then the same kind of item from the brand, then the brand, then the person's usual size from the quiz (low confidence). The Deals page shows "Your size" next to
brands the picked person has records for. Each person has their own ledger. Records live in the `wardrobe` schema (Wardrobe module), created by the worker or the web app on start.

## Prices from Awin product feeds

Awin (publisher id 3156285) gives each programme you join a daily product feed with prices, "was" prices and sizes.
Get the feed API key in Awin under **Toolbox → Create-a-Feed**, then:

```bash
dotnet user-secrets set "Awin:ApiKey" "<key>" --project src/SmartStylist.Worker
dotnet run --project src/SmartStylist.Worker -- awin-feeds   # which feeds exist and which programmes accepted you
dotnet run --project src/SmartStylist.Worker -- awin         # import today's prices from every joined programme
```

With the key set, the worker also imports daily on `Awin:Cron` (06:00 UTC). `Awin:FeedIds` limits the import to
some feeds; empty means every joined programme. Up to `Awin:MaxProductsPerFeed` (5000) products per feed are saved.
Each advertiser gets its own shop, `awin-<advertiser id>` (no crawler runs for it), unless a shop in `catalogue.json`
has a matching `"AwinAdvertiserId"`. Feed prices show on the Deals page as "Awin feed".

## Prices from sale emails

Shops that block the collectors still email their sales. `newsletters` reads those emails and adds the prices
to the same price history, marked `Source = Newsletter` and dated on the day the email arrived.

How one email is handled:

1. The sender is matched to a shop by domain (`jcrew@e.jcrew.com` → `jcrew.com`). Senders that match no shop
   are recorded as `UnknownSender` and never sent anywhere. Add `"EmailDomains": ["levi.com"]` to a shop if its
   emails come from a different domain. A shop email with no `List-Unsubscribe` header (order update, password
   reset) is recorded as `Transactional` and never sent to the model. Forwarded emails skip this check.
2. The HTML is turned into plain text. Links become short ids like `[L3]`.
3. Claude (`Newsletters:Model`, default `claude-haiku-5-5`) lists the products whose prices are printed, copying
   each price exactly as written.
4. Code checks every price: the same number must appear in the email text, the "was" price must be higher, and
   a currency symbol in the price wins over what the model said. Anything that fails is dropped and counted as
   rejected. The model never decides a stored amount on its own.
5. Product links are followed through the tracking redirects, but only up to the shop's own host. The shop page
   itself is not downloaded. When a link resolves, the product is keyed by its page URL, so the same product from
   another email or from a crawl lines up. When it doesn't, the product is keyed by its title.
6. A crawled price for the same product and day is never overwritten by an email price.

Forwarded emails work too, so you can forward sale emails to a mailbox the worker can reach. For inline
forwards (Gmail "Forward", Outlook "FW:") the shop is read from the forwarded "From:" line; for "Forward as
attachment" the attached email is used as is. A forwarded email's prices are dated on the day it was forwarded.

Every email is stored once by Message-ID. Failed emails (for example an API outage) are retried on the next read.

### Setup

Create an API key at https://console.anthropic.com (separate from a Claude subscription; add a card and a small
credit top-up), then keep it out of git:

```bash
dotnet user-secrets set "Newsletters:AnthropicApiKey" "sk-ant-..." --project src/SmartStylist.Worker
```

Try it on saved emails first. In Gmail, open a sale email, ⋮ → "Download message", put the `.eml` files in a
folder, then:

```bash
dotnet run --project src/SmartStylist.Worker -- newsletters --folder ~/sale-emails
```

To read a mailbox every hour, fill in `Newsletters:Imap` and set the password as a secret. The default folder is
Gmail's `[Gmail]/All Mail`, so emails that a filter or the stylist routine labelled and archived (e.g.
`Stylist/Reviewed`) are read too; set `Imap:Folder` to one label to read only that. Create an app password
(Google account → Security → 2-Step Verification → App passwords):

```bash
dotnet user-secrets set "Newsletters:Imap:Host" "imap.gmail.com" --project src/SmartStylist.Worker
dotnet user-secrets set "Newsletters:Imap:Username" "you@gmail.com" --project src/SmartStylist.Worker
dotnet user-secrets set "Newsletters:Imap:Password" "<app password>" --project src/SmartStylist.Worker
dotnet run --project src/SmartStylist.Worker -- newsletters
```

The mailbox is opened read-only: nothing is marked read, moved or deleted. With `Imap:Host` set, the Hangfire
worker also schedules `import-newsletters` on `Newsletters:Cron`. A dedicated mailbox that you forward sale emails
to works the same way.

Cost: one model call per new email, with effort set to low. With Claude Haiku 5.5 a typical sale email (about 15k tokens)
costs roughly $0.002, so $5 of API credit covers about 2,000 emails. Set `Newsletters:Model` to `claude-opus-5-5`
for harder emails at about $0.08 each.

## Development

```bash
dotnet test                       # needs Docker: the email date tests run on real Postgres
dotnet tool restore
dotnet ef migrations add <Name> --project src/Modules/Catalog/SmartStylist.Modules.Catalog \
  --startup-project src/SmartStylist.Worker --output-dir Persistence/Migrations
```

Money is `decimal(12,2)` with its currency on every row; nothing is converted yet.
