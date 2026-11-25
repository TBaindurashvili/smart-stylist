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

1. **Smart pick and notification.** One score per item for a given person (quality, style fit (`StyleQuiz.Match`),
   size confidence, price vs realistic low) and a rule for when it is worth telling them. First
   channel: Telegram or daily email.
2. **First outside user ready.** Sign-up, their own profile, and their own picks.
3. **Close the catalogue's gaps.** About 9 brands (mostly women's) have no working price source and most others only have sale emails:
   try the untried shops (`detect`, `probe`, extension), and decide on one shared app inbox subscribed to every
   catalogue shop's newsletter (`NewsletterInbox: "shared"`), so outside users do not depend on Temo's Gmail.

Later: landed cost in GEL on every pick, forwarder declarations and tracking in the app,
phone app, Armenia.

## Done

- **Style profile and quiz** (2026-11-25). `/style`: one profile per person (Temo, then friends; men's, women's or
  both), a five-step quiz (love / skip catalogue brands, style words read from them, want / never kinds of item and
  free no-go words, usual sizes) and a live list of catalogue brands that fit the person. `StyleQuiz.Match` scores a
  brand or item for a person; the next item builds on it. The fit ledger is now per person and falls back to the
  quiz's usual size. Learned: the catalogue's style words are uneven ("classic" is on 17 of 41 brands, 14 words on
  one brand), so loving brands says more than picking words; products have no kind-of-item field yet, so matching an
  item uses its title words. No sign-in yet: the person is picked on each page (`?person=ID`); sign-up is the "First outside user" item.
  Old fit records moved to a profile called "Me".

- **Quality catalogue** (2026-11-25). `catalogue.json` in the Catalog module: 41 brands (36 for women, 37 for men) with
  categories, style words and why each is quality, and 51 shops (brand stores and retailers). Each brand shows its best
  working price source (1 feed, 2 page data, 3 sale emails, 4 extension) and what was priced in 30 days: `catalogue`
  CLI and `/catalogue`. Learned: Temo's Gmail already gets ~35 quality shops' newsletters, so sale emails are today's
  main source (29 brands); only Uniqlo has a daily feed, J.Crew and lululemon the Chrome watch, and about 9 brands (mostly
  women's) have nothing yet. Awin applications sit on brands because they are country stores (Lacoste UK/FR/DE); two
  shops on one site are refused because the extension could not tell them apart. Blocked shops now say who blocks them
  (`BlockedBy`); Under Armour stays dropped; Dressup.ge stays for the extension. Taylor Stitch and Reigning Champ were
  missing from the extension; a test now checks every shop is covered.

- **Codebase health pass** (2026-11-25). Architecture note in [ARCHITECTURE.md](ARCHITECTURE.md). Learned: the
  code had little dead code but one big copy: the product + price save was written three times, and Awin prices
  skipped the sanity check. Now one `PriceWriter` serves crawl, extension, browser watch and Awin. Blocked shops
  stay in config (they still match emails, feeds and the extension); the quality catalogue will curate them.
