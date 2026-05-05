using System.Collections.Generic;
using System.Linq;

namespace Restocker.Market;

/// <summary>
/// 「最安値 -1 ギル」のための短命なマーケット価格キャッシュ。
/// ItemSearchResult addon が更新されるたびに <see cref="MarketWatcher"/> が
/// 該当アイテム種ぶんの listings 価格を上書き保存する。
///
/// インスタンスは Plugin 寿命と同じ。プロセス終了で消える（永続化しない）。
/// </summary>
public sealed class MarketCache
{
    private readonly Dictionary<(uint ItemId, bool IsHQ), List<long>> store = new();

    /// <summary>
    /// 1 検索ぶんの listings をまるごと差し替える。空リストでクリアの意味も兼ねる。
    /// </summary>
    public void Replace(uint itemId, bool isHQ, IEnumerable<long> pricesAscending)
    {
        store[(itemId, isHQ)] = pricesAscending.OrderBy(p => p).ToList();
    }

    /// <summary>外れ値除外つきの最安値。データ無しなら 0。</summary>
    public long GetLowest(uint itemId, bool isHQ)
    {
        if (!store.TryGetValue((itemId, isHQ), out var prices)) return 0;
        return LowestPriceResolver.Pick(prices);
    }

    public bool HasData(uint itemId, bool isHQ) => store.ContainsKey((itemId, isHQ));

    public IReadOnlyList<long> GetRaw(uint itemId, bool isHQ)
        => store.TryGetValue((itemId, isHQ), out var v) ? v : new List<long>();
}
