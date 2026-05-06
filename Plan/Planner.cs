using System;
using System.Collections.Generic;
using System.Linq;
using Restocker.Data;

namespace Restocker.Plan;

/// <summary>
/// 「全リテイナー横断・移動なし」モデルでの新規分割出品プランナー。純ロジック、ゲーム依存なし。
///
/// 入力: あるアイテム (ItemId, IsHQ) と、各リテイナーの保有数・空き出品スロット数、
/// 提示価格。
/// 出力: <see cref="PlannedAction"/> のリスト（NewListing のみ）。各リテイナー内で
/// 「最大スタック × N + 端数 1 件」の構成。リテイナー間でアイテムを移動しない。
/// 出品スロットが足りなければそのリテイナーで詰めるだけ詰めて残りはスキップ（呼び出し側で警告表示）。
/// </summary>
public static class Planner
{
    /// <summary>
    /// 単一アイテム（=ItemId+HQ 1 種）について、保有しているリテイナー全員ぶんの新規出品計画を作る。
    /// </summary>
    /// <param name="snapshots">候補リテイナーのスナップショット。実行時の最新状態。</param>
    /// <param name="itemId">対象アイテム ID。</param>
    /// <param name="isHQ">HQ フラグ（NQ/HQ は別物として扱う）。</param>
    /// <param name="unitPrice">提示価格（gil）。</param>
    /// <param name="maxStackPerListing">1 出品あたりの最大個数（=スタックサイズ、99/999 等）。</param>
    /// <returns>
    /// 各リテイナー × 出品アクションのフラットリスト。実行は List 順に行なう想定。
    /// </returns>
    public static List<PlannedAction> PlanNewListings(
        IEnumerable<RetainerSnapshot> snapshots,
        uint itemId,
        bool isHQ,
        long unitPrice,
        int maxStackPerListing,
        string? sourceKeyOverride = null,
        int? listingsCap = null,
        int? perListingQty = null)
    {
        var result = new List<PlannedAction>();
        if (maxStackPerListing <= 0) return result;
        // 明示的に 0 を渡された場合は 0 件とする (UI で「個数 0 / 枠数 0」と
        // 入っている行は意図的に対象外、というのが直感的なため)
        if (perListingQty.HasValue && perListingQty.Value <= 0) return result;
        if (listingsCap.HasValue && listingsCap.Value <= 0) return result;
        var qtyPer = perListingQty.HasValue
            ? Math.Min(perListingQty.Value, maxStackPerListing)
            : maxStackPerListing;

        foreach (var snapshot in snapshots)
        {
            var owned = snapshot.Inventory
                .Where(e => e.ItemId == itemId && e.IsHQ == isHQ)
                .Sum(e => e.Quantity);
            if (owned <= 0) continue;

            var freeSlots = MaxListingSlots - snapshot.Listings.Count;
            if (freeSlots <= 0) continue;

            var remaining = owned;
            var slotsLeft = freeSlots;
            if (listingsCap.HasValue) slotsLeft = Math.Min(slotsLeft, Math.Max(0, listingsCap.Value));

            // フル本数 (qtyPer 個) を埋める
            while (remaining >= qtyPer && slotsLeft > 0)
            {
                result.Add(new PlannedAction
                {
                    Kind = PlannedActionKind.NewListing,
                    RetainerKey = snapshot.Key,
                    SourceKey = sourceKeyOverride ?? snapshot.Key,
                    ItemId = itemId,
                    IsHQ = isHQ,
                    Quantity = qtyPer,
                    UnitPrice = unitPrice,
                });
                remaining -= qtyPer;
                slotsLeft--;
            }

            // 端数 1 本: listingsCap で「**ちょうど N 枠**」と指定された場合は端数を出さない方が
            // ユーザー意図に近い (1 枠だけ qtyPer 未満が混じると驚くため)。
            // listingsCap 未指定 (= 全枠埋める) なら端数も出す。
            if (!listingsCap.HasValue && remaining > 0 && slotsLeft > 0)
            {
                result.Add(new PlannedAction
                {
                    Kind = PlannedActionKind.NewListing,
                    RetainerKey = snapshot.Key,
                    SourceKey = sourceKeyOverride ?? snapshot.Key,
                    ItemId = itemId,
                    IsHQ = isHQ,
                    Quantity = remaining,
                    UnitPrice = unitPrice,
                });
            }
        }

        return result;
    }

    /// <summary>FFXIV のリテイナー 1 人あたりの最大出品スロット数。</summary>
    public const int MaxListingSlots = 20;

    /// <summary>
    /// 任意のインベントリリスト（キャラバッグ／サドル等）を在庫源として、
    /// target リテイナーへ「預け入れなし」直接出品する計画を作る。
    /// </summary>
    public static List<PlannedAction> PlanFromInventoryList(
        IEnumerable<Restocker.Data.InventoryEntry> inventory,
        string sourceKey,
        RetainerSnapshot targetRetainer,
        uint itemId,
        bool isHQ,
        long unitPrice,
        int maxStackPerListing,
        int? listingsCap = null,
        int? perListingQty = null)
    {
        var result = new List<PlannedAction>();
        if (maxStackPerListing <= 0) return result;
        if (perListingQty.HasValue && perListingQty.Value <= 0) return result;
        if (listingsCap.HasValue && listingsCap.Value <= 0) return result;
        var qtyPer = perListingQty.HasValue
            ? Math.Min(perListingQty.Value, maxStackPerListing)
            : maxStackPerListing;

        var owned = inventory.Where(e => e.ItemId == itemId && e.IsHQ == isHQ).Sum(e => e.Quantity);
        if (owned <= 0) return result;

        var freeSlots = MaxListingSlots - targetRetainer.Listings.Count;
        if (freeSlots <= 0) return result;

        var remaining = owned;
        var slotsLeft = freeSlots;
        if (listingsCap.HasValue) slotsLeft = Math.Min(slotsLeft, Math.Max(0, listingsCap.Value));

        while (remaining >= qtyPer && slotsLeft > 0)
        {
            result.Add(new PlannedAction
            {
                Kind = PlannedActionKind.NewListing,
                RetainerKey = targetRetainer.Key,
                SourceKey = sourceKey,
                ItemId = itemId,
                IsHQ = isHQ,
                Quantity = qtyPer,
                UnitPrice = unitPrice,
            });
            remaining -= qtyPer;
            slotsLeft--;
        }
        if (!listingsCap.HasValue && remaining > 0 && slotsLeft > 0)
        {
            result.Add(new PlannedAction
            {
                Kind = PlannedActionKind.NewListing,
                RetainerKey = targetRetainer.Key,
                SourceKey = sourceKey,
                ItemId = itemId,
                IsHQ = isHQ,
                Quantity = remaining,
                UnitPrice = unitPrice,
            });
        }
        return result;
    }

    /// <summary>後方互換: <see cref="PlanFromInventoryList"/> のキャラバッグ版エイリアス。</summary>
    public static List<PlannedAction> PlanCharacterListings(
        Restocker.Data.CharacterSnapshot character,
        RetainerSnapshot targetRetainer,
        uint itemId, bool isHQ, long unitPrice, int maxStackPerListing)
        => PlanFromInventoryList(character.Bag,
            Restocker.Data.CharacterSnapshot.MakeKey(character.CharacterContentId),
            targetRetainer, itemId, isHQ, unitPrice, maxStackPerListing);

    /// <summary>
    /// 計画と実際の保有量の差分から「あふれて出品できなかった個数」を計算する。
    /// UI のスロット枯渇警告に使う。
    /// </summary>
    public static int Overflow(
        IEnumerable<RetainerSnapshot> snapshots,
        IEnumerable<PlannedAction> actions,
        uint itemId,
        bool isHQ)
    {
        var planned = actions
            .Where(a => a.Kind == PlannedActionKind.NewListing && a.ItemId == itemId && a.IsHQ == isHQ)
            .Sum(a => a.Quantity);
        var owned = snapshots
            .SelectMany(s => s.Inventory)
            .Where(e => e.ItemId == itemId && e.IsHQ == isHQ)
            .Sum(e => e.Quantity);
        return owned - planned;
    }
}
