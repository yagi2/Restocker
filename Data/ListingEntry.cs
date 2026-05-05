using System;

namespace Restocker.Data;

/// <summary>
/// リテイナーマーケットの 1 出品（最大 20 件のうちの 1 つ）。
/// </summary>
[Serializable]
public sealed class ListingEntry
{
    public uint ItemId { get; set; }
    public bool IsHQ { get; set; }
    public int Quantity { get; set; }
    public long UnitPrice { get; set; }

    /// <summary>
    /// リテイナー出品リスト内のスロット位置（0..19）。書き戻し時にゲーム側のリストインデックスへ使う。
    /// </summary>
    public int ListingIndex { get; set; }
}
