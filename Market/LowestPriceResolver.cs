using System.Collections.Generic;
using System.Linq;

namespace Restocker.Market;

/// <summary>
/// 「マーケット最安値 -1 ギル」算出時の **外れ値除外つき最安値ピッカー**。純ロジック。
///
/// 板の先頭に極端に安い 1〜2 件だけ刺さっているケース（業者の誘い水・釣り出品）を
/// 自動的に弾き、2 番手以降の本格的な価格帯（クラスター）を真の最安値として返す。
/// </summary>
public static class LowestPriceResolver
{
    /// <summary>
    /// 価格昇順のリストから「実用的な最安値」を返す。
    /// </summary>
    /// <param name="pricesAscending">マーケット問い合わせ結果。**昇順ソート済み**である前提。</param>
    /// <param name="maxOutlierGroupSize">外れ値とみなして弾く最大件数（先頭から）。既定 2。</param>
    /// <param name="gapRatioThreshold">「次の価格との比率がこの値以上なら外れ値の境界」と判定する閾値。既定 1.5（=次が 1.5 倍以上）。</param>
    /// <returns>外れ値を除外した先頭の価格。空入力なら 0。</returns>
    public static long Pick(
        IReadOnlyList<long> pricesAscending,
        int maxOutlierGroupSize = 2,
        double gapRatioThreshold = 1.5)
    {
        if (pricesAscending.Count == 0) return 0;
        if (pricesAscending.Count == 1) return pricesAscending[0];

        var limit = System.Math.Min(maxOutlierGroupSize, pricesAscending.Count - 1);
        var outlierEnd = 0;
        for (var i = 0; i < limit; i++)
        {
            // 0 円が混ざっていたらそれは未取得データ。先頭から除外。
            if (pricesAscending[i] <= 0)
            {
                outlierEnd = i + 1;
                continue;
            }
            var ratio = (double)pricesAscending[i + 1] / pricesAscending[i];
            if (ratio >= gapRatioThreshold)
            {
                // i 以下を外れ値として弾く
                outlierEnd = i + 1;
                break;
            }
        }
        return pricesAscending[outlierEnd];
    }

    /// <summary>
    /// ソート保証なしで投げ込みたい時の便利オーバーロード。内部で昇順ソートする。
    /// </summary>
    public static long Pick(
        IEnumerable<long> prices,
        int maxOutlierGroupSize = 2,
        double gapRatioThreshold = 1.5)
        => Pick(prices.OrderBy(p => p).ToList(), maxOutlierGroupSize, gapRatioThreshold);
}
