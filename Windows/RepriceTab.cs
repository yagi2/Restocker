using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using Restocker.Data;
using Restocker.Execution;
using Restocker.Localization;
using Restocker.Market;

namespace Restocker.Windows;

/// <summary>
/// 出品中アイテムのリプライス画面。各 listing 行で「+追加」を押すとプランに積まれ、
/// 最後に「適用」ボタンで pendingPlans を一括実行する (新規出品タブと同じ流儀)。
/// 「全てのアイテム ...」ボタンは fetch → editedPrice 反映までを行い、その後ユーザーが
/// 「全部プランに追加」で pendingPlans に流し込めるようにしてある。
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
    /// <summary>「最安値 -Xギル」の X。セッション中のみ有効 (永続化しない)。</summary>
    private int undercutDelta = 1;
    /// <summary>適用前のプランリスト。</summary>
    private readonly List<RepricePlan> pendingPlans = new();

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
        ImGui.Separator();
        DrawPendingPlans();
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
        ImGui.SameLine();
        DrawApplyButton();

        if (!string.IsNullOrEmpty(lastMatchSummary))
        {
            ImGui.TextColored(new System.Numerics.Vector4(0.85f, 0.85f, 0.4f, 1f), lastMatchSummary);
        }
    }

    private void DrawApplyButton()
    {
        var actions = BuildActionsFromPlans();
        var disabled = actions.Count == 0 || executor.IsRunning;
        if (disabled) ImGui.BeginDisabled();
        if (ImGui.Button(string.Format(Strings.ApplyWithCount, Strings.Apply, actions.Count)))
        {
            confirmDialog.Request(actions, list =>
            {
                executor.StartApplyActions(list);
                pendingPlans.Clear();
            });
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

    private List<PlannedAction> BuildActionsFromPlans()
    {
        var result = new List<PlannedAction>();
        foreach (var p in pendingPlans)
        {
            result.Add(new PlannedAction
            {
                Kind = PlannedActionKind.Reprice,
                RetainerKey = p.RetainerKey,
                ItemId = p.ItemId,
                IsHQ = p.IsHQ,
                Quantity = p.Quantity,
                UnitPrice = p.NewPrice,
                ListingIndex = p.ListingIndex,
            });
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

        // 上 60% を listings、下 40% を pending plans に振る
        var avail = ImGui.GetContentRegionAvail();
        var topHeight = System.Math.Max(160f, avail.Y * 0.6f);
        if (!ImGui.BeginChild("##reprice-scroll", new System.Numerics.Vector2(avail.X, topHeight), false, ImGuiWindowFlags.HorizontalScrollbar))
        {
            ImGui.EndChild();
            return;
        }

        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        var snapshots = configuration.Snapshots.Values
            .OrderBy(s => s.CharacterName).ThenBy(s => s.RetainerName).ToList();

        foreach (var snap in snapshots)
        {
            var filteredListings = FilterListings(snap, sheet);
            if (filteredListings.Count == 0 && filter.Length > 0) continue;

            var header = string.Format(Strings.RetainerHeader, snap.RetainerName, filteredListings.Count, FreshnessSuffix(snap));
            if (!DrawCollapsingHeader("reprice-" + snap.Key, header)) continue;

            // 行 1: undercut input + fetch ボタン群
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("-");
            ImGui.SameLine(0, 2);
            ImGui.SetNextItemWidth(60);
            if (ImGui.InputInt($"##undercut-{snap.Key}", ref undercutDelta, 0))
            {
                if (undercutDelta < 0) undercutDelta = 0;
            }
            ImGui.SameLine(0, 4);
            ImGui.TextUnformatted("g");
            ImGui.SameLine();
            if (ImGui.SmallButton(Strings.RepriceMatchLowestThisRetainer + "##matchlowest-" + snap.Key))
            {
                var capturedSnap = snap;
                var capturedOffset = -undercutDelta;
                executor.OnFetchMarketCompleted = () => ApplyMatchLowestForRetainer(capturedSnap, offset: capturedOffset);
                executor.StartFetchMarketPricesForRetainer(snap.Key);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.RepriceMatchLowestTooltip);
            ImGui.SameLine();
            if (ImGui.SmallButton(Strings.RepriceMatchLowestExactThisRetainer + "##matchexact-" + snap.Key))
            {
                var capturedSnap = snap;
                executor.OnFetchMarketCompleted = () => ApplyMatchLowestForRetainer(capturedSnap, offset: 0);
                executor.StartFetchMarketPricesForRetainer(snap.Key);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.RepriceMatchLowestTooltip);
            ImGui.SameLine();
            if (ImGui.SmallButton(Strings.RepriceAddAllToPlan + "##addall-" + snap.Key))
            {
                AddAllEditedToPlans(snap, sheet);
            }

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

    /// <summary>fetch 完了後に editedPrice に「最安値 + offset」を入れる。プランには勝手に積まない。</summary>
    private void ApplyMatchLowestForRetainer(RetainerSnapshot snap, long offset = -1)
    {
        var seen = new HashSet<(uint, bool)>();
        var missing = new HashSet<(uint, bool, string)>();
        var applied = 0;
        var sheet = Plugin.DataManager.GetExcelSheet<Item>();

        Plugin.Log.Information($"[Restocker] reprice apply: retainer={snap.RetainerName} listings={snap.Listings.Count} offset={offset}");

        foreach (var listing in snap.Listings)
        {
            var name = sheet.TryGetRow(listing.ItemId, out var row) ? row.Name.ExtractText() : $"#{listing.ItemId}";
            if (listing.IsHQ) name += " (HQ)";
            if (filter.Length > 0 && !name.Contains(filter, System.StringComparison.OrdinalIgnoreCase)) continue;

            seen.Add((listing.ItemId, listing.IsHQ));
            if (!marketCache.HasData(listing.ItemId, listing.IsHQ))
            {
                missing.Add((listing.ItemId, listing.IsHQ, name));
                Plugin.Log.Information($"  slot={listing.ListingIndex} item={listing.ItemId} hq={listing.IsHQ} name='{name}' MISS (no cache)");
                continue;
            }
            var lowest = marketCache.GetLowest(listing.ItemId, listing.IsHQ);
            var current = listing.UnitPrice;
            if (lowest <= 0)
            {
                Plugin.Log.Information($"  slot={listing.ListingIndex} item={listing.ItemId} hq={listing.IsHQ} name='{name}' lowest={lowest} (skipped, no data)");
                continue;
            }
            var newPrice = lowest + offset;
            if (newPrice < 1) newPrice = 1;
            editedPrice[$"{snap.Key}#{listing.ListingIndex}"] = newPrice;
            applied++;
            Plugin.Log.Information($"  slot={listing.ListingIndex} item={listing.ItemId} hq={listing.IsHQ} name='{name}' current={current:N0} lowest={lowest:N0} offset={offset} -> new={newPrice:N0}");
        }

        if (missing.Count == 0)
            lastMatchSummary = $"{snap.RetainerName}: " + string.Format(Strings.MatchAppliedSummary, applied, seen.Count);
        else
            lastMatchSummary = $"{snap.RetainerName}: " + string.Format(Strings.MatchAppliedSummaryWithMissing, applied, seen.Count, missing.Count,
                string.Join(", ", missing.Take(5).Select(m => m.Item3)) + (missing.Count > 5 ? "…" : ""));
    }

    /// <summary>このリテイナーの editedPrice 全行をまとめて pendingPlans に追加。</summary>
    private void AddAllEditedToPlans(RetainerSnapshot snap, Lumina.Excel.ExcelSheet<Item> sheet)
    {
        var added = 0;
        foreach (var listing in snap.Listings)
        {
            var key = $"{snap.Key}#{listing.ListingIndex}";
            if (!editedPrice.TryGetValue(key, out var price)) continue;
            if (price <= 0) continue;
            if (price == listing.UnitPrice) continue; // 同額は積まない
            var name = sheet.TryGetRow(listing.ItemId, out var row) ? row.Name.ExtractText() : $"#{listing.ItemId}";
            if (listing.IsHQ) name += " (HQ)";
            UpsertPlan(new RepricePlan
            {
                RetainerKey = snap.Key,
                RetainerName = snap.RetainerName,
                ItemId = listing.ItemId,
                IsHQ = listing.IsHQ,
                ItemName = name,
                ListingIndex = listing.ListingIndex,
                OldPrice = listing.UnitPrice,
                NewPrice = price,
                Quantity = listing.Quantity,
            });
            added++;
        }
        Plugin.Log.Information($"[Restocker] reprice plan: added/updated {added} plans for {snap.RetainerName}");
    }

    /// <summary>同じ (retainer, slot) のプランは上書き、無ければ追加。</summary>
    private void UpsertPlan(RepricePlan plan)
    {
        for (var i = 0; i < pendingPlans.Count; i++)
        {
            var p = pendingPlans[i];
            if (p.RetainerKey == plan.RetainerKey && p.ListingIndex == plan.ListingIndex)
            {
                pendingPlans[i] = plan;
                return;
            }
        }
        pendingPlans.Add(plan);
    }

    private void RemovePlanIfExists(string retainerKey, int slot)
    {
        for (var i = 0; i < pendingPlans.Count; i++)
        {
            if (pendingPlans[i].RetainerKey == retainerKey && pendingPlans[i].ListingIndex == slot)
            {
                pendingPlans.RemoveAt(i);
                return;
            }
        }
    }

    private bool HasPlan(string retainerKey, int slot)
        => pendingPlans.Any(p => p.RetainerKey == retainerKey && p.ListingIndex == slot);

    private void DrawRetainerListings(RetainerSnapshot snap, List<(ListingEntry Entry, string ItemName)> rows)
    {
        const ImGuiTableFlags flags =
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp |
            ImGuiTableFlags.NoSavedSettings;

        // #, Item, Qty, Current, New, fill, +Add = 7
        if (!ImGui.BeginTable($"##reprice-{snap.Key}", 7, flags)) return;

        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 30);
        ImGui.TableSetupColumn(Strings.ColItem, ImGuiTableColumnFlags.WidthStretch, 0.42f);
        ImGui.TableSetupColumn(Strings.ColTotalQty, ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn(Strings.ColCurrentPrice, ImGuiTableColumnFlags.WidthStretch, 0.2f);
        ImGui.TableSetupColumn(Strings.ColNewPrice, ImGuiTableColumnFlags.WidthStretch, 0.2f);
        ImGui.TableSetupColumn("##fill", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("##add", ImGuiTableColumnFlags.WidthFixed, 60);
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
            ImGui.TableNextColumn();
            DrawFillButton(snap, l);
            ImGui.TableNextColumn();
            DrawAddPlanButton(snap, l, name);
        }

        ImGui.EndTable();
    }

    /// <summary>この行で入力された価格を、同じリテイナーの他の全 listing の editedPrice にコピー。</summary>
    private void DrawFillButton(RetainerSnapshot snap, ListingEntry sourceListing)
    {
        var sourceKey = $"{snap.Key}#{sourceListing.ListingIndex}";
        var sourcePrice = editedPrice.GetValueOrDefault(sourceKey, 0);
        var canFill = sourcePrice > 0;
        if (!canFill) ImGui.BeginDisabled();
        if (ImGui.SmallButton($"{Strings.RepriceFillRetainer}##fill-{sourceKey}"))
        {
            foreach (var l in snap.Listings)
            {
                if (l.ListingIndex == sourceListing.ListingIndex) continue;
                editedPrice[$"{snap.Key}#{l.ListingIndex}"] = sourcePrice;
            }
            Plugin.Log.Information($"[Restocker] fill: copied {sourcePrice} to {snap.Listings.Count - 1} other listings on {snap.RetainerName}");
        }
        if (!canFill) ImGui.EndDisabled();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.RepriceFillTooltip);
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

    private void DrawAddPlanButton(RetainerSnapshot snap, ListingEntry l, string name)
    {
        var key = $"{snap.Key}#{l.ListingIndex}";
        var price = editedPrice.GetValueOrDefault(key, 0);
        var canAdd = price > 0 && price != l.UnitPrice;

        if (HasPlan(snap.Key, l.ListingIndex))
        {
            // 既にプランにあれば「-」(削除) を出す
            if (ImGui.SmallButton($"-##rm-{key}"))
                RemovePlanIfExists(snap.Key, l.ListingIndex);
            return;
        }

        if (!canAdd) ImGui.BeginDisabled();
        if (ImGui.SmallButton($"{Strings.AddToPlan}##add-{key}"))
        {
            UpsertPlan(new RepricePlan
            {
                RetainerKey = snap.Key,
                RetainerName = snap.RetainerName,
                ItemId = l.ItemId,
                IsHQ = l.IsHQ,
                ItemName = name,
                ListingIndex = l.ListingIndex,
                OldPrice = l.UnitPrice,
                NewPrice = price,
                Quantity = l.Quantity,
            });
        }
        if (!canAdd) ImGui.EndDisabled();
    }

    private void DrawPendingPlans()
    {
        ImGui.TextUnformatted(string.Format(Strings.PendingPlansHeader, pendingPlans.Count));
        ImGui.SameLine();
        if (pendingPlans.Count > 0 && ImGui.SmallButton(Strings.ClearAllPlans + "##clear-reprice-plans"))
        {
            pendingPlans.Clear();
        }

        if (pendingPlans.Count == 0)
        {
            ImGui.TextDisabled(Strings.PendingPlansEmpty);
            return;
        }

        const ImGuiTableFlags flags =
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp |
            ImGuiTableFlags.NoSavedSettings;
        if (!ImGui.BeginTable("##reprice-pending", 6, flags)) return;
        ImGui.TableSetupColumn(Strings.ColItem, ImGuiTableColumnFlags.WidthStretch, 0.4f);
        ImGui.TableSetupColumn(Strings.ColTarget, ImGuiTableColumnFlags.WidthStretch, 0.2f);
        ImGui.TableSetupColumn(Strings.ColTotalQty, ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn(Strings.ColCurrentPrice, ImGuiTableColumnFlags.WidthStretch, 0.2f);
        ImGui.TableSetupColumn(Strings.ColNewPrice, ImGuiTableColumnFlags.WidthStretch, 0.2f);
        ImGui.TableSetupColumn("##rm", ImGuiTableColumnFlags.WidthFixed, 50);
        ImGui.TableHeadersRow();

        var removeIdx = -1;
        for (var i = 0; i < pendingPlans.Count; i++)
        {
            var p = pendingPlans[i];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(p.ItemName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(p.RetainerName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(p.Quantity.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(p.OldPrice.ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(p.NewPrice.ToString("N0"));
            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"{Strings.PendingPlanRemove}##rmp-{i}")) removeIdx = i;
        }
        ImGui.EndTable();

        if (removeIdx >= 0) pendingPlans.RemoveAt(removeIdx);
    }
}

internal sealed class RepricePlan
{
    public string RetainerKey = string.Empty;
    public string RetainerName = string.Empty;
    public uint ItemId;
    public bool IsHQ;
    public string ItemName = string.Empty;
    public int ListingIndex;
    public long OldPrice;
    public long NewPrice;
    public int Quantity;
}
