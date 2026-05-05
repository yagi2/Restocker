using Lumina.Excel.Sheets;

namespace Restocker.Common;

/// <summary>
/// Lumina の Addon シートからゲーム内表示文字列を引いて、ゲーム言語に追従したマッチングに使う。
/// 既知の row id は AutoRetainer / 各種プラグインで実証済みのもの。
/// </summary>
public static class AddonText
{
    private static string? cacheHaveRetainerSellItems;
    private static string? cacheQuit;
    private static string? cacheEntrustOrWithdrawItems;
    private static string? cacheEntrustToRetainer;
    private static string? cachePutUpForSale;

    /// <summary>Addon row 5480: 「(リテイナーに) マーケットへの出品を任せる」系のテキスト。</summary>
    public static string HaveRetainerSellItems
        => cacheHaveRetainerSellItems ??= ResolveRow(5480, "Have Retainer Sell Items");

    /// <summary>Addon row 2383: SelectString の「終了する」エントリ。</summary>
    public static string Quit => cacheQuit ??= ResolveRow(2383, "Quit");

    /// <summary>Addon row 2378: 「アイテムを預ける／引き出す」エントリ。</summary>
    public static string EntrustOrWithdrawItems
        => cacheEntrustOrWithdrawItems ??= ResolveRow(2378, "Entrust or withdraw items.");

    /// <summary>Addon row 97: 右クリックメニューの「リテイナーに預ける」。</summary>
    public static string EntrustToRetainer
        => cacheEntrustToRetainer ??= ResolveRow(97, "Entrust to Retainer");

    /// <summary>Addon row 99: 右クリックメニューの「マーケットに出品する」。</summary>
    public static string PutUpForSale
        => cachePutUpForSale ??= ResolveRow(99, "Put Up for Sale");

    private static string ResolveRow(uint rowId, string fallback)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Addon>();
        if (sheet.TryGetRow(rowId, out var row))
        {
            var text = row.Text.ExtractText();
            if (!string.IsNullOrEmpty(text)) return text;
        }
        return fallback;
    }
}
