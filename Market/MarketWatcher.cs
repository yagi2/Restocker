using System;
using System.Collections.Generic;
using Dalamud.Game.Network.Structures;
using Dalamud.Plugin.Services;

namespace Restocker.Market;

/// <summary>
/// Dalamud の <see cref="IMarketBoard.OfferingsReceived"/> イベントで
/// マーケットボード検索結果を受信して <see cref="MarketCache"/> を更新する。
///
/// 旧実装は <c>ItemSearchResult</c> addon を polling していたが、
/// レイアウト変更や node id 揺れで取りこぼしが起きていた。Dalamud のイベント
/// 経由なら listing struct から PricePerUnit/IsHq を直接読めるので確実。
///
/// PennyPincher など他プラグインも同経路。
/// </summary>
public sealed unsafe class MarketWatcher : IDisposable
{
    private readonly IMarketBoard marketBoard;
    private readonly IPluginLog log;
    public MarketCache Cache { get; } = new();

    public MarketWatcher(IMarketBoard marketBoard, IPluginLog log)
    {
        this.marketBoard = marketBoard;
        this.log = log;
        marketBoard.OfferingsReceived += OnOfferingsReceived;
    }

    public void Dispose()
    {
        marketBoard.OfferingsReceived -= OnOfferingsReceived;
    }

    private void OnOfferingsReceived(IMarketBoardCurrentOfferings offerings)
    {
        try
        {
            if (offerings.ItemListings.Count == 0) return;
            var itemId = offerings.ItemListings[0].ItemId;
            if (itemId == 0) return;

            var hqPrices = new List<long>();
            var nqPrices = new List<long>();
            foreach (var listing in offerings.ItemListings)
            {
                if (listing.ItemId != itemId) continue;
                var price = (long)listing.PricePerUnit;
                if (price <= 0) continue;
                (listing.IsHq ? hqPrices : nqPrices).Add(price);
            }

            if (nqPrices.Count > 0) Cache.Replace(itemId, false, nqPrices);
            if (hqPrices.Count > 0) Cache.Replace(itemId, true, hqPrices);

            log.Information($"[Restocker] market cache updated via OfferingsReceived: item={itemId} nq={nqPrices.Count} hq={hqPrices.Count}");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Restocker] OfferingsReceived handler exception");
        }
    }
}
