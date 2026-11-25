# Mission

**An AI stylist that dresses people well for little money, with no time spent shopping.**

It learns a person's style, plans their wardrobe and buys only what is worth it: high-quality
items at a price that is really low. Then it gets them delivered to Georgia without the forwarder
hassle. Over time the user "is what the app wears": they look good, spend less and spend no time
choosing.

A user says after a month: *"I stopped buying things I don't wear, I saved money, and I didn't
spend time shopping."*

## Who it is for

- **Any shopper in Georgia** (men's and women's), then Armenia, then the region (Azerbaijan,
  Kazakhstan, Uzbekistan, Kyrgyzstan, Moldova; Russian reaches most of it). Not Russia or Belarus
  (sanctions). A new country must be configuration (currency, customs rule, forwarders), not code.
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

## Roadmap

Product shape: the morning stylist routine Temo already uses, made repeatable for other people.
One shared pool of deal sources (feeds, a shared brand inbox, page data, the extension) and a
personal filter on top (style, sizes, "buy now or wait"). Coverage grows through **affiliate
feeds**: be on every feed that exists. Networks accept sites with an audience, so the public
deals channel is the engine for both users and feeds.

Each stage has Temo's own steps (outside the code) and a check that says the stage is done.

**Stage 1 - Foundation (December 2026)**
- Temo: buy a domain for the site; open a public Telegram deals channel; create the shared
  app Gmail and subscribe it to every catalogue shop's newsletter; apply to the networks the
  research names, in its order.
- Code: shared inbox import; networks research; post real deals to the channel.
- Check: channel posts at least 3 real deals a week; shared inbox brings sale prices daily;
  applications sent to every network on the list.

**Stage 2 - Friends (January 2027)**
- Temo: pick 2-3 friends whose style the catalogue covers; each fills in the style quiz.
- Code: smart pick score and daily picks on Telegram (style quiz is done).
- Check: each friend gets picks for 2+ weeks; "buy now" right 8 of 10 times (track every
  call and its outcome).

**Stage 3 - First outside users (February 2027)**
- Temo: reapply to networks that declined, with channel and user numbers.
- Code: sign-up and own profile without Temo's help.
- Check: at least one person Temo did not set up uses it weekly; a second network accepted.

**Stage 4 - Real cost and forwarders (spring 2027)**
- Landed cost in GEL on every pick, then forwarder declarations and tracking. Then Armenia.

**Direction check.** A weekly review (scheduled, Sunday) reads this file, the commits and the
data, and writes one entry to [REVIEWS.md](REVIEWS.md): stage progress against its check,
the signals below, and one sentence on whether we are still heading towards the mission. Signals:
- Coverage: catalogue brands with a working price source; feeds/networks accepted.
- Value: picks sent, "buy now" hit rate, money saved, items bought and worn.
- Reach: channel followers, active users outside Temo.
- Health: codebase size trend, CI green, Temo's hours that week.

## Next up

Each item is one worker thread. When one is done, tick it, add what was learned, and keep this
list at 3-5 items.

1. **Shared brand inbox.** Import sale emails from one app Gmail subscribed to every catalogue
   shop's newsletter (`NewsletterInbox: "shared"`), so outside users do not depend on Temo's Gmail.
   Temo creates the account; the thread builds the import and lists which shops to subscribe.
2. **Networks research.** Every affiliate network and aggregator that carries fashion feeds
   (Awin, CJ, Rakuten, impact.com, FlexOffers, Partnerize, Skimlinks, Sovrn, ShopStyle and
   others): requirements, whether Georgia is accepted, brands covered, feed or API access, and an
   apply order. Output: a tracker in `docs/` with status and reapply dates.
3. **Smart pick and notification.** One score per item for a given person (quality, style fit (`StyleQuiz.Match`),
   size confidence, price vs realistic low) and a rule for when it is worth telling them. First
   channel: Telegram. Record every "buy now" and its outcome for the 8-of-10 check.
4. **First outside user ready.** Sign-up, their own profile, and their own picks.

Later: close the catalogue's gaps (about 9 brands, mostly women's, have no price source: try
`detect`, `probe`, extension), landed cost in GEL, forwarder declarations and tracking, phone
app, Armenia.

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
