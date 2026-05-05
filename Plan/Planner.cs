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
        string? sourceKeyOverride = null)
    {
        var result = new List<PlannedAction>();
        if (maxStackPerListing <= 0) return result;

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

            // フル本数 (max stack) を埋める
            while (remaining >= maxStackPerListing && slotsLeft > 0)
            {
                result.Add(new PlannedAction
                {
                    Kind = PlannedActionKind.NewListing,
                    RetainerKey = snapshot.Key,
                    SourceKey = sourceKeyOverride ?? snapshot.Key,
                    ItemId = itemId,
                    IsHQ = isHQ,
                    Quantity = maxStackPerListing,
                    UnitPrice = unitPrice,
                });
                remaining -= maxStackPerListing;
                slotsLeft--;
            }

            // 端数 1 本（スロットが余っているなら）
            if (remaining > 0 && slotsLeft > 0)
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
            // それ以外（スロット枯渇）は呼び出し側で「N 個出品できなかった」と警告する責務。
        }

        return result;
    }

    /// <summary>FFXIV のリテイナー 1 人あたりの最大出品スロット数。</summary>
    public const int MaxListingSlots = 20;

    /// <summary>
    /// キャラ所持品から target リテイナーへ「預け入れなし」で新規出品する計画。
    /// 在庫ソースは <paramref name="character"/>.Bag、出品先のスロット制約は
    /// <paramref name="targetRetainer"/>.Listings の空き数のみで決まる。
    /// </summary>
    public static List<PlannedAction> PlanCharacterListings(
        Restocker.Data.CharacterSnapshot character,
        RetainerSnapshot targetRetainer,
        uint itemId,
        bool isHQ,
        long unitPrice,
        int maxStackPerListing)
    {
        var result = new List<PlannedAction>();
        if (maxStackPerListing <= 0) return result;

        var owned = character.Bag
            .Where(e => e.ItemId == itemId && e.IsHQ == isHQ)
            .Sum(e => e.Quantity);
        if (owned <= 0) return result;

        var freeSlots = MaxListingSlots - targetRetainer.Listings.Count;
        if (freeSlots <= 0) return result;

        var sourceKey = Restocker.Data.CharacterSnapshot.MakeKey(character.CharacterContentId);
        var remaining = owned;
        var slotsLeft = freeSlots;

        while (remaining >= maxStackPerListing && slotsLeft > 0)
        {
            result.Add(new PlannedAction
            {
                Kind = PlannedActionKind.NewListing,
                RetainerKey = targetRetainer.Key,
                SourceKey = sourceKey,
                ItemId = itemId,
                IsHQ = isHQ,
                Quantity = maxStackPerListing,
                UnitPrice = unitPrice,
            });
            remaining -= maxStackPerListing;
            slotsLeft--;
        }
        if (remaining > 0 && slotsLeft > 0)
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
