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
    private readonly ConfirmDialog confirmDialog;
    private string filter = string.Empty;
    private bool listableOnly = true;
    /// <summary>編集中の価格。キーは "{sourceKey}#{itemId}.{hq}"。</summary>
    private readonly Dictionary<string, long> editedPrice = new();
    /// <summary>キャラ所持品セクションごとの「出品先リテイナー key」選択。キーは char source key。</summary>
    private readonly Dictionary<string, string> charTargetRetainer = new();
    /// <summary>明示的に折りたたんだセクションの id。CollapsingHeader のフレーム間状態保持を奪われる事故を回避。</summary>
    private readonly HashSet<string> collapsedSections = new();

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
            foreach (var snap in configuration.Snapshots.Values)
                collapsedSections.Add("list-" + snap.Key);
            foreach (var ch in configuration.Characters.Values)
            {
                var k = CharacterSnapshot.MakeKey(ch.CharacterContentId);
                collapsedSections.Add("char-" + k);
                collapsedSections.Add("saddle-" + k);
            }
        }
        ImGui.SameLine();
        if (ImGui.SmallButton(Strings.ExpandAll + "##list-expand"))
        {
            collapsedSections.Clear();
        }
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
            // Apply 直前にキャラ snapshot を取り直して plan の食い違いを防ぐ
            Plugin.Instance?.RetainerWatcher.CaptureCharacterSnapshot();
            var freshActions = BuildPlannedActions();
            confirmDialog.Request(freshActions, executor.StartApplyActions);
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
        // 1) リテイナー所持品 → そのリテイナーで出品（既存）
        foreach (var snap in configuration.Snapshots.Values)
        {
            foreach (var entry in DistinctItems(snap.Inventory))
            {
                var key = MakePriceKey(snap.Key, entry.ItemId, entry.IsHQ);
                if (!editedPrice.TryGetValue(key, out var price) || price <= 0) continue;
                result.AddRange(Planner.PlanNewListings(new[] { snap }, entry.ItemId, entry.IsHQ, price, entry.MaxStackPerListing));
            }
        }
        // 2) キャラバッグ・サドル → 選択された target リテイナーで「預け入れなし」直接出品
        //    InventoryManager.MoveToRetainerMarket を使うのでサドルも同じ経路。
        foreach (var ch in configuration.Characters.Values)
        {
            var charKey = CharacterSnapshot.MakeKey(ch.CharacterContentId);
            // bag
            AddCharSourceActions(result, ch.Bag, charKey + ".bag");
            // saddle (free + premium 両方を 1 セットで扱う)
            AddCharSourceActions(result, ch.Saddlebag.Concat(ch.PremiumSaddlebag).ToList(), charKey + ".saddle");
        }
        return result;
    }

    private void AddCharSourceActions(List<PlannedAction> result, List<Restocker.Data.InventoryEntry> inventory, string sourceKey)
    {
        if (!charTargetRetainer.TryGetValue(sourceKey, out var targetKey)) return;
        if (string.IsNullOrEmpty(targetKey) || !configuration.Snapshots.TryGetValue(targetKey, out var target)) return;

        foreach (var entry in DistinctItems(inventory))
        {
            var pkey = MakePriceKey(sourceKey, entry.ItemId, entry.IsHQ);
            if (!editedPrice.TryGetValue(pkey, out var price) || price <= 0) continue;
            result.AddRange(Planner.PlanFromInventoryList(inventory, sourceKey, target, entry.ItemId, entry.IsHQ, price, entry.MaxStackPerListing));
        }
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

    /// <summary>
    /// 折りたたみ状態を <see cref="collapsedSections"/> で明示管理する CollapsingHeader。
    /// ImGui のフレーム内ステート（DefaultOpen）に依存しないので、
    /// 「閉じたのに勝手に展開される」事故が起きない。
    /// </summary>
    private bool DrawCollapsingHeader(string id, string label)
    {
        var open = !collapsedSections.Contains(id);
        ImGui.SetNextItemOpen(open, ImGuiCond.Always);
        var nowOpen = ImGui.CollapsingHeader(label + "##" + id);
        if (nowOpen != open)
        {
            if (nowOpen) collapsedSections.Remove(id);
            else collapsedSections.Add(id);
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

        DrawRowsTable(snap.Key, filtered, sourceLabel: null, isRetainerSource: true);
    }

    private void DrawCharacterSection(CharacterSnapshot ch)
    {
        var sourceKey = CharacterSnapshot.MakeKey(ch.CharacterContentId);

        // バッグ — target リテイナーを指定して「預け入れなし」で直接出品可
        var bagRows = AggregatePerSource(ch.Bag, listingSource: null);
        var bagFiltered = ApplyFilter(bagRows);
        if (bagFiltered.Count > 0 || filter.Length == 0)
        {
            var header = string.Format(Strings.RetainerHeaderInventory, $"{Strings.CharacterInventoryHeader} ({ch.CharacterName})", bagFiltered.Count, FreshnessSuffix(ch.LastRefreshedUtc));
            if (DrawCollapsingHeader("char-" + sourceKey, header))
            {
                DrawCharBagTargetCombo(sourceKey + ".bag");
                DrawRowsTable(sourceKey + ".bag", bagFiltered, Strings.CharacterInventoryHeader, isRetainerSource: false);
            }
        }

        // サドル系も MoveToRetainerMarket 経由で直接出品可能
        var saddleRows = AggregatePerSource(ch.Saddlebag.Concat(ch.PremiumSaddlebag).ToList(), listingSource: null);
        var saddleFiltered = ApplyFilter(saddleRows);
        if (saddleFiltered.Count > 0 || filter.Length == 0)
        {
            var header = string.Format(Strings.RetainerHeaderInventory, $"{Strings.SaddlebagHeader} ({ch.CharacterName})", saddleFiltered.Count, FreshnessSuffix(ch.LastRefreshedUtc));
            if (DrawCollapsingHeader("saddle-" + sourceKey, header))
            {
                DrawCharBagTargetCombo(sourceKey + ".saddle");
                DrawRowsTable(sourceKey + ".saddle", saddleFiltered, Strings.SaddlebagHeader, isRetainerSource: false);
            }
        }
    }

    private void DrawCharBagTargetCombo(string sourceKey)
    {
        var retainers = configuration.Snapshots.Values
            .OrderBy(s => s.RetainerName).ToList();
        if (retainers.Count == 0)
        {
            ImGui.TextDisabled(Strings.CharBagNeedRetainerSnapshot);
            return;
        }

        var current = charTargetRetainer.GetValueOrDefault(sourceKey, string.Empty);
        var labels = new List<string> { Strings.CharBagPickTarget };
        var keys = new List<string> { string.Empty };
        foreach (var r in retainers)
        {
            var freeSlots = Plan.Planner.MaxListingSlots - r.Listings.Count;
            labels.Add($"{r.RetainerName}  ({freeSlots} {Strings.FreeSlots})");
            keys.Add(r.Key);
        }
        var idx = keys.IndexOf(current);
        if (idx < 0) idx = 0;

        ImGui.SetNextItemWidth(280);
        if (ImGui.Combo($"{Strings.CharBagTargetLabel}##target-{sourceKey}", ref idx, labels.ToArray(), labels.Count))
        {
            charTargetRetainer[sourceKey] = keys[idx];
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
