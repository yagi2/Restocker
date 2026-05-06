# CLAUDE.md

Notes for future agents working on Restocker.

## Project at a glance

- **Type:** Dalamud plugin (FFXIV), API 15, .NET via `Dalamud.NET.Sdk/15.0.0`.
- **Goal:** cross-retainer market workbench. Bulk reprice every listing to live market lowest (-N gil). Bulk new listings with stack splitting + per-row target retainer + char bag / Chocobo Saddlebag sources. Plan-then-apply UX everywhere.
- **Read/write surface.** Unlike GilDelta this plugin *does* drive game state: it summons retainers, opens the sell list, clicks listings, fires ComparePrices, calls `SetRetainerMarketPrice`, calls `MoveToRetainerMarket`, calls `MoveItemSlot`. The destructive action surface is gated by an explicit `Apply` button + a confirm dialog; do not bypass either.

## Code map

```
Restocker/
├── Restocker.csproj                   # Dalamud.NET.Sdk/15.0.0; pins output to bin/Release/, excludes tests/**
├── Restocker.json                     # plugin manifest shown in Dalamud
├── Restocker.sln                      # main project + tests/Restocker.Tests
├── Plugin.cs                          # IDalamudPlugin entry, DI, command registration
├── Configuration.cs                   # IPluginConfiguration: Snapshots, Characters, AutoOpenOnBell, Language
├── RetainerWatcher.cs                 # AddonLifecycle PostRequestedUpdate on RetainerSellList → snapshot
├── BellWatcher.cs                     # auto-open main window when RetainerList opens
├── AutoRetainerDetector.cs            # one-time toast warning if AutoRetainer also loaded
├── Common/
│   ├── AddonHelper.cs                 # IsOpen / GetVisible
│   ├── AddonText.cs                   # 6-language predicates: IsHaveRetainerSellItemsEntry / IsQuitEntry / IsAdjustPriceEntry / IsPutUpForSaleEntry
│   ├── ButtonClick.cs                 # ECommons-style ReceiveEvent click for AtkComponentButton
│   ├── Callback.cs                    # min port of ECommons.Automation.Callback (params object[] → AtkValue[])
│   ├── ContextMenuHelper.cs           # ContextMenu entry enumeration / click via SeString reader
│   └── SelectStringHelper.cs          # SelectString entry enumeration / click via predicate
├── Data/
│   ├── PlannedAction.cs               # NewListing / Reprice / FetchMarketPrice
│   ├── RetainerSnapshot.cs            # MakeKey "{contentId:X}.{retainerId:X}", listings + inventory
│   ├── CharacterSnapshot.cs           # MakeKey "char.{contentId:X}", Bag / Saddlebag / PremiumSaddlebag
│   ├── ListingEntry.cs                # ItemId/IsHQ/Quantity/UnitPrice/ListingIndex
│   └── InventoryEntry.cs              # ItemId/IsHQ/Quantity/MaxStackPerListing/IsListable
├── Execution/
│   ├── ExecutionState.cs              # state machine values + ExecutionMode
│   ├── RetainerVisitJob.cs            # one retainer + its actions
│   └── Executor.cs                    # giant switch driving the state machine
├── Plan/
│   └── Planner.cs                     # PlanNewListings / PlanFromInventoryList / Overflow (pure logic, tested)
├── Market/
│   ├── MarketCache.cs                 # in-memory (itemId, isHq) → list<long> (single-entry, single retainer's view)
│   ├── MarketWatcher.cs               # IMarketBoard.OfferingsReceived listener; PennyPincher logic + 'expectedItem' filter
│   └── LowestPriceResolver.cs         # outlier-aware lowest picker (pure logic, tested)
├── Localization/
│   └── Strings.cs                     # 6-arg T() helper across EN/JA/DE/FR/ZH/KO
├── Windows/
│   ├── MainWindow.cs                  # tab bar (Reprice / New listing / Settings)
│   ├── ConfirmDialog.cs               # yes/no modal between plan and Apply
│   ├── ProgressWindow.cs              # auto-shown while Executor.IsRunning, surfaces state + progress bars
│   ├── RepriceTab.cs                  # plan-then-apply for existing listings; auto-fetch loop; fill button
│   └── ListTab.cs                     # plan-then-apply for new listings; per-row qty x slots + target retainer
├── images/
│   └── icon.png                       # plugin icon
├── tests/Restocker.Tests/             # xUnit, pure-logic only (no Dalamud refs)
└── .github/workflows/                 # release artifact upload
```

## Game-side wiring

These are the things the executor talks to. If an SDK bump breaks one, re-derive from `%AppData%\XIVLauncher\addon\Hooks\dev\FFXIVClientStructs.dll`.

### Snapshots (read)
- **Listings:** `InventoryManager.Instance()->GetInventoryContainer(InventoryType.RetainerMarket)`. Walk slots 0..19 — `ItemId`, `Flags & HighQuality`, `Quantity`, `Slot` are the snapshot fields. `GetRetainerMarketPrice((short)i)` reads the asking price (server-side slot index, NOT display row).
- **Retainer inventory:** `InventoryManager` `RetainerPage1..7` containers.
- **Char bag / Saddlebag:** `Inventory1..4`, `SaddleBag1/2`, `PremiumSaddleBag1/2`.

### Sell list flow (write)
- **Open `RetainerSellList`:** SelectString entry `IsHaveRetainerSellItemsEntry` → `Callback.Fire(addon, true, idx)`.
- **Click a listing row:** `Callback.Fire(retainerSellList, true, 0, slot, 1)` — **the third arg `1` is required**. Without it the addon silently drops the click. The `slot` here is the *display row index*, NOT the server slot index — they only line up when the user hasn't touched sort. Snapshot's `ListingIndex` is server slot, so when fetching by row we resolve `(itemId, isHq)` from the dialog text instead of trusting the snapshot.
- **ContextMenu adjust price:** entry `IsAdjustPriceEntry` → `ContextMenuHelper.ClickEntry`.
- **ComparePrices:** `Callback.Fire(retainerSell, true, 4)`.
- **Cancel a dialog:** `Callback.Fire(addon, true, -1)`.
- **Confirm new listing:** `ButtonClick.Click(sell->Confirm, addon)` (FireCallback alone misses sometimes).
- **Reprice direct:** `InventoryManager.SetRetainerMarketPrice((short)slot, (uint)price)` — single API, no dialog, server slot. Used after the user confirms the plan.
- **New listing direct:** `InventoryManager.MoveToRetainerMarket(srcType, srcSlot, RetainerMarket, dstSlot, qty, price)` — single API, no dialog. Saddlebag sources need to be staged into char bag first via `MoveItemSlot(saddleType, slot, bagType, slot, **true**)`. The `true` (the merge flag, not swap) is required; `false` produces a server-rejected pending state.

### Market data
- `IMarketBoard.OfferingsReceived` event delivers the search results. Skip your own retainers via `RetainerManager.Instance()->GetRetainerBySortedIndex(i)->RetainerId` membership. PennyPincher uses the same approach.
- The server occasionally redelivers a previous request's result late, after we've already moved on to the next listing. `MarketWatcher.expectedItemId` filters those out: Executor sets it before each ComparePrices fire, clears it on completion.

## Dalamud SDK 15 gotchas

These are the deviations that cost time:

1. **Default build output is `bin/x64/Release/`.** Pin back to `bin/Release/` via the csproj `PropertyGroup`:
   ```xml
   <PlatformTarget>x64</PlatformTarget>
   <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
   <AppendRuntimeIdentifierToOutputPath>false</AppendRuntimeIdentifierToOutputPath>
   <OutputPath>bin\$(Configuration)\</OutputPath>
   ```
2. **DalamudPackager 15 cleans up loose files after zipping** the staged folder. `ExtractStagedZipForDevPlugin` re-extracts `bin/Release/Restocker/latest.zip` so a real DLL stays on disk for Dev Plugin Locations. Wire it with `AfterTargets="Build"` AND `DependsOnTargets="DefaultDalamudPackagerDebug;DefaultDalamudPackagerRelease"` — `AfterTargets` alone doesn't enforce ordering.
3. **`AtkUnitBasePtr` wrapper** vs raw pointer. `Plugin.GameGui.GetAddonByName(name)` returns `AtkUnitBasePtr` (struct with `Address` field). Cast `(AtkUnitBase*)addon.Address` to dereference.
4. **`AtkValueType` enum, not the old per-int magic numbers.** `ContextMenuHelper.ExtractText` had to switch on `AtkValueType.String / String8 / ManagedString / Managed` and read via `Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated` because raw `Marshal.PtrToStringUTF8` chokes on payloads.
5. **`tests/**` exclude.** Without `<Compile Remove="tests\**\*.cs" />` the test project's xUnit `Using` directives leak into the main plugin compile.
6. **Test-mode Lumina/Dalamud absence.** `AddonText.SafeRowText` early-returns when `Plugin.DataManager is null` and wraps the actual sheet read in an inner method so the JIT doesn't need to load `Lumina.Excel.dll` at all when running unit tests on plain .NET.

## Listing slot click was the long one

- `Callback.Fire(addon, true, 0, slot)` ← only 2 args, what we tried first. Addon silently ignores it.
- `Callback.Fire(addon, true, 0, (uint)slot)` ← UInt instead of Int. Same — silent ignore.
- `Callback.Fire(addon, true, 0, slot, 1)` ← three args, the trailing `1` is the click flag. **This is what works.**

If it ever stops working again, check whether the third arg's meaning has changed (most likely candidates: a flag bitmask or a button index).

## Snapshot vs display row

The `RetainerSellList` addon takes a row click as a *display row* index, not a server slot index. Snapshot writes server slot. They only match when nothing has been sorted in-game. Don't try to fetch by `ListingIndex` from the snapshot — fetch by walking display rows 0..N-1 and resolve which item the dialog actually shows by reading the `RetainerSell` dialog's `ItemName` and reverse-lookup against Lumina's `Item` sheet (cached as `itemNameToIdCache` in `Executor.ResolveItemFromDialogName`).

The reverse lookup walks longest-name-first to avoid a 4-character item-name accidentally matching as a substring inside a 12-character dialog string. The HQ check happens before stripping, by `IndexOf` on the PUA HQ glyph (`U+E03C`). Strip everything in `U+E000..U+F8FF` plus control chars before doing the dictionary lookup.

## Server pacing

- After closing `ItemSearchResult` + `RetainerSell`, sit on `FetchAwaitingSellListAfter` for **800ms** before clicking the next listing row. The server occasionally trickles in a previous listing's offerings during that window; the wait + the `expectedItemId` filter together keep stale offerings out of cache.
- After the saddle pre-stage phase, wait an additional **3s** (`SaddleSettleAfterStage`) before entering `PerformingAction`. The server needs that window to finish committing the cross-container moves; firing `MoveToRetainerMarket` against a freshly-staged slot before the server has acknowledged the move makes the listing silently fail.

## Don't-aborts

The executor was originally written to `Stop()` on a few transient conditions (ContextMenu fails to open, dialog mismatches the snapshot, RetainerSell never opens). Each of those killed entire reprice runs whenever a single row glitched. The current rule: if it's a per-row issue, **log + skip the row + bump CompletedActions**, don't `Stop()` the whole executor. The only `Stop()` left in the fetch path is structural ("no RetainerSellList visible at all", "no InventoryManager"). Preserve that distinction when adding new branches.

## Test

```
dotnet test
```

xUnit project at `tests/Restocker.Tests/`. Pure-logic only — no Dalamud / Lumina at runtime. Game-side wiring is verified in-game, not by tests. Current count: 68 tests covering Planner, LowestPriceResolver, AddonText predicates, RetainerSnapshot.MakeKey / CharacterSnapshot.MakeKey, plus a smoke.

If you add new logic to `Planner` or `LowestPriceResolver`, add a test before merging. Don't try to test the executor or the Windows code there — they're full of Dalamud / Lumina / unsafe pointers; tested in-game only.

## Build

```
dotnet build -c Release
```

- Output: `bin/Release/Restocker/Restocker.dll` (loadable) + `bin/Release/Restocker/latest.zip` (distribution).
- `NETSDK1057` preview-SDK info note is harmless.
- `ExtractStagedZipForDevPlugin` re-extracts the zip after DalamudPackager cleans up so a DLL stays on disk for Dev Plugin Locations.

## Distribution

- Index repo: [`yagi2/dalamud-plugins`](https://github.com/yagi2/dalamud-plugins). Adding the index URL to Dalamud once gets the user every yagi2 plugin in their installer. New release = bump `AssemblyVersion` / `Changelog` in that repo's `repo.json`.
- Release artifacts: GHA workflow on `release: published` builds on `windows-latest`, downloads Dalamud reference assemblies from `https://goatcorp.github.io/dalamud-distrib/latest.zip`, uploads `bin/Release/Restocker/latest.zip` via `gh release upload --clobber`.

## Reference plugins (cloned at sibling paths)

- `..\GilDelta` — yagi2's read-only gil tracker. Same SDK / project shape; **most useful as csproj / test layout reference.** Has `<PlatformTarget>x64</PlatformTarget>` + the `ExtractStagedZipForDevPlugin` target — copy from there if those need to be regenerated.
- `..\PennyPincher` — undercut helper. The `MarketBoard.OfferingsReceived` reading + `IsOwnRetainer` check pattern came from here.
- `..\Marketbuddy` — dialog-button sniffing reference (`Commons.SendClick(addon, EventType, eventId, node)`). Confirmed `Confirm` button = `EventType.CHANGE, 21`, `ComparePrices` = `EventType.CHANGE, 4`.
- `..\Dagobert` — auto-undercut driver. The 3-arg `Callback.Fire(addon, true, 0, slot, 1)` listing-row click came from here. Also the staged ContextMenu → adjust price flow.
- `..\AutoRetainer` — `OpenInventoryContextDetour` Hook signature reference (only used to confirm `OpenForItemSlot` arg shape).
- `..\ECommons` — submodule shell of NightmareXIV's ECommons. The actual code lives outside; primarily kept around so we can grep `AddonMaster*` for callback signatures when adding new addon interactions.
- `..\RepeatBuy` — earlier author plugin, smaller surface, useful for the "auto-show window when bell opens" behaviour reference.

## What not to do

- Don't fetch by `snap.Listings[i].ListingIndex` and click that as a row — it's the server slot, not the display row. Walk rows 0..N-1 and resolve from the dialog.
- Don't make `MoveItemSlot(..., false)`. Saddle-to-bag staging needs `true`. `false` is a server-rejected pending swap.
- Don't `Stop()` the executor on per-row failures. Skip + log + advance.
- Don't trust the snapshot's `ItemId` after a row click — read the dialog's `ItemName` and reverse-resolve.
- Don't drop the 800ms cooldown between listings or the 3s saddle settle — they exist because the server sometimes re-delivers prior request offerings into a fresh fetch.
- Don't ship a PNG icon without sRGB + gAMA + pHYs chunks. The Dalamud installer leaves the icon slot blank otherwise. (Recipe lives in `..\GilDelta` git history.)
- Don't write to game state outside of an `Apply`-button-confirmed path. The plan-then-apply pattern is the contract; bypassing it removes user undo.
- Don't expand `AddonText` predicates speculatively. Add a language-specific phrase only when a `/xllog` dump shows the predicate failing on a real menu in that locale.
