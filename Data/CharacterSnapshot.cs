using System;
using System.Collections.Generic;

namespace Restocker.Data;

/// <summary>
/// プレイヤーキャラ側の所持品スナップショット (バッグ + サドルバッグ + プレミアムサドル)。
/// リテイナーに預ける前段階のアイテムを把握するため。
/// </summary>
[Serializable]
public sealed class CharacterSnapshot
{
    public ulong CharacterContentId { get; set; }
    public string CharacterName { get; set; } = string.Empty;

    /// <summary>キャラのメインバッグ (Inventory1〜4)。</summary>
    public List<InventoryEntry> Bag { get; set; } = new();

    /// <summary>サドルバッグ (Saddlebag1, Saddlebag2)。アクセスにはチョコボサドル等の条件あり。</summary>
    public List<InventoryEntry> Saddlebag { get; set; } = new();

    /// <summary>プレミアムサドルバッグ (PremiumSaddlebag1, PremiumSaddlebag2)。月額課金者用。</summary>
    public List<InventoryEntry> PremiumSaddlebag { get; set; } = new();

    public DateTime LastRefreshedUtc { get; set; } = DateTime.MinValue;

    public static string MakeKey(ulong characterContentId) => $"char.{characterContentId:X}";
}
