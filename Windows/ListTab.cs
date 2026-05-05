using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using Restocker.Data;
using Restocker.Localization;
using Restocker.Plan;

namespace Restocker.Windows;

/// <summary>
/// 新規分割出品画面。リテイナーの所持品を横断アイテム単位で表示し、
/// 価格を入力すると Planner が分配計画をその場で計算してプレビューする。
/// </summary>
public sealed class ListTab
{
    private readonly Configuration configuration;
    private string filter = string.Empty;
    private bool listableOnly = true;
    private readonly Dictionary<string, long> editedPrice = new();

    public ListTab(Configuration configuration)
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
        ImGui.InputTextWithHint("##list-filter", Strings.Filter, ref filter, 64);
        ImGui.SameLine();
        ImGui.Checkbox(Strings.ListableOnly, ref listableOnly);
        ImGui.SameLine();
        ImGui.TextDisabled($"{Strings.HQNQNote} / {Strings.NoTransfer}");
    }

    private record AggKey(uint ItemId, bool IsHQ);

    private sealed class AggregatedRow
    {
        public required AggKey Key;
        public required string ItemName;
        public required int TotalInventoryQty;
        public required int MaxStackPerListing;
        public required bool IsListable;
        public required int CurrentlyListedQty;
        public required List<RetainerSnapshot> SourceSnapshots;
    }

    private List<AggregatedRow> BuildRows()
    {
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();

        var groups = configuration.Snapshots.Values
            .SelectMany(s => s.Inventory.Select(e => (Snap: s, Entry: e)))
            .GroupBy(t => new AggKey(t.Entry.ItemId, t.Entry.IsHQ));

        var rows = new List<AggregatedRow>();
        foreach (var grp in groups)
        {
            var first = grp.First().Entry;
            var name = itemSheet.TryGetRow(grp.Key.ItemId, out var row) ? row.Name.ExtractText() : $"#{grp.Key.ItemId}";
            if (grp.Key.IsHQ) name += " (HQ)";

            if (filter.Length > 0 && !name.Contains(filter, System.StringComparison.OrdinalIgnoreCase))
                continue;
            if (listableOnly && !first.IsListable) continue;

            var listedQty = configuration.Snapshots.Values
                .SelectMany(s => s.Listings)
                .Where(l => l.ItemId == grp.Key.ItemId && l.IsHQ == grp.Key.IsHQ)
                .Sum(l => l.Quantity);

            rows.Add(new AggregatedRow
            {
                Key = grp.Key,
                ItemName = name,
                TotalInventoryQty = grp.Sum(t => t.Entry.Quantity),
                MaxStackPerListing = first.MaxStackPerListing,
                IsListable = first.IsListable,
                CurrentlyListedQty = listedQty,
                SourceSnapshots = grp.Select(t => t.Snap).Distinct().ToList(),
            });
        }
        return rows.OrderByDescending(r => r.TotalInventoryQty).ToList();
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

        if (!ImGui.BeginTable("##list-table", 6, flags, new Vector2(0, 280)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(Strings.ColItem, ImGuiTableColumnFlags.WidthStretch, 0.4f);
        ImGui.TableSetupColumn(Strings.ColOwned, ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn(Strings.ColListed, ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn(Strings.ColMaxStack, ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn(Strings.ColPrice, ImGuiTableColumnFlags.WidthStretch, 0.2f);
        ImGui.TableSetupColumn(Strings.ColPlan, ImGuiTableColumnFlags.WidthStretch, 0.3f);
        ImGui.TableHeadersRow();

        foreach (var row in BuildRows())
        {
            var rowKey = $"{row.Key.ItemId}.{(row.Key.IsHQ ? 1 : 0)}";
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            if (row.IsListable) ImGui.TextUnformatted(row.ItemName);
            else ImGui.TextDisabled(row.ItemName + Strings.Unsellable);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.TotalInventoryQty.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.CurrentlyListedQty == 0 ? "—" : row.CurrentlyListedQty.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.MaxStackPerListing.ToString());

            ImGui.TableNextColumn();
            DrawPriceInput(rowKey);

            ImGui.TableNextColumn();
            DrawPlanCell(row, rowKey);
        }

        ImGui.EndTable();
    }

    private void DrawPriceInput(string rowKey)
    {
        var v = (int)editedPrice.GetValueOrDefault(rowKey, 0);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputInt($"##list-price-{rowKey}", ref v, 0))
        {
            if (v < 0) v = 0;
            editedPrice[rowKey] = v;
        }
    }

    private void DrawPlanCell(AggregatedRow row, string rowKey)
    {
        var price = editedPrice.GetValueOrDefault(rowKey, 0);
        if (price <= 0)
        {
            ImGui.TextDisabled("—");
            return;
        }
        if (!row.IsListable)
        {
            ImGui.TextDisabled(Strings.Unsellable);
            return;
        }

        var plan = Planner.PlanNewListings(row.SourceSnapshots, row.Key.ItemId, row.Key.IsHQ, price, row.MaxStackPerListing);
        var overflow = Planner.Overflow(row.SourceSnapshots, plan, row.Key.ItemId, row.Key.IsHQ);
        var byRetainer = plan.GroupBy(p => p.RetainerKey).Select(g => $"{ShortRetainerName(g.Key)}={g.Count()}").ToList();
        var summary = string.Format(Strings.PlanCount, plan.Count);
        if (byRetainer.Count > 0) summary += " (" + string.Join(", ", byRetainer) + ")";

        if (overflow > 0)
        {
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.2f, 1f), summary + " " + string.Format(Strings.PlanOverflow, overflow));
        }
        else
        {
            ImGui.TextUnformatted(summary);
        }
    }

    private string ShortRetainerName(string retainerKey)
    {
        if (configuration.Snapshots.TryGetValue(retainerKey, out var s) && s.RetainerName.Length > 0)
            return s.RetainerName;
        return retainerKey;
    }

    private void DrawApplyButton()
    {
        var rowsWithPrice = editedPrice.Count(kv => kv.Value > 0);
        ImGui.TextUnformatted(string.Format(Strings.ListSummary, rowsWithPrice));
        ImGui.SameLine();
        ImGui.BeginDisabled();
        ImGui.Button($"{Strings.Apply} {Strings.NotImplemented}");
        ImGui.EndDisabled();
    }
}
