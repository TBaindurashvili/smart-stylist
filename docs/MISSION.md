# Mission

**An AI stylist that dresses people well for little money, with no time spent shopping.**

It learns a person's style, plans their wardrobe and buys only what is worth it: high-quality
items at a price that is really low. Then it gets them delivered to Georgia without the forwarder
hassle. Over time the user "is what the app wears": they look good, spend less and spend no time
choosing.

A user says after a month: *"I stopped buying things I don't wear, I saved money, and I didn't
spend time shopping."*

## Who it is for

- **Any shopper in Georgia** (men's and women's), then Armenia, then wider.
- It is a **product**, not a personal tool. Temo is the first user; the first outside user
  comes **within 3 months (by end of February 2027)**.

## What it must do

Temo's priority answers (Nov 25) were close together: all of these matter. Landed cost scored
lowest (4), "is this sale real" next (5), and the rest equal (6).

1. **Know the person's style.** Style quizzes that detect what they want and can steer to a new
   style at any time. Sizes per brand (a fit ledger) so the size is right first time.
1. **Plan the wardrobe.** What's missing, what to skip, what goes with what.
2. **Buy smart.** Is the sale real, buy now or wait, always with a reason. No fixed % thresholds:
   a deal is a price near the item's realistic low.
3. **Quality first.** Cover every source of high-quality clothing, because different users have
   different styles; never push low-quality items just because they are cheap.
4. **Notify intelligently.** It speaks up on its own when it finds something really worth it for
   *this* person (quality, fit, style, price). No fixed number of alerts and no simple threshold.
   Channels: daily email, Telegram, the website and phone push.
6. **Real cost to Georgia.** Price + shipping + forwarder + VAT/fees, in GEL.
7. **Forwarders in one place.** Declare and track parcels across forwarders from the app instead
   of logging in to each forwarder's site. Forwarders are partners, not competitors.

Success after 3 months: **"I stopped buying things I don't wear"** and **"I saved money"**.
"Buy now" must be right **8 times out of 10**.

## How we build

- **Quality over speed.** Temo has under 3 hours a week, so every change must be worth keeping.
- Keep the codebase small and clean: refactor and delete regularly, prefer simple designs, test
  what matters, and follow good development practice (CI, reviews of our own work, clear docs).
- Parse data carefully and in detail. A wrong price is worse than no price.
- Commit straight to `main` (no PRs, no extra branches). Temo pulls and tests locally.
- Hard rules: never solve CAPTCHAs, never log in to shops on a user's behalf without them doing
  it themselves, never automate checkout, respect robots.txt and terms, rate-limit per site, never
  recommend splitting shipments to avoid customs, keep secrets in user-secrets.

## Next up

Each item is one worker thread. When one is done, tick it, add what was learned, and keep this
list at 3-5 items.

1. **Quality catalogue.** A curated list of high-quality brands and shops (men's and women's)
   with how each can be read (feed, API, email, extension). This replaces "which shops can we
   scrape" with "which shops matter".
2. **Style profile and quiz.** A first quiz that captures a person's style, sizes and no-go
   items, stored per user. This is the base for everything personal.
3. **Smart pick and notification.** One score per item for a given person (quality, style fit,
   size confidence, price vs realistic low) and a rule for when it is worth telling them. First
   channel: Telegram or daily email.
4. **First outside user ready.** Sign-up, their own profile, and their own picks.

Later: landed cost in GEL on every pick, forwarder declarations and tracking in the app,
phone app, Armenia.

## Done

- **Codebase health pass** (2026-11-25). Architecture note in [ARCHITECTURE.md](ARCHITECTURE.md). Learned: the
  code had little dead code but one big copy: the product + price save was written three times, and Awin prices
  skipped the sanity check. Now one `PriceWriter` serves crawl, extension, browser watch and Awin. Blocked shops
  stay in config (they still match emails, feeds and the extension); the quality catalogue will curate them.
