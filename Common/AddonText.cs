using System;
using Lumina.Excel.Sheets;

namespace Restocker.Common;

/// <summary>
/// SelectString のリテイナーメニュー項目を **substring 一致のセット** で識別する。
///
/// FFXIV アップデートで Lumina の Addon row 値とゲーム内表示テキストが一致
/// しなくなる事故が稀にある（実例: 5480 が「アイテム売却を依頼する」だった
/// ものが「マーケット出品（リテイナー所持品から）」に差し替わるケース）。
/// row id ベースの完全一致だと巡回が止まるため、各言語の安定して残りそうな
/// 部分文字列のリストで照合する。
/// </summary>
public static class AddonText
{
    /// <summary>
    /// 「リテイナー所持品から」マーケット出品 — refresh-all で SellList を開く時に使う項目。
    /// </summary>
    public static bool IsHaveRetainerSellItemsEntry(string entryText)
    {
        if (string.IsNullOrEmpty(entryText)) return false;

        // JP 新表記: 「マーケット出品（リテイナー所持品から）」
        if (entryText.Contains("リテイナー所持品", StringComparison.Ordinal)) return true;
        // JP 旧表記: 「アイテム売却を依頼する」
        if (entryText.Contains("アイテム売却", StringComparison.Ordinal)) return true;
        // EN 新表記: "List items on the market (retainer's inventory)" 系
        if (entryText.Contains("retainer's inventory", StringComparison.OrdinalIgnoreCase)) return true;
        if (entryText.Contains("retainer inventory", StringComparison.OrdinalIgnoreCase)) return true;
        // EN 旧表記: "Have Retainer Sell Items"
        if (entryText.Contains("Sell Items", StringComparison.OrdinalIgnoreCase)) return true;
        if (entryText.Contains("Retainer Sell", StringComparison.OrdinalIgnoreCase)) return true;
        // DE: 「Verkaufsangebote des Gehilfen ändern」「auf dem Markt verkaufen」系
        if (entryText.Contains("Verkaufsangebote", StringComparison.OrdinalIgnoreCase)) return true;
        if (entryText.Contains("auf dem Markt", StringComparison.OrdinalIgnoreCase) &&
            entryText.Contains("Gehilfen", StringComparison.OrdinalIgnoreCase)) return true;
        // FR: "vendre sur le marché" + "intendant" / "marchandises"
        if (entryText.Contains("vendre sur le marché", StringComparison.OrdinalIgnoreCase) &&
            (entryText.Contains("intendant", StringComparison.OrdinalIgnoreCase) ||
             entryText.Contains("marchandises", StringComparison.OrdinalIgnoreCase))) return true;
        // ZH 簡体: 「市场上架」+ 雇员 / 物品
        if (entryText.Contains("市场上架") && entryText.Contains("雇员")) return true;
        if (entryText.Contains("出售物品") && entryText.Contains("雇员")) return true;
        // KO: 「시장 등록」 + 리테이너
        if (entryText.Contains("시장 등록") && entryText.Contains("리테이너")) return true;
        // 互換: Lumina row 5480 が現在指してる文字列とも一致しておく
        var legacy = LegacySellItemsRow.Value;
        if (!string.IsNullOrEmpty(legacy) && entryText.Contains(legacy, StringComparison.Ordinal)) return true;

        return false;
    }

    /// <summary>「終了する／リテイナーを帰す」系の項目。</summary>
    public static bool IsQuitEntry(string entryText)
    {
        if (string.IsNullOrEmpty(entryText)) return false;

        // JP: 「リテイナーを帰す」「終了する」
        if (entryText.Contains("帰す", StringComparison.Ordinal)) return true;
        if (entryText.StartsWith("終了", StringComparison.Ordinal)) return true;
        // EN: "Dismiss retainer", "Quit"
        if (entryText.StartsWith("Dismiss", StringComparison.OrdinalIgnoreCase)) return true;
        if (entryText.Equals("Quit", StringComparison.OrdinalIgnoreCase)) return true;
        if (entryText.Equals("Quit.", StringComparison.OrdinalIgnoreCase)) return true;
        // DE: "Beenden" / "Verlassen"
        if (entryText.StartsWith("Beenden", StringComparison.OrdinalIgnoreCase)) return true;
        if (entryText.StartsWith("Verlassen", StringComparison.OrdinalIgnoreCase)) return true;
        // FR: "Quitter"
        if (entryText.StartsWith("Quitter", StringComparison.OrdinalIgnoreCase)) return true;
        // ZH: 「退出」「结束」
        if (entryText.Contains("退出")) return true;
        if (entryText.Contains("结束")) return true;
        // KO: 「종료」「돌려보내기」
        if (entryText.Contains("종료")) return true;
        if (entryText.Contains("돌려보내")) return true;

        var legacy = LegacyQuitRow.Value;
        if (!string.IsNullOrEmpty(legacy) && entryText.Equals(legacy, StringComparison.Ordinal)) return true;

        return false;
    }

    /// <summary>RetainerSellList の listing 行クリック後の ContextMenu に出る「価格を変更する」系。</summary>
    public static bool IsAdjustPriceEntry(string entryText)
    {
        if (string.IsNullOrEmpty(entryText)) return false;
        if (entryText.Contains("価格を変更", StringComparison.Ordinal)) return true;
        if (entryText.Contains("価格変更", StringComparison.Ordinal)) return true;
        if (entryText.Contains("Adjust price", StringComparison.OrdinalIgnoreCase)) return true;
        if (entryText.Contains("Preis ändern", StringComparison.OrdinalIgnoreCase)) return true;
        if (entryText.Contains("Preis aendern", StringComparison.OrdinalIgnoreCase)) return true;
        if (entryText.Contains("Changer le prix", StringComparison.OrdinalIgnoreCase)) return true;
        if (entryText.Contains("Modifier le prix", StringComparison.OrdinalIgnoreCase)) return true;
        if (entryText.Contains("修改价格")) return true;
        if (entryText.Contains("调整价格")) return true;
        if (entryText.Contains("가격 변경")) return true;
        return false;
    }

    /// <summary>右クリックメニューの「マーケットに出品する」系。Addon row 99 ベース + 多言語ハードコード。</summary>
    public static bool IsPutUpForSaleEntry(string entryText)
    {
        if (string.IsNullOrEmpty(entryText)) return false;
        if (entryText.Contains("マーケットに出品") || entryText.Contains("マーケット出品")) return true;
        if (entryText.Contains("出品", StringComparison.Ordinal) && !entryText.Contains("販売履歴")) return true;
        if (entryText.Contains("Put Up for Sale", StringComparison.OrdinalIgnoreCase)) return true;
        if (entryText.Contains("Sell on Market", StringComparison.OrdinalIgnoreCase)) return true;
        if (entryText.Contains("Auf dem Markt anbieten", StringComparison.OrdinalIgnoreCase)) return true;
        if (entryText.Contains("Mettre en vente", StringComparison.OrdinalIgnoreCase)) return true;
        if (entryText.Contains("市场出售") || entryText.Contains("市场上架")) return true;
        if (entryText.Contains("시장에 등록") || entryText.Contains("시장 등록")) return true;
        var legacy = LegacyPutUpForSaleRow.Value;
        if (!string.IsNullOrEmpty(legacy) && entryText.Contains(legacy, StringComparison.Ordinal)) return true;
        return false;
    }

    // Lazy factory 全体を try でくるむ (Dalamud / Lumina 未ロードの単体テスト下でも
    // 例外で死なないように)。ホスト下では try/catch のオーバーヘッドは無視できる。
    private static readonly Lazy<string> LegacySellItemsRow = new(() => TryRow(5480));
    private static readonly Lazy<string> LegacyQuitRow = new(() => TryRow(2383));
    private static readonly Lazy<string> LegacyPutUpForSaleRow = new(() => TryRow(99));

    private static string TryRow(uint rowId)
    {
        try { return SafeRowText(rowId); } catch { return string.Empty; }
    }

    private static string SafeRowText(uint rowId)
    {
        // ホスト未初期化 (= 単体テスト) のときは Lumina への参照を JIT させずに早抜け。
        if (Plugin.DataManager is null) return string.Empty;
        try { return SafeRowTextCore(rowId); } catch { return string.Empty; }
    }

    private static string SafeRowTextCore(uint rowId)
    {
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Addon>();
            if (sheet.TryGetRow(rowId, out var row))
            {
                return row.Text.ExtractText() ?? string.Empty;
            }
        }
        catch { }
        return string.Empty;
    }
}
