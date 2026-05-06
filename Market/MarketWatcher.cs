using System;
using Dalamud.Game.Network.Structures;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Restocker.Market;

/// <summary>
/// Dalamud の <see cref="IMarketBoard.OfferingsReceived"/> イベントで
/// マーケットボード検索結果を受信して <see cref="MarketCache"/> を更新する。
/// PennyPincher と同じ流儀で、
///   - 自リテイナー listing はスキップ（自分で自分を undercut しない）
///   - HQ / NQ それぞれで「サーバが返した順序の先頭にある有効 listing」を採用
///     (server returns listings sorted by price ascending)
/// その採用 price を 1 件だけ <see cref="MarketCache"/> に入れる。
/// </summary>
public sealed unsafe class MarketWatcher : IDisposable
{
    private readonly IMarketBoard marketBoard;
    private readonly IPluginLog log;
    public MarketCache Cache { get; } = new();

    /// <summary>同じ検索結果が複数回飛んでくる server エラー時の保護 (PennyPincher 由来)。</summary>
    private int lastRequestId = -1;

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
            if (offerings.RequestId == lastRequestId) return;
            lastRequestId = offerings.RequestId;

            var itemId = offerings.ItemListings[0].ItemId;
            if (itemId == 0) return;

            // デバッグ用に受信 listing をすべてログに出す。OWN マークは自リテイナーで
            // スキップされた listing。各行: HQ/NQ, price, retainerId。
            var dump = new System.Text.StringBuilder();
            dump.Append($"[Restocker] OfferingsReceived item={itemId} count={offerings.ItemListings.Count} reqId={offerings.RequestId}");

            long? nqPrice = null;
            long? hqPrice = null;
            for (var idx = 0; idx < offerings.ItemListings.Count; idx++)
            {
                var listing = offerings.ItemListings[idx];
                if (listing.ItemId != itemId) continue;
                var isOwn = IsOwnRetainer(listing.RetainerId);
                var price = (long)listing.PricePerUnit;
                var hqMark = listing.IsHq ? "HQ" : "NQ";
                var ownMark = isOwn ? " OWN" : "";
                dump.Append($"\n  [{idx,2}] {hqMark} price={price,9:N0} retainer={listing.RetainerId}{ownMark}");

                if (isOwn) continue;
                if (price <= 0) continue;
                if (listing.IsHq) { if (hqPrice == null) hqPrice = price; }
                else { if (nqPrice == null) nqPrice = price; }
            }

            if (nqPrice.HasValue) Cache.Replace(itemId, false, new[] { nqPrice.Value });
            if (hqPrice.HasValue) Cache.Replace(itemId, true, new[] { hqPrice.Value });

            dump.Append($"\n  → adopted nq={nqPrice?.ToString("N0") ?? "-"}  hq={hqPrice?.ToString("N0") ?? "-"}");
            log.Information(dump.ToString());
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Restocker] OfferingsReceived handler exception");
        }
    }

    private static bool IsOwnRetainer(ulong retainerId)
    {
        var rm = RetainerManager.Instance();
        if (rm == null) return false;
        for (uint i = 0; i < rm->GetRetainerCount(); ++i)
        {
            var r = rm->GetRetainerBySortedIndex(i);
            if (r != null && r->RetainerId == retainerId) return true;
        }
        return false;
    }
}
