using System;
using System.Collections.Generic;

namespace Restocker.Data;

/// <summary>
/// あるリテイナーの「最後に呼び出した瞬間」のマーケット出品とインベントリのスナップショット。
/// パッシブ収集（召喚時）と「全リテイナー更新」アクティブリフレッシュの両方で書き込まれる。
/// </summary>
[Serializable]
public sealed class RetainerSnapshot
{
    /// <summary>
    /// 一意キー: <c>"{CharacterContentId:X}.{RetainerId:X}"</c>。Configuration.Snapshots の Dict キーと一致する。
    /// </summary>
    public string Key { get; set; } = string.Empty;

    public ulong CharacterContentId { get; set; }
    public string CharacterName { get; set; } = string.Empty;

    public ulong RetainerId { get; set; }
    public string RetainerName { get; set; } = string.Empty;

    /// <summary>所属ワールド ID。別ワールドリテイナーは現在ログイン中のキャラから召喚不能。</summary>
    public uint WorldId { get; set; }

    /// <summary>現在の出品一覧（最大 20 件）。空 = まだ取得していない／出品ゼロ。</summary>
    public List<ListingEntry> Listings { get; set; } = new();

    /// <summary>リテイナー所持品（出品ボックス除く、通常インベントリ）。</summary>
    public List<InventoryEntry> Inventory { get; set; } = new();

    /// <summary>最終取得時刻（UTC）。UI 上の「N 日前」鮮度表示に使う。<see cref="DateTime.MinValue"/> なら未取得。</summary>
    public DateTime LastRefreshedUtc { get; set; } = DateTime.MinValue;

    public static string MakeKey(ulong characterContentId, ulong retainerId)
        => $"{characterContentId:X}.{retainerId:X}";
}
