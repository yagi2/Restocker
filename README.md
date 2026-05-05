# Restocker

A Dalamud plugin for FFXIV that gives you a **cross-retainer market workbench**:

- See every listing across every retainer in **one aggregated table**
- Bulk reprice in one place — edit by item or drill down to per-listing
- Bulk new listing with **auto stack-splitting** — e.g. 990 of an item with stack 99 becomes ten listings of 99 automatically, distributed across the retainers that actually hold the items (no inter-retainer item movement)
- Snapshots are populated passively whenever you summon a retainer and open the sell list — no extra steps
- Six-language UI: English / 日本語 / Deutsch / Français / 简体中文 / 한국어

## Status

`0.1.0` — **read-only side complete**. The execution layer (auto-clicking through the sell dialogs) is scaffolded but the in-game callback wiring is still being filled in. Apply buttons are intentionally disabled until that lands. You can already use Restocker as a planning view today: see what's out there, see what a bulk-list plan would look like, then act on it manually.

## Install

Add this plugin repository to Dalamud:

```
https://raw.githubusercontent.com/yagi2/dalamud-plugins/main/repo.json
```

Then install **Restocker** from the plugin list.

> The plugin will only ship to the public Dalamud index after the execution layer is verified end-to-end. For now it's available via the personal repo above.

## How it works

### Passive snapshot collection

Every time you summon a retainer and the sell list (`RetainerSellList`) updates, Restocker captures:

- the 20 market listings (item, HQ, qty)
- the inventory pages 1–7 (item, HQ, qty)

…and saves them to your plugin config keyed by `(character, retainer)`. The next time you open the bell — even without summoning anyone — the cross-retainer view shows you everything you saw last time.

> The "current asking price" of each listing is not exposed in `InventoryItem` on Dalamud API 15, so the **Reprice** tab shows `unknown` for prices on retainers you haven't visited recently. Re-summoning the retainer and opening the sell list will refresh.

### The Reprice tab

One row per `(item, HQ)` pair across all retainers, showing total qty and listing count. Type a new price → that price applies to every listing of that item when you press *Apply*. Click `+` next to a row to drill into the per-listing rows and override individual prices.

### The List tab

One row per `(item, HQ)` pair found in retainer inventories. Type a price → the planner instantly shows you how the listings would split across retainers, e.g. `11 listings (R1=6, R2=5)`. Items in retainers with no remaining listing slots are flagged as overflow (orange).

### Settings

- Auto-show the window when the retainer bell is open (RepeatBuy-style)
- Language (auto / explicit per supported language)

### AutoRetainer co-existence

Restocker drives retainer summoning itself; if you also have AutoRetainer loaded, both plugins will fight for the bell. You'll get a one-time toast warning at startup. Pause one of them while the other runs.

### Outlier-aware "match lowest -1"

When/if you trigger the "match lowest -1 gil" rule, Restocker fetches the in-game market board for each unique item (deduplicated within one operation) and picks the lowest **after excluding obvious shill listings** — typically a single rock-bottom listing meant to bait you into matching. The default heuristic excludes up to 2 leading entries when the next price is at least 1.5× higher.

## Build

```
dotnet build -c Release
```

Output: `bin/Release/Restocker.dll` and `bin/Release/Restocker/latest.zip` (the latter is what GitHub Actions uploads to releases).

## License

MIT.
