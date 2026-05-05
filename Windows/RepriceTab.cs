using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using Restocker.Data;
using Restocker.Localization;

namespace Restocker.Windows;

/// <summary>
/// 出品中アイテムの一括リプライス画面。
/// 既定はアイテム単位（ItemId × HQ）で集約 1 行、▶ で子行（リテイナー個別の各 listing）を展開。
/// </summary>
public sealed class RepriceTab
{
    private readonly Configuration configuration;
    private string filter = string.Empty;
    private readonly Dictionary<string, long> editedAggregatePrice = new();
    private readonly Dictionary<string, long> editedListingPrice = new();
    private readonly HashSet<string> expanded = new();

    public RepriceTab(Configuration configuration)
    {
        this.configuration = configuration;
    }

    public void Draw()
    {
        DrawToolbar();
        ImGui.Separator();
        DrawTable();
        ImGui.Separator();
        DrawApplyButton();
    }

    private void DrawToolbar()
    {
        ImGui.SetNextItemWidth(240);
        ImGui.InputTextWithHint("##reprice-filter", Strings.Filter, ref filter, 64);
        ImGui.SameLine();
        ImGui.BeginDisabled();
        ImGui.Button(Strings.RepriceMatchLowest);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled(Strings.HQNQNote);
    }

    private record AggregateKey(uint ItemId, bool IsHQ);

    private List<(AggregateKey Key, string ItemName, int TotalQty, int ListingCount, List<(RetainerSnapshot Snap, ListingEntry Listing)> Children)>
        BuildAggregateRows()
    {
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        var groups = configuration.Snapshots.Values
            .SelectMany(s => s.Listings.Select(l => (Snap: s, Listing: l)))
            .GroupBy(t => new AggregateKey(t.Listing.ItemId, t.Listing.IsHQ));

        var rows = new List<(AggregateKey, string, int, int, List<(RetainerSnapshot, ListingEntry)>)>();
        foreach (var grp in groups)
        {
            var name = itemSheet.TryGetRow(grp.Key.ItemId, out var row) ? row.Name.ExtractText() : $"#{grp.Key.ItemId}";
            if (grp.Key.IsHQ) name += " (HQ)";

            if (filter.Length > 0 && !name.Contains(filter, System.StringComparison.OrdinalIgnoreCase))
                continue;

            var children = grp.OrderBy(t => t.Snap.RetainerName).ThenBy(t => t.Listing.ListingIndex).ToList();
            var totalQty = children.Sum(t => t.Listing.Quantity);
            var listingCount = children.Count;
            rows.Add((grp.Key, name, totalQty, listingCount, children));
        }
        return rows.OrderBy(r => r.Item2).ToList();
    }

    private void DrawTable()
    {
        if (configuration.Snapshots.Count == 0)
        {
            ImGui.TextDisabled(Strings.EmptyHint);
            return;
        }

        const ImGuiTableFlags flags =
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp |
            ImGuiTableFlags.ScrollY | ImGuiTableFlags.NoSavedSettings;

        if (!ImGui.BeginTable("##reprice-table", 6, flags, new System.Numerics.Vector2(0, 280)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(Strings.ColExpand, ImGuiTableColumnFlags.WidthFixed, 28);
        ImGui.TableSetupColumn(Strings.ColItem, ImGuiTableColumnFlags.WidthStretch, 0.4f);
        ImGui.TableSetupColumn(Strings.ColTotalQty, ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn(Strings.ColListings, ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn(Strings.ColCurrentPrice, ImGuiTableColumnFlags.WidthStretch, 0.25f);
        ImGui.TableSetupColumn(Strings.ColNewPrice, ImGuiTableColumnFlags.WidthStretch, 0.2f);
        ImGui.TableHeadersRow();

        var rows = BuildAggregateRows();
        foreach (var (key, name, totalQty, listingCount, children) in rows)
        {
            var aggKey = $"{key.ItemId}.{(key.IsHQ ? 1 : 0)}";
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var isExpanded = expanded.Contains(aggKey);
            if (ImGui.SmallButton((isExpanded ? "-" : "+") + "##" + aggKey))
            {
                if (isExpanded) expanded.Remove(aggKey);
                else expanded.Add(aggKey);
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(name);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(totalQty.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(listingCount.ToString());

            ImGui.TableNextColumn();
            DrawCurrentPriceCell(children);

            ImGui.TableNextColumn();
            DrawAggregatePriceInput(aggKey);

            if (isExpanded)
            {
                foreach (var (snap, listing) in children)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TableNextColumn();
                    ImGui.TextDisabled($"  {snap.RetainerName} #{listing.ListingIndex}");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(listing.Quantity.ToString());
                    ImGui.TableNextColumn();
                    ImGui.TextDisabled("—");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(listing.UnitPrice == 0 ? Strings.PriceUnknown : listing.UnitPrice.ToString("N0"));
                    ImGui.TableNextColumn();
                    DrawListingPriceInput(snap.Key, listing.ListingIndex);
                }
            }
        }

        ImGui.EndTable();
    }

    private static void DrawCurrentPriceCell(List<(RetainerSnapshot Snap, ListingEntry Listing)> children)
    {
        var prices = children.Select(c => c.Listing.UnitPrice).Where(p => p > 0).Distinct().OrderBy(p => p).ToList();
        if (prices.Count == 0) ImGui.TextDisabled(Strings.PriceUnknown);
        else if (prices.Count == 1) ImGui.TextUnformatted(prices[0].ToString("N0"));
        else ImGui.TextUnformatted($"{prices[0]:N0} - {prices[^1]:N0}");
    }

    private void DrawAggregatePriceInput(string aggKey)
    {
        var v = (int)editedAggregatePrice.GetValueOrDefault(aggKey, 0);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputInt($"##agg-price-{aggKey}", ref v, 0))
        {
            if (v < 0) v = 0;
            editedAggregatePrice[aggKey] = v;
        }
    }

    private void DrawListingPriceInput(string retainerKey, int listingIndex)
    {
        var key = $"{retainerKey}#{listingIndex}";
        var v = (int)editedListingPrice.GetValueOrDefault(key, 0);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputInt($"##list-price-{key}", ref v, 0))
        {
            if (v < 0) v = 0;
            editedListingPrice[key] = v;
        }
    }

    private void DrawApplyButton()
    {
        var aggCount = editedAggregatePrice.Count(kv => kv.Value > 0);
        var listCount = editedListingPrice.Count(kv => kv.Value > 0);
        var total = aggCount + listCount;

        ImGui.TextUnformatted(string.Format(Strings.EditedSummary, aggCount, listCount));
        ImGui.SameLine();
        ImGui.BeginDisabled();
        ImGui.Button($"{Strings.Apply} {Strings.NotImplemented} ({total})");
        ImGui.EndDisabled();
    }
}
