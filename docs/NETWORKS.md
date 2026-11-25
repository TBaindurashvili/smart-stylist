# Affiliate networks: research and tracker

Goal: be on every product feed that carries the quality catalogue, so many shops are read well
(price route 1 in the catalogue). Researched 2026-11-25. Temo applies himself; this file is the
tracker. Update the **Tracker** table whenever a status changes.

Sources: network help pages (payout, eligibility), affi.io listings (which network runs which
brand's programme; third-party and approximate), news on the Rakuten + impact.com alliance.
Anything marked *not checked* still needs a look, best done from inside a network's advertiser
directory once accepted (that list is the authoritative one).

## The short answer

1. **Finish what is open**: Awin (accepted) and CJ (payment via Payoneer) need no new site.
2. **Apply now, no traffic needed**: FlexOffers, Sovrn Commerce, Admitad.
3. **After wearworth.app is live and the Telegram channel has posts (Dec-Jan)**: Rakuten
   (it is moving onto impact.com), Partnerize (Mr Porter, Huckberry).
4. **With numbers (Feb 2027, Stage 3)**: reapply impact.com, then AvantLink and Skimlinks.
5. **Skip**: creator platforms (Collective Voice, LTK, ShopMy) and EU-local networks
   (Tradedoubler, TradeTracker, Webgains, Daisycon, Adtraction, Belboon) until a brand needs one.

**Payments**: use a **Payoneer** USD receiving account everywhere. PayPal in Georgia is unclear:
PayPal's developer table says Georgia can "send, receive, and withdraw", but a Nov 2026 local
guide and PayPal community posts say Georgian accounts can only send. Do not rely on PayPal.

**History**: no network gives price history. Feeds are daily snapshots; our daily import builds
the history (and `wayback` fills the past). **Sizes**: in a feed only when the advertiser sends
them (usually one row per variant with a size field); it varies per brand.

## Brand coverage

Which networks run each quality-catalogue brand's programme (direct), or a catalogue retailer
that sells it (via). From affi.io, 2026-11-25; country stores in brackets.

| Brand | Networks (direct) | Via a catalogue retailer |
|---|---|---|
| lululemon | Awin (US, CA, UK, DE, FR, ES, IE), Rakuten (UK, DE, FR) | |
| adidas | Awin (UK, DE, FR, most of EU), FlexOffers (non-US) | |
| Reebok | Awin (EU), Rakuten (US), FlexOffers (US), TradeTracker (EU) | |
| Uniqlo | Awin (UK, DE, FR, ES, IT, NL, SE...), Rakuten (US), FlexOffers (US) | |
| Lacoste | Awin (UK, FR, DE), FlexOffers (US, CA), Admitad (KZ, feed) | |
| Clarks | Awin (UK, US), FlexOffers (US) | |
| Hilditch & Key, Peper Harow | Awin | |
| New Balance | Awin (CZ, PL, HU, SK only) | Mr Porter, SSENSE |
| Salomon | Awin (UK, DE, FR), impact (US, CA), AvantLink (US, CA) | Bergfreunde (Awin) |
| ECCO | Rakuten (US, CA), FlexOffers (US) | |
| Timberland | impact (US), FlexOffers (US), Rakuten (UK, DE, IT), Admitad (IT), Daisycon, Webgains | |
| Massimo Dutti | Rakuten (ES, UK), CJ (US, paused) | |
| Spier & Mackay | Rakuten (US), FlexOffers (US), impact | |
| COS (& Other Stories likely the same, H&M group) | Rakuten (US, UK), FlexOffers (US), Adtraction (Nordics) | |
| Charles Tyrwhitt | CJ (US, DE, FR) | |
| GANT | impact (UK, SE, FR, IT, ES, NL), Tradedoubler (PL) | |
| Quince | impact (US) | |
| Theory | Rakuten (US), FlexOffers (US) | Nordstrom (impact) |
| Vince | *not checked* | Nordstrom (impact) |
| Patagonia, Arc'teryx, The North Face | *not checked* | Bergfreunde (Awin); REI / Steep & Cheap *not checked* (likely AvantLink) |
| Reigning Champ, Sunspel, Red Wing, Barbour, A.P.C. | *not checked* | Mr Porter (Partnerize); Huckberry (Partnerize, AvantLink) for Reigning Champ, Red Wing |
| Taylor Stitch | *not checked* | Huckberry (Partnerize, AvantLink) |
| Levi's, J.Crew, Madewell, Brooks Brothers, Thursday Boots, Todd Snyder, Arket, Veja, Everlane, Sézane, Aritzia | *not checked* (affi.io failed or no page) | |

Retailers: **Mr Porter** is on Partnerize in every region (6%, "fully automated data feed");
**Huckberry** on Partnerize and AvantLink; **Nordstrom** on impact; **Bergfreunde** on Awin.
Sovrn Commerce lists most of these brands too (it sits on top of the networks).

Count of the 41 catalogue brands reachable (direct + via retailer, checked rows only):
Awin ~13, Rakuten ~9, FlexOffers ~10, impact ~7 (Rakuten's brands join it over 2026-27),
Partnerize ~8 (all through Mr Porter and Huckberry), AvantLink ~4, CJ 1-2, Admitad 2.
About 17 brands are not checked yet.

## Networks

### Awin — accepted (Publisher 3156285)
- Georgia: accepted; payouts to all non-core countries go through **Payoneer** (10 currencies).
- Feeds: product feeds per joined advertiser (CSV/XML, daily; price, RRP/was-price when sent,
  sizes as custom fields when sent). Our `awin-feeds` / `awin` CLI reads them.
- Covers the most catalogue brands. Next: apply to Uniqlo UK, adidas UK/DE, lululemon UK/US/CA,
  Salomon UK/DE, Bergfreunde, Reebok (EU) programmes. 10 programmes already pending.
- Domain: add wearworth.app as a promotional space when live; programmes review the site.

### CJ (Commission Junction) — account 8127991, payment pending
- Georgia: account works; CJ's bank deposit list lacks Georgia, so use a **Payoneer USD receiving
  account** (Payoneer runs a CJ-specific signup page).
- Feeds: Product Search / Shopping GraphQL API and product feeds per joined advertiser.
- Few catalogue brands (Charles Tyrwhitt US/DE/FR; Massimo Dutti US paused). Low effort to keep.

### FlexOffers — apply now
- Accepts international publishers; known as lenient with small sites (no stated traffic bar).
- Payouts: PayPal ($25 threshold), international wire ($1,000 threshold, $20-26 fee), check.
  Payoneer not listed: use wire to a Georgian bank or let earnings accrue ("Hold My Payments").
- Feeds: product feeds and API per approved advertiser.
- Covers US stores we lack elsewhere: Uniqlo, Lacoste, Theory, COS, ECCO, Timberland, Clarks,
  Reebok, Spier & Mackay.

### Sovrn Commerce (ex-VigLink) — apply now, biggest data win
- **Price comparison API**: "over 180 million products from hundreds of retailers in North America,
  Europe and Australia", markets like usd_en, gbp_en, eur_de; 20 requests/second; images included.
  It is "non-affiliated", so it can price items even where we have no programme.
- Joining: one account, links auto-affiliated across merchants; country and payout rules for
  Georgia *not checked*. Apply and ask about the API key in the first contact.

### Admitad (Mitgo) — apply now, for the region
- Roots in the CIS; serves Kazakhstan, Azerbaijan, Armenia shoppers (Lacoste KZ/AZ feeds).
- Feeds: XML, CSV or Google Merchant format, updated daily, export via API.
- Payout and Georgia rules *not checked* (Georgia expected to be fine). Matters most for the
  Armenia / region stage.

### Rakuten Advertising — apply after the domain is live
- Payouts: direct deposit to 74 countries (list not public; ask support if Georgia is in it),
  otherwise Payoneer.
- **Moving to impact.com**: from April 2026 impact.com is Rakuten's only technology platform;
  advertisers migrate "in waves over this year and next"; publishers can link an impact.com
  account and auto-join. So an impact.com acceptance later carries Rakuten's brands too.
- Feeds: product search API + merchandiser feeds (FTP) today; impact Catalogs after migration.
- Brands: lululemon, Reebok US, Uniqlo US, ECCO, Timberland, Massimo Dutti, Spier & Mackay, COS, Theory.

### impact.com — declined 2026-11-15, reapply Feb 2027
- Declined for a new site with no traffic. Reapply with wearworth.app, the channel's follower
  count and active users (Stage 3).
- Payouts: bank (local EFT where impact holds an account, otherwise international wire with a
  currency fee) or PayPal (2%, max $20); minimum $10. Georgia not named.
- Feeds: Catalogs API per joined brand (price, sale price, sizes when sent).
- Brands: GANT, Quince, Salomon US, Timberland US, Nordstrom, plus Rakuten's as they migrate.

### Partnerize — apply per brand after the domain is live
- Brand-by-brand applications (Mr Porter, Huckberry). Mr Porter offers a "fully automated data
  feed" and bespoke feeds on request.
- Payouts: international partners PayPal only, or wire to a US-based bank ($25): a **Payoneer US
  account** counts as one.

### AvantLink — apply Feb-Mar 2027
- Outdoor specialist (Salomon, Huckberry; REI and Steep & Cheap likely). Denies sites under about
  3 months old or with low traffic, so wait.
- Payouts through Tipalti: PayPal, ACH, or wire to "most international bank accounts" ($25 min).
- Feeds: datafeeds and product search for approved merchants.

### Skimlinks — low priority
- Link monetization, no product feed for small publishers. Pays non-US/EU/UK publishers by
  **PayPal only** (a problem in Georgia) and only monetizes traffic from North America, Europe
  and APAC. Useful only once readers click from those regions.

### Skip for now
- **Collective Voice** (ex-ShopStyle Collective), **LTK**, **ShopMy**: creator platforms with
  follower minimums (LTK 5,000+, ShopMy ~1,000+); no feed for an app.
- **Tradedoubler** (Georgia not in its territory list), **TradeTracker**, **Webgains**,
  **Daisycon**, **Adtraction**, **Belboon**: EU-local; a few brands each (GANT PL, Reebok EU,
  COS Nordics, Bergfreunde). Revisit if a catalogue brand is only there.
- **Strackr** (reporting across ~296 networks) and **Datafeedr** (paid feed aggregator): tools,
  not networks. Consider Strackr once on 4+ networks.

## Tracker

| Network | Status | Date | Next step | Reapply / check on |
|---|---|---|---|---|
| Awin | Accepted (3156285), 10 programmes pending | 2026-11-16 | Apply to the extra programmes above; add wearworth.app | 2026-12-15 (pending programmes) |
| CJ | Account 8127991, payment pending | 2026-11-15 | Payoneer USD account into CJ | 2026-12-01 |
| FlexOffers | Not applied | | Apply (current site is fine) | |
| Sovrn Commerce | Not applied | | Apply; ask for the price comparison API key | |
| Admitad | Not applied | | Apply | |
| Rakuten | Not applied | | Apply once wearworth.app is live | 2026-12-31 |
| Partnerize (Mr Porter, Huckberry) | Not applied | | Apply per brand once wearworth.app is live | 2027-01-15 |
| impact.com | Declined (no traffic) | 2026-11-15 | Reapply with channel and user numbers | 2027-02-01 |
| AvantLink | Not applied | | Wait for 3-month-old site with traffic | 2027-03-01 |
| Skimlinks | Not applied | | Only if PayPal works in Georgia | 2027-03-01 |

## Needs the new domain first

Rakuten, Partnerize, impact.com, AvantLink and Skimlinks all review the site itself, so apply
with wearworth.app live (with the guides and a link to the Telegram channel). Awin and CJ only
need the new domain added as a promotional property. FlexOffers, Sovrn and Admitad can start
with tbaindurashvili.github.io and switch the URL later.
