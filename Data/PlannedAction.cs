using System;

namespace Restocker.Data;

/// <summary>実行計画 1 ステップ。Executor がこれを順次走らせる。</summary>
[Serializable]
public sealed class PlannedAction
{
    public PlannedActionKind Kind { get; set; }

    /// <summary>対象リテイナー（CharacterContentId + RetainerId）。<see cref="RetainerSnapshot.MakeKey"/> と同形式。</summary>
    public string RetainerKey { get; set; } = string.Empty;

    public uint ItemId { get; set; }
    public bool IsHQ { get; set; }

    /// <summary>新規出品の場合の出品個数（端数または最大スタック）。</summary>
    public int Quantity { get; set; }

    /// <summary>提示価格（gil）。</summary>
    public long UnitPrice { get; set; }

    /// <summary>
    /// リプライス時の対象出品スロット（0..19）。新規出品では未使用。
    /// </summary>
    public int ListingIndex { get; set; }
}

public enum PlannedActionKind
{
    /// <summary>既存出品 ListingIndex の価格を UnitPrice に書き換える。</summary>
    Reprice,

    /// <summary>インベントリの ItemId(HQ) を Quantity 個・UnitPrice で新規出品する。</summary>
    NewListing,
}
