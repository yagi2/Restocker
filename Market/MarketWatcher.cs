using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace Restocker.Market;

/// <summary>
/// ユーザーがゲーム内マーケット (ItemSearchResult addon) で何かを検索するたびに、
/// その結果（listings の価格列）を <see cref="MarketCache"/> に取り込む。
///
/// 0.1.x ではユーザーが自分でマーケットを開くワークフロー前提。
/// 「全行に最安値 -1ギルを適用」ボタンが押された時に MarketCache から拾う。
/// </summary>
public sealed unsafe class MarketWatcher : IDisposable
{
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IPluginLog log;
    public MarketCache Cache { get; } = new();

    public MarketWatcher(IAddonLifecycle addonLifecycle, IPluginLog log)
    {
        this.addonLifecycle = addonLifecycle;
        this.log = log;
        addonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "ItemSearchResult", OnUpdate);
    }

    public void Dispose()
    {
        addonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, "ItemSearchResult", OnUpdate);
    }

    private void OnUpdate(AddonEvent type, AddonArgs args)
    {
        try
        {
            CaptureFromAddon();
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Restocker] MarketWatcher capture exception");
        }
    }

    private void CaptureFromAddon()
    {
        var info = InfoProxyItemSearch.Instance();
        if (info == null) return;
        var itemId = info->SearchItemId;
        if (itemId == 0) return;

        var addon = (AddonItemSearchResult*)Common.AddonHelper.GetVisible("ItemSearchResult");
        if (addon == null) return;

        var list = addon->Results;
        if (list == null) return;

        var hqPrices = new List<long>();
        var nqPrices = new List<long>();

        for (var i = 0; i < list->ListLength; i++)
        {
            var renderer = list->ItemRendererList[i].AtkComponentListItemRenderer;
            if (renderer == null) continue;

            var priceNode = renderer->GetTextNodeById(5);
            if (priceNode == null) continue;

            var priceStr = priceNode->NodeText.ToString();
            if (string.IsNullOrEmpty(priceStr)) continue;

            // 価格テキストはギルアイコン・カンマ等が混じるので数字以外を落としてから parse
            var clean = StripNonDigits(priceStr);
            if (!long.TryParse(clean, out var price) || price <= 0) continue;

            // HQ 判定: NodeId=3 の image node が visible なら HQ
            var hqNode = renderer->GetImageNodeById(3);
            var isHq = hqNode != null && hqNode->AtkResNode.IsVisible();

            (isHq ? hqPrices : nqPrices).Add(price);
        }

        // クリアと差し替えはアイテム種別単位。混じってる場合は両方更新。
        Cache.Replace(itemId, false, nqPrices);
        if (hqPrices.Count > 0) Cache.Replace(itemId, true, hqPrices);

        log.Debug($"[Restocker] market cache updated: item={itemId} hq={hqPrices.Count} nq={nqPrices.Count}");
    }

    private static string StripNonDigits(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s) if (c >= '0' && c <= '9') sb.Append(c);
        return sb.ToString();
    }
}
