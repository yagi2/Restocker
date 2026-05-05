using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace Restocker.Market;

/// <summary>
/// ItemSearchResult addon が開いている間、ポーリング + 複数イベント hook で
/// 表示中の listings 価格を読み取り <see cref="MarketCache"/> に保存する。
///
/// PostRequestedUpdate イベントが言語/タイミングで取りこぼすケースを観測したため、
/// IFramework.Update でも 500ms ポーリングしてフォールバック取得する。
/// 価格テキストの NodeId も複数候補（4, 5, 6, 7, 10）を順に試して、
/// 最初に gil 数として parse できたものを採用する。
/// </summary>
public sealed unsafe class MarketWatcher : IDisposable
{
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    public MarketCache Cache { get; } = new();

    private DateTime nextPoll = DateTime.MinValue;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private static readonly uint[] PriceNodeCandidates = { 5, 4, 6, 7, 10, 11 };

    public MarketWatcher(IAddonLifecycle addonLifecycle, IFramework framework, IPluginLog log)
    {
        this.addonLifecycle = addonLifecycle;
        this.framework = framework;
        this.log = log;

        addonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "ItemSearchResult", OnAddonEvent);
        addonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, "ItemSearchResult", OnAddonEvent);
        framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        addonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, "ItemSearchResult", OnAddonEvent);
        addonLifecycle.UnregisterListener(AddonEvent.PostReceiveEvent, "ItemSearchResult", OnAddonEvent);
        framework.Update -= OnFrameworkUpdate;
    }

    private void OnAddonEvent(AddonEvent type, AddonArgs args)
    {
        try { CaptureFromAddon(); } catch (Exception ex) { log.Error(ex, "[Restocker] MarketWatcher event capture exception"); }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var now = DateTime.UtcNow;
        if (now < nextPoll) return;
        nextPoll = now + PollInterval;
        try { CaptureFromAddon(); } catch (Exception ex) { log.Error(ex, "[Restocker] MarketWatcher poll exception"); }
    }

    private void CaptureFromAddon()
    {
        var addon = (AddonItemSearchResult*)Common.AddonHelper.GetVisible("ItemSearchResult");
        if (addon == null) return;

        var info = InfoProxyItemSearch.Instance();
        if (info == null) return;
        var itemId = info->SearchItemId;
        if (itemId == 0) return;

        var list = addon->Results;
        if (list == null) return;
        if (list->ListLength == 0) return;

        var hqPrices = new List<long>();
        var nqPrices = new List<long>();
        var anyParsed = false;

        for (var i = 0; i < list->ListLength; i++)
        {
            var renderer = list->ItemRendererList[i].AtkComponentListItemRenderer;
            if (renderer == null) continue;

            // 価格テキストの NodeId は ItemSearchResult のレイアウト変更で揺れるため、
            // 候補 ID を順に試して最初に gil 数として parse できたものを採用
            long price = 0;
            foreach (var nid in PriceNodeCandidates)
            {
                var node = renderer->GetTextNodeById(nid);
                if (node == null) continue;
                var raw = node->NodeText.ToString() ?? string.Empty;
                if (string.IsNullOrEmpty(raw)) continue;
                var clean = StripNonDigits(raw);
                if (string.IsNullOrEmpty(clean)) continue;
                if (long.TryParse(clean, out var v) && v > 0)
                {
                    price = v;
                    break;
                }
            }
            if (price <= 0) continue;
            anyParsed = true;

            // HQ 判定: NodeId=3 の image node が visible なら HQ。NodeId が違う環境用のフォールバックも兼ねる。
            var isHq = false;
            foreach (var nid in new uint[] { 3, 2, 6 })
            {
                var hqNode = renderer->GetImageNodeById(nid);
                if (hqNode != null && hqNode->AtkResNode.IsVisible())
                {
                    isHq = true;
                    break;
                }
            }
            (isHq ? hqPrices : nqPrices).Add(price);
        }

        if (!anyParsed) return; // この呼び出しでは何も拾えなかったので Cache は触らない

        // 同 itemId の HQ/NQ をそれぞれ差し替え（無いほうは触らない）
        if (nqPrices.Count > 0) Cache.Replace(itemId, false, nqPrices);
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
