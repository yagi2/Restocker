# Restocker

<p align="center"><img src="images/icon.png" alt="Restocker icon" width="160"></p>

A Dalamud plugin for FFXIV that turns every retainer's market board work into a **plan once, apply once** workflow:

- See every listing across every retainer in one aggregated view
- **Reprice** all listings to the live market lowest (or lowest minus *N* gil) — Restocker drives the in-game market lookup itself, no manual searching
- **New listings** with automatic stack-splitting (e.g. 990 ÷ 99 → ten stacks) and per-row routing to whichever retainer you choose, including direct-list from your character bag or Chocobo Saddlebag
- Plan-then-apply UX: every change is staged in a list at the bottom of the tab, you review/delete what you don't want, then a single **Apply** button commits all of them
- A live progress dialog shows what the executor is doing on each step (selecting retainer, opening sell list, fetching market data, listing, …) so long runs don't look frozen
- Six-language UI: English / 日本語 / Deutsch / Français / 简体中文 / 한국어

## Install

Add the personal Dalamud index to Dalamud:

```
https://raw.githubusercontent.com/yagi2/dalamud-plugins/main/repo.json
```

Then install **Restocker** from the plugin list. After install, type `/restocker` or open the retainer bell — the main window shows up automatically when the bell opens (configurable).

## What you can do with it

### Reprice tab — drop every listing to the lowest

For each retainer:

1. Set the undercut amount in the inline `- N g` input (per-session, default 1).
2. Click **All items: lowest -N g** *or* **All items: lowest (exact)**.
3. Restocker walks the listings, for each one opens *Adjust price → Compare prices*, reads the actual market data via Dalamud's `IMarketBoard.OfferingsReceived`, and fills the row's new price.
4. Skim the table, then either click **+** on rows you want to commit, or **Add all to plan** to queue the whole retainer.
5. The bottom panel shows queued plans across all retainers (item / target / qty / old → new). Use **x** on a row to drop it, **Clear all** to wipe.
6. Press **Apply**. Confirmed prices are written via `InventoryManager.SetRetainerMarketPrice` directly — no per-listing dialogs to click through.

Per row you can also type a price manually, or click **fill** to copy that row's new price to every other listing on the same retainer.

The fetch step:
- Skips your own retainers' listings when picking the lowest (no self-undercut).
- Deduplicates per `(item, HQ)` within one run, so a retainer with five stacks of the same item only hits the market board once.
- Tolerates the occasional listing where the dialog doesn't open, logs a warning, and moves on instead of aborting the whole run.

### New listing tab — split, route, plan, apply

For each retainer / character bag / Chocobo Saddlebag section:

- Set **price**, **qty per listing × number of slots**, **target retainer** per row.
- The **M** button next to qty fills it with `min(MaxStack, owned)`.
- Click **+ Add** to push a `PendingPlan` onto the queue at the bottom of the tab.
- Repeat for as many items / target retainers as you want — the same item from your saddlebag can be split between retainer A and retainer B in one Apply run.
- Press **Apply**. Restocker:
  - For saddlebag sources, **bulk-pre-stages** the required quantity from saddle to character bag in one phase, lets the server settle, then enters the listing phase.
  - For each new listing, calls `InventoryManager.MoveToRetainerMarket` to commit a `qty × price` listing in a single API call, then waits for the server to actually land it before moving on.

No retainer↔retainer item moves. No deposit step. The character bag sources its own items and lists them directly under the chosen retainer.

### Progress dialog

While the executor is running, an auto-shown floating window surfaces:

- Mode (Refresh all / Apply actions)
- Current step localised (`Pre-staging saddle items to bag…`, `Waiting for sell dialog…`, etc.)
- Two progress bars: retainers and individual actions
- Cancel button

This is the "is it stuck or just thinking" window — phases like saddle pre-staging or per-listing server confirmation can take seconds.

### Settings

- **Auto-open on bell** — the main window appears whenever `RetainerList` opens, RepeatBuy-style.
- **Language** — Auto (follow client) or explicit override across the six supported locales.

## How market data gets in

Restocker subscribes to **Dalamud's `IMarketBoard.OfferingsReceived`** event. Every time a market board search lands — whether you ran it from Restocker's reprice flow or browsed the market manually — listings are filtered (skip your own retainers, take the cheapest per HQ/NQ) and stored in an in-memory cache.

The reprice flow drives those searches itself by:

1. Clicking the listing row in `RetainerSellList`,
2. Picking *Adjust price* in the context menu,
3. Pressing the *Compare prices* button on the price dialog.

It also resolves which item is actually displayed in the dialog (not what the snapshot says — those can drift if you sort the sell list) by reading the dialog's item name and looking it up in the Lumina `Item` sheet.

## Co-existence with other plugins

- **AutoRetainer** — both plugins drive the retainer summon flow. If both are loaded you get a one-time toast warning; pause one while the other runs. Restocker doesn't itself touch venture / quick exploration.
- **PriceBuddy / Marketbuddy** — these listen on `OfferingsReceived` too, and most don't conflict; but if PriceBuddy auto-clicks *Compare prices* the moment a `RetainerSell` opens, that's harmless to Restocker since Restocker would have clicked it anyway. Disable their auto-click feature only if you see unwanted churn.

## Build

```
dotnet build -c Release
```

Output: `bin/Release/Restocker/Restocker.dll` plus `bin/Release/Restocker/latest.zip` (uploaded by the GitHub Actions release workflow).

The `<PlatformTarget>x64</PlatformTarget>` + `<AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>` block in `Restocker.csproj` keeps the SDK 15 default `bin/x64/Release/` from breaking Dev Plugin Locations and the GHA workflow.

## Test

```
dotnet test
```

`tests/Restocker.Tests/` is a pure-logic xUnit project — no Dalamud / Lumina references at runtime, so it can be run on plain .NET. Coverage:

- `Planner` — stack splitting, listing-cap behaviour, qty clamping, free-slot capping, plan / overflow accounting
- `LowestPriceResolver` — outlier rejection, single / empty input, gap-ratio threshold knobs
- `AddonText` predicates — six-language pattern matching for the SelectString and ContextMenu entries
- `RetainerSnapshot.MakeKey` / `CharacterSnapshot.MakeKey`

Game-side wiring (executor state machine, addon callbacks) is verified in-game, not by tests.

## Related projects

- **[GilDelta](https://github.com/yagi2/GilDelta)** — same author's read-only gil tracker. Same SDK / project shape; useful as a reference for plugin scaffolding.
- **[RepeatBuy](https://github.com/yagi2/RepeatBuy)** — earlier author plugin, smaller surface, also references the bell-aware UI behaviour.
- **[yagi2/dalamud-plugins](https://github.com/yagi2/dalamud-plugins)** — the personal Dalamud plugin index that hosts Restocker, GilDelta, RepeatBuy.

## License

MIT.
