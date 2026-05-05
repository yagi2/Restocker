using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using Restocker.Data;
using Restocker.Execution;
using Restocker.Localization;
using Restocker.Plan;

namespace Restocker.Windows;

/// <summary>
/// 新規分割出品画面。**リテイナー毎の collapsing header** が top-level、
/// 加えてキャラ所持品とサドルバッグも同じ形でセクション化される。
/// 各セクション内のテーブルで価格を入れると、その場で Planner プレビュー。
/// </summary>
public sealed class ListTab
{
    private readonly Configuration configuration;
    private readonly Executor executor;
    private string filter = string.Empty;
    private bool listableOnly = true;
    /// <summary>編集中の価格。キーは "{sourceKey}#{itemId}.{hq}"。</summary>
    private readonly Dictionary<string, long> editedPrice = new();

    public ListTab(Configuration configuration, Executor executor)
    {
        this.configuration = configuration;
        this.executor = executor;
    }

    public void Draw()
    {
        DrawToolbar();
        ImGui.Separator();
        DrawSections();
    }

    private void DrawToolbar()
    {
        ImGui.SetNextItemWidth(240);
        ImGui.InputTextWithHint("##list-filter", Strings.Filter, ref filter, 64);
        ImGui.SameLine();
        ImGui.Checkbox(Strings.ListableOnly, ref listableOnly);
        ImGui.SameLine();
        ImGui.TextDisabled($"{Strings.HQNQNote} / {Strings.NoTransfer}");

        // 適用ボタン
        ImGui.SameLine();
        DrawApplyButton();
    }

    private void DrawApplyButton()
    {
        // ListTab の適用は各リテイナーセクションで価格入力された行に対し Planner.PlanNewListings で展開
        var actions = BuildPlannedActions();
        var disabled = actions.Count == 0 || executor.IsRunning;
        if (disabled) ImGui.BeginDisabled();
        if (ImGui.Button(string.Format(Strings.ApplyWithCount, Strings.Apply, actions.Count)))
        {
            executor.StartApplyActions(actions);
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
        // リテイナーソースに対しては該当リテイナーのみ Planner にかける
        foreach (var snap in configuration.Snapshots.Values)
        {
            foreach (var entry in DistinctItems(snap.Inventory))
            {
                var key = MakePriceKey(snap.Key, entry.ItemId, entry.IsHQ);
                if (!editedPrice.TryGetValue(key, out var price) || price <= 0) continue;
                result.AddRange(Planner.PlanNewListings(new[] { snap }, entry.ItemId, entry.IsHQ, price, entry.MaxStackPerListing));
            }
        }
        // キャラ所持品/サドルは Entrust ステップが要るので 0.1.x ではプランに含めない
        // (Executor 側でも NewListing は skip。char inventory 経由の出品は次フェーズ)
        return result;
    }

    private static IEnumerable<InventoryEntry> DistinctItems(IEnumerable<InventoryEntry> entries)
    {
        var seen = new HashSet<(uint, bool)>();
        foreach (var e in entries)
        {
            if (seen.Add((e.ItemId, e.IsHQ))) yield return e;
        }
    }

    private static string MakePriceKey(string sourceKey, uint itemId, bool isHQ)
        => $"{sourceKey}#{itemId}.{(isHQ ? 1 : 0)}";

    private void DrawSections()
    {
        if (configuration.Snapshots.Count == 0 && configuration.Characters.Count == 0)
        {
            ImGui.TextDisabled(Strings.EmptyHint);
            return;
        }

        var size = ImGui.GetContentRegionAvail();
        if (!ImGui.BeginChild("##list-scroll", size, false, ImGuiWindowFlags.HorizontalScrollbar))
        {
            ImGui.EndChild();
            return;
        }

        // リテイナー側
        var snapshots = configuration.Snapshots.Values
            .OrderBy(s => s.CharacterName).ThenBy(s => s.RetainerName).ToList();
        foreach (var snap in snapshots)
        {
            DrawRetainerSection(snap);
        }

        // キャラ側
        foreach (var ch in configuration.Characters.Values.OrderBy(c => c.CharacterName))
        {
            DrawCharacterSection(ch);
        }

        ImGui.EndChild();
    }

    private void DrawRetainerSection(RetainerSnapshot snap)
    {
        var rows = AggregatePerSource(snap.Inventory, snap.Listings);
        var filtered = ApplyFilter(rows);
        if (filtered.Count == 0 && filter.Length > 0) return;

        var header = string.Format(Strings.RetainerHeaderInventory, snap.RetainerName, filtered.Count, FreshnessSuffix(snap.LastRefreshedUtc));
        if (!ImGui.CollapsingHeader(header + "##list-" + snap.Key, ImGuiTreeNodeFlags.DefaultOpen)) return;

        DrawRowsTable(snap.Key, filtered, sourceLabel: null, isRetainerSource: true);
    }

    private void DrawCharacterSection(CharacterSnapshot ch)
    {
        var sourceKey = CharacterSnapshot.MakeKey(ch.CharacterContentId);

        // bag
        var bagRows = AggregatePerSource(ch.Bag, listingSource: null);
        var bagFiltered = ApplyFilter(bagRows);
        if (bagFiltered.Count > 0 || filter.Length == 0)
        {
            var header = string.Format(Strings.RetainerHeaderInventory, $"{Strings.CharacterInventoryHeader} ({ch.CharacterName})", bagFiltered.Count, FreshnessSuffix(ch.LastRefreshedUtc));
            if (ImGui.CollapsingHeader(header + "##char-" + sourceKey))
            {
                DrawRowsTable(sourceKey + ".bag", bagFiltered, Strings.CharacterInventoryHeader, isRetainerSource: false);
            }
        }

        // saddlebag
        var saddleRows = AggregatePerSource(ch.Saddlebag.Concat(ch.PremiumSaddlebag).ToList(), listingSource: null);
        var saddleFiltered = ApplyFilter(saddleRows);
        if (saddleFiltered.Count > 0)
        {
            var header = string.Format(Strings.RetainerHeaderInventory, $"{Strings.SaddlebagHeader} ({ch.CharacterName})", saddleFiltered.Count, FreshnessSuffix(ch.LastRefreshedUtc));
            if (ImGui.CollapsingHeader(header + "##saddle-" + sourceKey))
            {
                DrawRowsTable(sourceKey + ".saddle", saddleFiltered, Strings.SaddlebagHeader, isRetainerSource: false);
            }
        }
    }

    private sealed class Row
    {
        public required uint ItemId;
        public required bool IsHQ;
        public required string ItemName;
        public required int Qty;
        public required int MaxStack;
        public required bool IsListable;
        public required int CurrentlyListedQty; // リテイナーソースでのみ意味あり
    }

    private List<Row> AggregatePerSource(List<InventoryEntry> inventory, List<ListingEntry>? listingSource)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        var rows = new Dictionary<(uint, bool), Row>();
        foreach (var e in inventory)
        {
            var key = (e.ItemId, e.IsHQ);
            if (!rows.TryGetValue(key, out var r))
            {
                var name = sheet.TryGetRow(e.ItemId, out var row) ? row.Name.ExtractText() : $"#{e.ItemId}";
                if (e.IsHQ) name += " (HQ)";
                r = new Row
                {
                    ItemId = e.ItemId,
                    IsHQ = e.IsHQ,
                    ItemName = name,
                    Qty = 0,
                    MaxStack = e.MaxStackPerListing,
                    IsListable = e.IsListable,
                    CurrentlyListedQty = 0,
                };
                rows[key] = r;
            }
            r.Qty += e.Quantity;
        }
        if (listingSource != null)
        {
            foreach (var l in listingSource)
            {
                var key = (l.ItemId, l.IsHQ);
                if (rows.TryGetValue(key, out var r)) r.CurrentlyListedQty += l.Quantity;
            }
        }
        return rows.Values.OrderByDescending(r => r.Qty).ToList();
    }

    private List<Row> ApplyFilter(List<Row> rows)
    {
        return rows.Where(r =>
        {
            if (listableOnly && !r.IsListable) return false;
            if (filter.Length > 0 && !r.ItemName.Contains(filter, System.StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }).ToList();
    }

    private static string FreshnessSuffix(System.DateTime utc)
    {
        if (utc == System.DateTime.MinValue) return Strings.HeaderUnknown;
        var age = System.DateTime.UtcNow - utc;
        if (age.TotalMinutes < 1) return "now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours}h";
        return $"{(int)age.TotalDays}d";
    }

    private void DrawRowsTable(string sourceKey, List<Row> rows, string? sourceLabel, bool isRetainerSource)
    {
        const ImGuiTableFlags flags =
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp |
            ImGuiTableFlags.NoSavedSettings;

        if (!ImGui.BeginTable($"##list-table-{sourceKey}", isRetainerSource ? 6 : 4, flags)) return;

        ImGui.TableSetupColumn(Strings.ColItem, ImGuiTableColumnFlags.WidthStretch, 0.4f);
        ImGui.TableSetupColumn(Strings.ColOwned, ImGuiTableColumnFlags.WidthFixed, 70);
        if (isRetainerSource)
            ImGui.TableSetupColumn(Strings.ColListed, ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn(Strings.ColMaxStack, ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn(Strings.ColPrice, ImGuiTableColumnFlags.WidthStretch, 0.2f);
        if (isRetainerSource)
            ImGui.TableSetupColumn(Strings.ColPlan, ImGuiTableColumnFlags.WidthStretch, 0.3f);
        ImGui.TableHeadersRow();

        foreach (var r in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (r.IsListable) ImGui.TextUnformatted(r.ItemName);
            else ImGui.TextDisabled(r.ItemName + Strings.Unsellable);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.Qty.ToString());

            if (isRetainerSource)
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(r.CurrentlyListedQty == 0 ? "—" : r.CurrentlyListedQty.ToString());
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.MaxStack.ToString());

            ImGui.TableNextColumn();
            DrawPriceInput(MakePriceKey(sourceKey, r.ItemId, r.IsHQ));

            if (isRetainerSource)
            {
                ImGui.TableNextColumn();
                DrawPlanCell(sourceKey, r);
            }
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

    private void DrawPlanCell(string sourceKey, Row r)
    {
        var key = MakePriceKey(sourceKey, r.ItemId, r.IsHQ);
        var price = editedPrice.GetValueOrDefault(key, 0);
        if (price <= 0) { ImGui.TextDisabled("—"); return; }
        if (!r.IsListable) { ImGui.TextDisabled(Strings.Unsellable); return; }

        if (!configuration.Snapshots.TryGetValue(sourceKey, out var snap)) { ImGui.TextDisabled("—"); return; }
        var plan = Planner.PlanNewListings(new[] { snap }, r.ItemId, r.IsHQ, price, r.MaxStack);
        var overflow = Planner.Overflow(new[] { snap }, plan, r.ItemId, r.IsHQ);
        var summary = string.Format(Strings.PlanCount, plan.Count);
        if (overflow > 0)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.55f, 0.2f, 1f),
                summary + " " + string.Format(Strings.PlanOverflow, overflow));
        }
        else
        {
            ImGui.TextUnformatted(summary);
        }
    }
}
