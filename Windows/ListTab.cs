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
/// 新規分割出品画面。各行で「価格・個数・枠数・出品先」を指定して
/// 「+追加」ボタンを押すとプランリストに積まれる。最後に「適用」で
/// プランリストを一括実行。プランは下部のテーブルから個別削除可。
/// </summary>
public sealed class ListTab
{
    private readonly Configuration configuration;
    private readonly Executor executor;
    private readonly ConfirmDialog confirmDialog;
    private string filter = string.Empty;
    private bool listableOnly = true;

    /// <summary>編集中の値。キーは "{sourceKey}#{itemId}.{hq}"。</summary>
    private readonly Dictionary<string, long> editedPrice = new();
    private readonly Dictionary<string, int> editedQtyPer = new();
    private readonly Dictionary<string, int> editedSlots = new();
    /// <summary>行毎に target retainer。retainer source は自身固定なので未使用。</summary>
    private readonly Dictionary<string, string> editedTarget = new();
    private readonly HashSet<string> expandedSections = new();

    /// <summary>プランリスト。+追加で積まれ、適用で一括実行 → クリア。</summary>
    private readonly List<PendingPlan> pendingPlans = new();

    public ListTab(Configuration configuration, Executor executor, ConfirmDialog confirmDialog)
    {
        this.configuration = configuration;
        this.executor = executor;
        this.confirmDialog = confirmDialog;
    }

    public void Draw()
    {
        DrawToolbar();
        ImGui.Separator();
        DrawSections();
        ImGui.Separator();
        DrawPendingPlans();
    }

    private void DrawToolbar()
    {
        ImGui.SetNextItemWidth(240);
        ImGui.InputTextWithHint("##list-filter", Strings.Filter, ref filter, 64);
        ImGui.SameLine();
        ImGui.Checkbox(Strings.ListableOnly, ref listableOnly);
        ImGui.SameLine();
        if (ImGui.SmallButton(Strings.CollapseAll + "##list-collapse"))
        {
            expandedSections.Clear();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton(Strings.ExpandAll + "##list-expand"))
        {
            foreach (var snap in configuration.Snapshots.Values)
                expandedSections.Add("list-" + snap.Key);
            foreach (var ch in configuration.Characters.Values)
            {
                var k = CharacterSnapshot.MakeKey(ch.CharacterContentId);
                expandedSections.Add("char-" + k);
                expandedSections.Add("saddle-" + k);
            }
        }
        ImGui.SameLine();
        DrawApplyButton();
    }

    private void DrawApplyButton()
    {
        var totalActions = pendingPlans.Sum(p => ExpandPlanToActions(p).Count);
        var disabled = totalActions == 0 || executor.IsRunning;
        if (disabled) ImGui.BeginDisabled();
        if (ImGui.Button(string.Format(Strings.ApplyWithCount, Strings.Apply, totalActions)))
        {
            Plugin.Instance?.RetainerWatcher.CaptureCharacterSnapshot();
            var actions = pendingPlans.SelectMany(ExpandPlanToActions).ToList();
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

    /// <summary>1 つの PendingPlan を実行可能な PlannedAction list に展開。</summary>
    private List<PlannedAction> ExpandPlanToActions(PendingPlan p)
    {
        if (!configuration.Snapshots.TryGetValue(p.TargetKey, out var target)) return new List<PlannedAction>();

        // SourceKey が retainer key の場合は PlanNewListings、char.* なら PlanFromInventoryList
        if (p.SourceKey == p.TargetKey && configuration.Snapshots.TryGetValue(p.SourceKey, out var srcSnap))
        {
            return Planner.PlanNewListings(
                new[] { srcSnap }, p.ItemId, p.IsHQ, p.UnitPrice, p.MaxStack,
                listingsCap: p.Slots, perListingQty: p.QtyPer);
        }
        var inv = ResolveCharSourceInventory(p.SourceKey);
        if (inv == null) return new List<PlannedAction>();
        return Planner.PlanFromInventoryList(
            inv, p.SourceKey, target, p.ItemId, p.IsHQ, p.UnitPrice, p.MaxStack,
            listingsCap: p.Slots, perListingQty: p.QtyPer);
    }

    private List<InventoryEntry>? ResolveCharSourceInventory(string sourceKey)
    {
        // sourceKey: "char.{contentId:X}.bag" or "char.{contentId:X}.saddle"
        foreach (var ch in configuration.Characters.Values)
        {
            var prefix = CharacterSnapshot.MakeKey(ch.CharacterContentId);
            if (sourceKey == prefix + ".bag") return ch.Bag;
            if (sourceKey == prefix + ".saddle") return ch.Saddlebag.Concat(ch.PremiumSaddlebag).ToList();
        }
        return null;
    }

    private (int? qtyPer, int? slots) ResolveLots(string priceKey)
    {
        int? qtyPer = editedQtyPer.TryGetValue(priceKey, out var q) ? q : (int?)null;
        int? slots = editedSlots.TryGetValue(priceKey, out var s) ? s : (int?)null;
        return (qtyPer, slots);
    }

    /// <summary>retainer source なら自身、char source なら editedTarget の値。target retainer の空き枠数。</summary>
    private int ResolveFreeListingSlots(string sourceKey, string priceKey)
    {
        var targetKey = ResolveTargetKey(sourceKey, priceKey);
        if (string.IsNullOrEmpty(targetKey)) return 0;
        if (configuration.Snapshots.TryGetValue(targetKey, out var snap))
            return System.Math.Max(0, Planner.MaxListingSlots - snap.Listings.Count);
        return 0;
    }

    /// <summary>retainer source 行は sourceKey 自身、char source 行は editedTarget[priceKey]。</summary>
    private string ResolveTargetKey(string sourceKey, string priceKey)
    {
        if (configuration.Snapshots.ContainsKey(sourceKey)) return sourceKey;
        return editedTarget.TryGetValue(priceKey, out var t) ? t : string.Empty;
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

        // 上部: アイテム一覧 (高さの 60%)
        var avail = ImGui.GetContentRegionAvail();
        var topHeight = System.Math.Max(120f, avail.Y * 0.6f);
        if (!ImGui.BeginChild("##list-scroll", new System.Numerics.Vector2(avail.X, topHeight), false, ImGuiWindowFlags.HorizontalScrollbar))
        {
            ImGui.EndChild();
            return;
        }

        var snapshots = configuration.Snapshots.Values
            .OrderBy(s => s.CharacterName).ThenBy(s => s.RetainerName).ToList();
        foreach (var snap in snapshots)
        {
            DrawRetainerSection(snap);
        }
        foreach (var ch in configuration.Characters.Values.OrderBy(c => c.CharacterName))
        {
            DrawCharacterSection(ch);
        }

        ImGui.EndChild();
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

    private void DrawRetainerSection(RetainerSnapshot snap)
    {
        var rows = AggregatePerSource(snap.Inventory, snap.Listings);
        var filtered = ApplyFilter(rows);
        if (filtered.Count == 0 && filter.Length > 0) return;

        var header = string.Format(Strings.RetainerHeaderInventory, snap.RetainerName, filtered.Count, FreshnessSuffix(snap.LastRefreshedUtc));
        if (!DrawCollapsingHeader("list-" + snap.Key, header)) return;

        DrawRowsTable(snap.Key, filtered, isRetainerSource: true);
    }

    private void DrawCharacterSection(CharacterSnapshot ch)
    {
        var sourceKey = CharacterSnapshot.MakeKey(ch.CharacterContentId);

        var bagRows = AggregatePerSource(ch.Bag, listingSource: null);
        var bagFiltered = ApplyFilter(bagRows);
        if (bagFiltered.Count > 0 || filter.Length == 0)
        {
            var header = string.Format(Strings.RetainerHeaderInventory, $"{Strings.CharacterInventoryHeader} ({ch.CharacterName})", bagFiltered.Count, FreshnessSuffix(ch.LastRefreshedUtc));
            if (DrawCollapsingHeader("char-" + sourceKey, header))
            {
                DrawRowsTable(sourceKey + ".bag", bagFiltered, isRetainerSource: false);
            }
        }

        var saddleRows = AggregatePerSource(ch.Saddlebag.Concat(ch.PremiumSaddlebag).ToList(), listingSource: null);
        var saddleFiltered = ApplyFilter(saddleRows);
        if (saddleFiltered.Count > 0 || filter.Length == 0)
        {
            var header = string.Format(Strings.RetainerHeaderInventory, $"{Strings.SaddlebagHeader} ({ch.CharacterName})", saddleFiltered.Count, FreshnessSuffix(ch.LastRefreshedUtc));
            if (DrawCollapsingHeader("saddle-" + sourceKey, header))
            {
                DrawRowsTable(sourceKey + ".saddle", saddleFiltered, isRetainerSource: false);
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
        public required int CurrentlyListedQty;
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

    private void DrawRowsTable(string sourceKey, List<Row> rows, bool isRetainerSource)
    {
        const ImGuiTableFlags flags =
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp |
            ImGuiTableFlags.NoSavedSettings;

        // Item, Owned, (Listed), MaxStack, Price, LotsConfig, Target, AddBtn = 7 or 8
        var cols = isRetainerSource ? 8 : 7;
        if (!ImGui.BeginTable($"##list-table-{sourceKey}", cols, flags)) return;

        ImGui.TableSetupColumn(Strings.ColItem, ImGuiTableColumnFlags.WidthStretch, 0.35f);
        ImGui.TableSetupColumn(Strings.ColOwned, ImGuiTableColumnFlags.WidthFixed, 60);
        if (isRetainerSource)
            ImGui.TableSetupColumn(Strings.ColListed, ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn(Strings.ColMaxStack, ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn(Strings.ColPrice, ImGuiTableColumnFlags.WidthStretch, 0.2f);
        ImGui.TableSetupColumn(Strings.ColLotsConfig, ImGuiTableColumnFlags.WidthFixed, 140);
        ImGui.TableSetupColumn(Strings.ColTarget, ImGuiTableColumnFlags.WidthStretch, 0.25f);
        ImGui.TableSetupColumn("##add", ImGuiTableColumnFlags.WidthFixed, 70);
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
            DrawPriceCell(sourceKey, r);

            ImGui.TableNextColumn();
            DrawLotsCell(sourceKey, r);

            ImGui.TableNextColumn();
            DrawTargetCell(sourceKey, r, isRetainerSource);

            ImGui.TableNextColumn();
            DrawAddButton(sourceKey, r, isRetainerSource);
        }

        ImGui.EndTable();
    }

    private void DrawPriceCell(string sourceKey, Row r)
    {
        var key = MakePriceKey(sourceKey, r.ItemId, r.IsHQ);
        var v = (int)editedPrice.GetValueOrDefault(key, 0);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputInt($"##price-{key}", ref v, 0))
        {
            if (v < 0) v = 0;
            editedPrice[key] = v;
        }
    }

    private void DrawLotsCell(string sourceKey, Row r)
    {
        var key = MakePriceKey(sourceKey, r.ItemId, r.IsHQ);
        if (!editedQtyPer.ContainsKey(key)) editedQtyPer[key] = 0;
        if (!editedSlots.ContainsKey(key)) editedSlots[key] = 0;

        var qty = editedQtyPer[key];
        var slots = editedSlots[key];

        ImGui.SetNextItemWidth(50);
        if (ImGui.InputInt($"##qty-{key}", ref qty, 0))
        {
            if (qty < 0) qty = 0;
            if (qty > r.MaxStack) qty = r.MaxStack;
            editedQtyPer[key] = qty;
        }
        ImGui.SameLine(0, 4);
        ImGui.TextUnformatted("x");
        ImGui.SameLine(0, 4);
        ImGui.SetNextItemWidth(50);
        if (ImGui.InputInt($"##slots-{key}", ref slots, 0))
        {
            if (slots < 0) slots = 0;
            if (slots > Planner.MaxListingSlots) slots = Planner.MaxListingSlots;
            editedSlots[key] = slots;
        }

        ImGui.SameLine(0, 4);
        var maxDisabled = r.Qty <= 0;
        if (maxDisabled) ImGui.BeginDisabled();
        if (ImGui.SmallButton($"MAX##max-{key}"))
        {
            var qPer = System.Math.Min(r.MaxStack, r.Qty);
            if (qPer < 1) qPer = r.MaxStack;
            var freeSlots = ResolveFreeListingSlots(sourceKey, key);
            var neededSlots = qPer > 0 ? (r.Qty + qPer - 1) / qPer : 0;
            var targetSlots = System.Math.Min(freeSlots, neededSlots);
            if (targetSlots < 0) targetSlots = 0;
            editedQtyPer[key] = qPer;
            editedSlots[key] = targetSlots;
        }
        if (maxDisabled) ImGui.EndDisabled();
    }

    /// <summary>retainer source は「(自身)」表示、char source は target retainer の Combo。</summary>
    private void DrawTargetCell(string sourceKey, Row r, bool isRetainerSource)
    {
        if (isRetainerSource)
        {
            if (configuration.Snapshots.TryGetValue(sourceKey, out var self))
                ImGui.TextDisabled(self.RetainerName + " " + Strings.TargetSelf);
            else
                ImGui.TextDisabled(Strings.TargetSelf);
            return;
        }

        var key = MakePriceKey(sourceKey, r.ItemId, r.IsHQ);
        var retainers = configuration.Snapshots.Values.OrderBy(s => s.RetainerName).ToList();
        if (retainers.Count == 0)
        {
            ImGui.TextDisabled(Strings.CharBagNeedRetainerSnapshot);
            return;
        }

        var current = editedTarget.GetValueOrDefault(key, string.Empty);
        var labels = new List<string> { Strings.CharBagPickTarget };
        var keys = new List<string> { string.Empty };
        foreach (var ret in retainers)
        {
            var freeSlots = Planner.MaxListingSlots - ret.Listings.Count;
            labels.Add($"{ret.RetainerName} ({freeSlots} {Strings.FreeSlots})");
            keys.Add(ret.Key);
        }
        var idx = keys.IndexOf(current);
        if (idx < 0) idx = 0;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo($"##target-{key}", ref idx, labels.ToArray(), labels.Count))
        {
            editedTarget[key] = keys[idx];
        }
    }

    private void DrawAddButton(string sourceKey, Row r, bool isRetainerSource)
    {
        var key = MakePriceKey(sourceKey, r.ItemId, r.IsHQ);
        var price = editedPrice.GetValueOrDefault(key, 0);
        var qtyPer = editedQtyPer.GetValueOrDefault(key, 0);
        var slots = editedSlots.GetValueOrDefault(key, 0);
        var targetKey = ResolveTargetKey(sourceKey, key);

        var canAdd = r.IsListable
                     && price > 0
                     && qtyPer > 0
                     && slots > 0
                     && !string.IsNullOrEmpty(targetKey)
                     && configuration.Snapshots.ContainsKey(targetKey);

        if (!canAdd) ImGui.BeginDisabled();
        if (ImGui.SmallButton($"{Strings.AddToPlan}##add-{key}"))
        {
            AddPlan(sourceKey, r, price, qtyPer, slots, targetKey, isRetainerSource);
        }
        if (!canAdd) ImGui.EndDisabled();
    }

    private void AddPlan(string sourceKey, Row r, long price, int qtyPer, int slots, string targetKey, bool isRetainerSource)
    {
        if (!configuration.Snapshots.TryGetValue(targetKey, out var target)) return;

        var sourceLabel = isRetainerSource
            ? target.RetainerName
            : (sourceKey.EndsWith(".saddle") ? Strings.SaddlebagHeader : Strings.CharacterInventoryHeader);

        pendingPlans.Add(new PendingPlan
        {
            ItemId = r.ItemId,
            IsHQ = r.IsHQ,
            ItemName = r.ItemName,
            SourceKey = sourceKey,
            SourceLabel = sourceLabel,
            TargetKey = targetKey,
            TargetName = target.RetainerName,
            QtyPer = qtyPer,
            Slots = slots,
            UnitPrice = price,
            MaxStack = r.MaxStack,
        });
    }

    private void DrawPendingPlans()
    {
        ImGui.TextUnformatted(string.Format(Strings.PendingPlansHeader, pendingPlans.Count));
        ImGui.SameLine();
        if (pendingPlans.Count > 0 && ImGui.SmallButton(Strings.ClearAllPlans + "##clear-plans"))
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
        if (!ImGui.BeginTable("##pending-table", 6, flags)) return;
        ImGui.TableSetupColumn(Strings.ColItem, ImGuiTableColumnFlags.WidthStretch, 0.35f);
        ImGui.TableSetupColumn(Strings.ColLotsConfig, ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn(Strings.ColPrice, ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableSetupColumn(Strings.ColTarget, ImGuiTableColumnFlags.WidthStretch, 0.25f);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthStretch, 0.25f);
        ImGui.TableSetupColumn("##remove", ImGuiTableColumnFlags.WidthFixed, 50);
        ImGui.TableHeadersRow();

        // 削除はループ後に行う
        var removeIdx = -1;
        for (var i = 0; i < pendingPlans.Count; i++)
        {
            var p = pendingPlans[i];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(p.ItemName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{p.QtyPer} x {p.Slots}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{p.UnitPrice:N0} g");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(p.TargetName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(p.SourceLabel);
            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"{Strings.PendingPlanRemove}##rm-{i}")) removeIdx = i;
        }
        ImGui.EndTable();

        if (removeIdx >= 0) pendingPlans.RemoveAt(removeIdx);
    }
}

/// <summary>1 行ぶんの「+追加」アクションでプランリストに積む内部表現。
/// ExpandPlanToActions で実行可能な PlannedAction list に展開する。</summary>
public sealed class PendingPlan
{
    public uint ItemId;
    public bool IsHQ;
    public string ItemName = string.Empty;
    public string SourceKey = string.Empty;
    public string SourceLabel = string.Empty;
    public string TargetKey = string.Empty;
    public string TargetName = string.Empty;
    public int QtyPer;
    public int Slots;
    public long UnitPrice;
    public int MaxStack;
}
