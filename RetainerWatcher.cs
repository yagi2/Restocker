using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Inventory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using Restocker.Data;

namespace Restocker;

/// <summary>
/// 「リテイナーが召喚されてマーケット出品リストが開いた瞬間」を検知し、
/// その時点のインベントリ＋出品状態を <see cref="Configuration.Snapshots"/> に書き込む。
/// 受動収集（ユーザーが普通にリテイナーを開いただけで貯まる）。
/// </summary>
public sealed unsafe class RetainerWatcher : IDisposable
{
    private readonly Configuration configuration;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IPlayerState playerState;
    private readonly IObjectTable objectTable;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    public RetainerWatcher(
        Configuration configuration,
        IAddonLifecycle addonLifecycle,
        IPlayerState playerState,
        IObjectTable objectTable,
        IDataManager dataManager,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.addonLifecycle = addonLifecycle;
        this.playerState = playerState;
        this.objectTable = objectTable;
        this.dataManager = dataManager;
        this.log = log;

        addonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "RetainerSellList", OnSellListUpdate);
    }

    public void Dispose()
    {
        addonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, "RetainerSellList", OnSellListUpdate);
    }

    /// <summary>
    /// RetainerSellList addon の PostRequestedUpdate（ゲーム側が再描画したタイミング）でスナップショットを取得。
    /// PostSetup だと中身が埋まる前に発火することがあるため、Update の方が安定。
    /// </summary>
    private void OnSellListUpdate(AddonEvent type, AddonArgs args)
    {
        try
        {
            CaptureActiveRetainerSnapshot();
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Restocker] failed to capture retainer snapshot");
        }
    }

    public void CaptureActiveRetainerSnapshot()
    {
        var retainerManager = RetainerManager.Instance();
        if (retainerManager == null || !retainerManager->IsReady) return;

        var active = retainerManager->GetActiveRetainer();
        if (active == null) return;

        var characterId = playerState.ContentId;
        if (characterId == 0) return;

        var localPlayer = objectTable.LocalPlayer;
        var snapshot = new RetainerSnapshot
        {
            CharacterContentId = characterId,
            CharacterName = localPlayer?.Name.TextValue ?? string.Empty,
            RetainerId = active->RetainerId,
            RetainerName = active->NameString,
            WorldId = localPlayer?.HomeWorld.RowId ?? 0,
            LastRefreshedUtc = DateTime.UtcNow,
        };
        snapshot.Key = RetainerSnapshot.MakeKey(snapshot.CharacterContentId, snapshot.RetainerId);

        snapshot.Listings = ReadListings();
        snapshot.Inventory = ReadInventory();

        configuration.Snapshots[snapshot.Key] = snapshot;
        configuration.Save();

        log.Debug($"[Restocker] snapshot updated: {snapshot.RetainerName} listings={snapshot.Listings.Count} inv={snapshot.Inventory.Count}");
    }

    private List<ListingEntry> ReadListings()
    {
        var result = new List<ListingEntry>();
        var im = InventoryManager.Instance();
        if (im == null) return result;

        var container = im->GetInventoryContainer(InventoryType.RetainerMarket);
        if (container == null || !container->IsLoaded) return result;

        for (var i = 0; i < container->Size; i++)
        {
            var item = container->GetInventorySlot(i);
            if (item == null || item->ItemId == 0) continue;

            // NOTE: API 15 の InventoryItem には Price フィールドが無い。
            // MVP では 0 を入れておき、リプライス実行時に SellDialog から現在価格を直接読む。
            result.Add(new ListingEntry
            {
                ItemId = item->ItemId,
                IsHQ = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0,
                Quantity = (int)item->Quantity,
                UnitPrice = 0,
                ListingIndex = item->Slot,
            });
        }

        return result;
    }

    private static readonly InventoryType[] RetainerInventoryTypes =
    [
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
        InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    private static readonly InventoryType[] CharacterBagTypes =
    [
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    private static readonly InventoryType[] SaddlebagTypes =
    [
        InventoryType.SaddleBag1, InventoryType.SaddleBag2,
    ];

    private static readonly InventoryType[] PremiumSaddlebagTypes =
    [
        InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2,
    ];

    /// <summary>
    /// キャラクター側のバッグ + サドルバッグ + プレミアムサドルを読み、
    /// <see cref="Configuration.Characters"/> に保存する。
    /// 召喚中リテイナーが居なくても呼べる（ベル開時など）。
    /// </summary>
    public void CaptureCharacterSnapshot()
    {
        var characterId = playerState.ContentId;
        if (characterId == 0) return;
        var localPlayer = objectTable.LocalPlayer;
        var name = localPlayer?.Name.TextValue ?? string.Empty;

        var snapshot = new CharacterSnapshot
        {
            CharacterContentId = characterId,
            CharacterName = name,
            Bag = ReadContainers(CharacterBagTypes),
            Saddlebag = ReadContainers(SaddlebagTypes),
            PremiumSaddlebag = ReadContainers(PremiumSaddlebagTypes),
            LastRefreshedUtc = DateTime.UtcNow,
        };
        configuration.Characters[CharacterSnapshot.MakeKey(characterId)] = snapshot;
        configuration.Save();

        log.Debug($"[Restocker] character snapshot updated: bag={snapshot.Bag.Count} saddle={snapshot.Saddlebag.Count} premium={snapshot.PremiumSaddlebag.Count}");
    }

    private List<InventoryEntry> ReadContainers(InventoryType[] types)
    {
        var result = new List<InventoryEntry>();
        var im = InventoryManager.Instance();
        if (im == null) return result;
        var itemSheet = dataManager.GetExcelSheet<Item>();

        foreach (var t in types)
        {
            var container = im->GetInventoryContainer(t);
            if (container == null || !container->IsLoaded) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null || item->ItemId == 0) continue;
                var entry = new InventoryEntry
                {
                    ItemId = item->ItemId,
                    IsHQ = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0,
                    Quantity = (int)item->Quantity,
                    ContainerId = (uint)t,
                    SlotIndex = item->Slot,
                };
                if (itemSheet.TryGetRow(item->ItemId, out var row))
                {
                    entry.MaxStackPerListing = (int)row.StackSize;
                    entry.IsListable = row.ItemSearchCategory.RowId != 0;
                }
                result.Add(entry);
            }
        }
        return result;
    }

    private List<InventoryEntry> ReadInventory()
    {
        var result = new List<InventoryEntry>();
        var im = InventoryManager.Instance();
        if (im == null) return result;

        var itemSheet = dataManager.GetExcelSheet<Item>();

        foreach (var t in RetainerInventoryTypes)
        {
            var container = im->GetInventoryContainer(t);
            if (container == null || !container->IsLoaded) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null || item->ItemId == 0) continue;

                var entry = new InventoryEntry
                {
                    ItemId = item->ItemId,
                    IsHQ = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0,
                    Quantity = (int)item->Quantity,
                    ContainerId = (uint)t,
                    SlotIndex = item->Slot,
                };

                if (itemSheet.TryGetRow(item->ItemId, out var row))
                {
                    entry.MaxStackPerListing = (int)row.StackSize;
                    // 出品可能フラグは ItemSearchCategory != 0 で近似（厳密には bind 状態など個別判定が要る）
                    entry.IsListable = row.ItemSearchCategory.RowId != 0;
                }

                result.Add(entry);
            }
        }

        return result;
    }
}
