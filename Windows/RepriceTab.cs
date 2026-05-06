using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using Restocker.Data;
using Restocker.Execution;
using Restocker.Localization;
using Restocker.Market;

namespace Restocker.Windows;

/// <summary>
/// 出品中アイテムのリプライス画面。**リテイナー毎の collapsing header** が top-level、
/// その中に当該リテイナーの listing テーブルが入る。
/// </summary>
public sealed class RepriceTab
{
    private readonly Configuration configuration;
    private readonly Executor executor;
    private readonly ConfirmDialog confirmDialog;
    private readonly MarketCache marketCache;
    private string filter = string.Empty;
    /// <summary>編集中の新価格。キーは "{retainerKey}#{listingIndex}"。</summary>
    private readonly Dictionary<string, long> editedPrice = new();
    private string? lastMatchSummary;
    /// <summary>明示的に展開したリテイナーセクションの key。デフォルトは折りたたみ。</summary>
    private readonly HashSet<string> expandedSections = new();

    public RepriceTab(Configuration configuration, Executor executor, ConfirmDialog confirmDialog, MarketCache marketCache)
    {
        this.configuration = configuration;
        this.executor = executor;
        this.confirmDialog = confirmDialog;
        this.marketCache = marketCache;
    }

    public void Draw()
    {
        DrawToolbar();
        ImGui.Separator();
        DrawRetainers();
        // 残り行は別個に書かない: 「適用」は ToolbarBottom で表示
    }

    private void DrawToolbar()
    {
        ImGui.SetNextItemWidth(240);
        ImGui.InputTextWithHint("##reprice-filter", Strings.Filter, ref filter, 64);
        ImGui.SameLine();
        if (ImGui.SmallButton(Strings.CollapseAll + "##reprice-collapse"))
        {
            expandedSections.Clear();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton(Strings.ExpandAll + "##reprice-expand"))
        {
            foreach (var snap in configuration.Snapshots.Values)
                expandedSections.Add("reprice-" + snap.Key);
        }
        // 適用ボタン
        ImGui.SameLine();
        DrawApplyButton();

        if (!string.IsNullOrEmpty(lastMatchSummary))
        {
            ImGui.TextColored(new System.Numerics.Vector4(0.85f, 0.85f, 0.4f, 1f), lastMatchSummary);
        }
    }

    private void DrawApplyButton()
    {
        var actions = BuildPlannedActions();
        var disabled = actions.Count == 0 || executor.IsRunning;
        if (disabled) ImGui.BeginDisabled();
        if (ImGui.Button(string.Format(Strings.ApplyWithCount, Strings.Apply, actions.Count)))
        {
            confirmDialog.Request(actions, executor.StartApplyActions);
        }
        if (disabled) ImGui.EndDisabled();

        if (executor.IsRunning && executor.Mode == ExecutionMode.ApplyActions)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted(string.Format(Strings.ApplyProgress, executor.CompletedActions, executor.TotalActions, executor.CompletedJobs, executor.TotalJobs));
            ImGui.SameLine();
            if (ImGui.SmallButton(Strings.HeaderStop)) executor.Cancel();
        }
    }

    private List<PlannedAction> BuildPlannedActions()
    {
        var result = new List<PlannedAction>();
        foreach (var snap in configuration.Snapshots.Values)
        {
            foreach (var listing in snap.Listings)
            {
                var key = $"{snap.Key}#{listing.ListingIndex}";
                if (!editedPrice.TryGetValue(key, out var price)) continue;
                if (price <= 0) continue;
                result.Add(new PlannedAction
                {
                    Kind = PlannedActionKind.Reprice,
                    RetainerKey = snap.Key,
                    ItemId = listing.ItemId,
                    IsHQ = listing.IsHQ,
                    Quantity = listing.Quantity,
                    UnitPrice = price,
                    ListingIndex = listing.ListingIndex,
                });
            }
        }
        return result;
    }

    private void DrawRetainers()
    {
        if (configuration.Snapshots.Count == 0)
        {
            ImGui.TextDisabled(Strings.EmptyHint);
            return;
        }

        // テーブル領域は残り高さフル
        var size = ImGui.GetContentRegionAvail();
        if (!ImGui.BeginChild("##reprice-scroll", size, false, ImGuiWindowFlags.HorizontalScrollbar))
        {
            ImGui.EndChild();
            return;
        }

        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        var snapshots = configuration.Snapshots.Values
            .OrderBy(s => s.CharacterName).ThenBy(s => s.RetainerName).ToList();

        foreach (var snap in snapshots)
        {
            // フィルタ済みで何も表示すべきものが無ければセクション自体を出さない
            var filteredListings = FilterListings(snap, sheet);
            if (filteredListings.Count == 0 && filter.Length > 0) continue;

            var header = string.Format(Strings.RetainerHeader, snap.RetainerName, filteredListings.Count, FreshnessSuffix(snap));
            if (!DrawCollapsingHeader("reprice-" + snap.Key, header)) continue;

            // 最安値 -1ギル を MarketCache から適用。
            // 自動 fetch (listing slot click → ComparePrices) は ECommons の Callback
            // シグネチャが現環境で listing slot click として認識されず止まる問題があり、
            // 一旦 cache 経由のみ。事前にマーケットボードで対象アイテムを開いておけば
            // MarketWatcher が cache に相場を吸い上げる。
            if (ImGui.SmallButton(Strings.RepriceMatchLowestThisRetainer + "##matchlowest-" + snap.Key))
            {
                ApplyMatchLowestForRetainer(snap);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Strings.RepriceMatchLowestTooltip);

            DrawRetainerListings(snap, filteredListings);
        }

        ImGui.EndChild();
    }

    private List<(ListingEntry Entry, string ItemName)> FilterListings(RetainerSnapshot snap, Lumina.Excel.ExcelSheet<Item> sheet)
    {
        var rows = new List<(ListingEntry, string)>();
        foreach (var l in snap.Listings.OrderBy(l => l.ListingIndex))
        {
            var name = sheet.TryGetRow(l.ItemId, out var row) ? row.Name.ExtractText() : $"#{l.ItemId}";
            if (l.IsHQ) name += " (HQ)";
            if (filter.Length > 0 && !name.Contains(filter, System.StringComparison.OrdinalIgnoreCase)) continue;
            rows.Add((l, name));
        }
        return rows;
    }

    private static string FreshnessSuffix(RetainerSnapshot snap)
    {
        if (snap.LastRefreshedUtc == System.DateTime.MinValue) return Strings.HeaderUnknown;
        var age = System.DateTime.UtcNow - snap.LastRefreshedUtc;
        if (age.TotalMinutes < 1) return "now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours}h";
        return $"{(int)age.TotalDays}d";
    }

    /// <summary>
    /// 折りたたみ状態を <see cref="expandedSections"/> で明示管理する CollapsingHeader。
    /// デフォルト = 折りたたみ。ユーザーが開いたら expandedSections に追加。
    /// </summary>
    private bool DrawCollapsingHeader(string id, string label)
    {
        var open = expandedSections.Contains(id);
        ImGui.SetNextItemOpen(open, ImGuiCond.Always);
        var nowOpen = ImGui.CollapsingHeader(label + "##" + id);
        if (nowOpen != open)
        {
            if (nowOpen) expandedSections.Add(id);
            else expandedSections.Remove(id);
        }
        return nowOpen;
    }

    /// <summary>
    /// このリテイナーの listing にだけ MarketCache の最安値 -1ギルを適用。
    /// </summary>
    private void ApplyMatchLowestForRetainer(RetainerSnapshot snap)
    {
        var seen = new HashSet<(uint, bool)>();
        var missing = new HashSet<(uint, bool, string)>();
        var applied = 0;
        var sheet = Plugin.DataManager.GetExcelSheet<Item>();

        foreach (var listing in snap.Listings)
        {
            var name = sheet.TryGetRow(listing.ItemId, out var row) ? row.Name.ExtractText() : $"#{listing.ItemId}";
            if (listing.IsHQ) name += " (HQ)";
            if (filter.Length > 0 && !name.Contains(filter, System.StringComparison.OrdinalIgnoreCase)) continue;

            seen.Add((listing.ItemId, listing.IsHQ));
            if (!marketCache.HasData(listing.ItemId, listing.IsHQ))
            {
                missing.Add((listing.ItemId, listing.IsHQ, name));
                continue;
            }
            var lowest = marketCache.GetLowest(listing.ItemId, listing.IsHQ);
            if (lowest <= 1) continue;
            editedPrice[$"{snap.Key}#{listing.ListingIndex}"] = lowest - 1;
            applied++;
        }

        if (missing.Count == 0)
            lastMatchSummary = $"{snap.RetainerName}: " + string.Format(Strings.MatchAppliedSummary, applied, seen.Count);
        else
            lastMatchSummary = $"{snap.RetainerName}: " + string.Format(Strings.MatchAppliedSummaryWithMissing, applied, seen.Count, missing.Count,
                string.Join(", ", missing.Take(5).Select(m => m.Item3)) + (missing.Count > 5 ? "…" : ""));
    }

    private void DrawRetainerListings(RetainerSnapshot snap, List<(ListingEntry Entry, string ItemName)> rows)
    {
        const ImGuiTableFlags flags =
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp |
            ImGuiTableFlags.NoSavedSettings;

        if (!ImGui.BeginTable($"##reprice-{snap.Key}", 5, flags)) return;

        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 30);
        ImGui.TableSetupColumn(Strings.ColItem, ImGuiTableColumnFlags.WidthStretch, 0.5f);
        ImGui.TableSetupColumn(Strings.ColTotalQty, ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn(Strings.ColCurrentPrice, ImGuiTableColumnFlags.WidthStretch, 0.25f);
        ImGui.TableSetupColumn(Strings.ColNewPrice, ImGuiTableColumnFlags.WidthStretch, 0.25f);
        ImGui.TableHeadersRow();

        foreach (var (l, name) in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(l.ListingIndex.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(l.Quantity.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(l.UnitPrice == 0 ? Strings.PriceUnknown : l.UnitPrice.ToString("N0"));
            ImGui.TableNextColumn();
            DrawPriceInput($"{snap.Key}#{l.ListingIndex}");
        }

        ImGui.EndTable();
    }

    private void DrawPriceInput(string key)
    {
        var v = (int)editedPrice.GetValueOrDefault(key, 0);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputInt($"##price-{key}", ref v, 0))
        {
            if (v < 0) v = 0;
            editedPrice[key] = v;
        }
    }
}
