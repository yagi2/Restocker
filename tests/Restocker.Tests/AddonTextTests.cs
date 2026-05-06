using Restocker.Common;

namespace Restocker.Tests;

/// <summary>
/// AddonText の言語横断 predicate の振る舞い確認。
/// 注意: <see cref="AddonText"/> 内部で Lumina の row を引く Lazy 初期化があるが、
/// それはユニットテスト（Plugin.DataManager 未初期化）では空文字列になるだけで、
/// 各 predicate のロジックには影響しない。
/// </summary>
public class AddonTextTests
{
    [Theory]
    [InlineData("マーケット出品（リテイナー所持品から）")]
    [InlineData("リテイナー所持品から販売を依頼する")]
    [InlineData("アイテム売却を依頼する")]
    [InlineData("List items on the market (retainer's inventory)")]
    [InlineData("Have Retainer Sell Items")]
    [InlineData("Verkaufsangebote des Gehilfen ändern")]
    [InlineData("Auf dem Markt verkaufen lassen (Gehilfen-Inventar)")]
    [InlineData("Faire vendre sur le marché (intendant)")]
    [InlineData("市场上架（雇员物品）")]
    [InlineData("리테이너 인벤토리에서 시장 등록")]
    public void IsHaveRetainerSellItemsEntry_matches_known_localisations(string entry)
        => Assert.True(AddonText.IsHaveRetainerSellItemsEntry(entry));

    [Theory]
    [InlineData("")]
    [InlineData("販売履歴を見る")]
    [InlineData("リテイナーベンチャーの確認")]
    [InlineData("Quit")]
    public void IsHaveRetainerSellItemsEntry_rejects_unrelated(string entry)
        => Assert.False(AddonText.IsHaveRetainerSellItemsEntry(entry));

    [Theory]
    [InlineData("リテイナーを帰す")]
    [InlineData("終了する")]
    [InlineData("Dismiss retainer")]
    [InlineData("Quit")]
    [InlineData("Quit.")]
    [InlineData("Beenden")]
    [InlineData("Quitter")]
    [InlineData("退出")]
    [InlineData("종료")]
    public void IsQuitEntry_matches_known_localisations(string entry)
        => Assert.True(AddonText.IsQuitEntry(entry));

    [Theory]
    [InlineData("価格を変更する")]
    [InlineData("Adjust price")]
    [InlineData("Preis ändern")]
    [InlineData("Changer le prix")]
    [InlineData("修改价格")]
    [InlineData("가격 변경")]
    public void IsAdjustPriceEntry_matches_known_localisations(string entry)
        => Assert.True(AddonText.IsAdjustPriceEntry(entry));

    [Theory]
    [InlineData("")]
    [InlineData("販売履歴を見る")]
    [InlineData("マーケットに出品する")] // adjacent intent but different action
    public void IsAdjustPriceEntry_rejects_unrelated(string entry)
        => Assert.False(AddonText.IsAdjustPriceEntry(entry));

    [Theory]
    [InlineData("マーケットに出品する")]
    [InlineData("マーケット出品")]
    [InlineData("Put Up for Sale")]
    [InlineData("Sell on Market")]
    [InlineData("Auf dem Markt anbieten")]
    [InlineData("Mettre en vente")]
    [InlineData("市场出售")]
    [InlineData("시장에 등록")]
    public void IsPutUpForSaleEntry_matches_known_localisations(string entry)
        => Assert.True(AddonText.IsPutUpForSaleEntry(entry));

    [Fact]
    public void IsPutUpForSaleEntry_rejects_history_lookalike()
    {
        // 販売履歴 includes 出品 substring; the predicate explicitly excludes it.
        Assert.False(AddonText.IsPutUpForSaleEntry("販売履歴を見る"));
    }
}
