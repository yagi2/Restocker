using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using Restocker.Data;
using Restocker.Execution;
using Restocker.Localization;
using Restocker.Market;
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
    private readonly MarketCache marketCache;
    private string filter = string.Empty;
    private bool listableOnly = true;
    private string? lastMatchSummary;
    /// <summary>編集中の価格。キーは "{sourceKey}#{itemId}.{hq}"。</summary>
    private readonly Dictionary<string, long> editedPrice = new();
    /// <summary>1 出品あたりの個数。0/未設定なら MaxStackPerListing。</summary>
    private readonly Dictionary<string, int> editedQtyPer = new();
    /// <summary>出品枠数。0/未設定なら「保有量を埋め切るまで」。</summary>
    private readonly Dictionary<string, int> editedSlots = new();
    /// <summary>キャラ所持品セクションごとの「出品先リテイナー key」選択。キーは char source key。</summary>
    private readonly Dictionary<string, string> charTargetRetainer = new();
    /// <summary>明示的に展開したセクションの id。デフォルトは折りたたみ。</summary>
    private readonly HashSet<string> expandedSections = new();

    public ListTab(Configuration configuration, Executor executor, ConfirmDialog confirmDialog, MarketCache marketCache)
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
        // 適用ボタン
        ImGui.SameLine();
        DrawApplyButton();

        if (!string.IsNullOrEmpty(lastMatchSummary))
        {
            ImGui.TextColored(new System.Numerics.Vector4(0.85f, 0.85f, 0.4f, 1f), lastMatchSummary);
        }
    }

    /// <summary>
    /// 表示中の listable な (ItemId, IsHQ) について MarketCache から
    /// 外れ値除外つき最安値を引いて editedPrice にセットする。
    /// offset = -1 なら最安値 -1ギル。データ無しのアイテムは missing として
    /// summary 表示する (ユーザーがマーケットボードで開けば polling で埋まる)。
    /// </summary>
    private void ApplyMarketLowest(long offset)
    {
        var seen = new HashSet<(uint, bool)>();
        var missing = new HashSet<(uint, bool, string)>();
        var applied = 0;
        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();

        // リテイナー所持品 → そのリテイナー出品用 editedPrice
        foreach (var snap in configuration.Snapshots.Values)
        {
            foreach (var entry in DistinctItems(snap.Inventory))
            {
                if (!entry.IsListable) continue;
                ApplyOne(snap.Key, entry.ItemId, entry.IsHQ, sheet);
            }
        }
        // キャラ所持品 / サドル → target retainer が選ばれてる場合のみ
        foreach (var ch in configuration.Characters.Values)
        {
            var charKey = CharacterSnapshot.MakeKey(ch.CharacterContentId);
            foreach (var entry in DistinctItems(ch.Bag))
            {
                if (!entry.IsListable) continue;
                ApplyOne(charKey + ".bag", entry.ItemId, entry.IsHQ, sheet);
            }
            foreach (var entry in DistinctItems(ch.Saddlebag.Concat(ch.PremiumSaddlebag).ToList()))
            {
                if (!entry.IsListable) continue;
                ApplyOne(charKey + ".saddle", entry.ItemId, entry.IsHQ, sheet);
            }
        }

        if (missing.Count == 0)
            lastMatchSummary = string.Format(Strings.MatchAppliedSummary, applied, seen.Count);
        else
            lastMatchSummary = string.Format(Strings.MatchAppliedSummaryWithMissing, applied, seen.Count, missing.Count,
                string.Join(", ", missing.Take(5).Select(m => m.Item3)) + (missing.Count > 5 ? "…" : ""));

        void ApplyOne(string sourceKey, uint itemId, bool isHQ, Lumina.Excel.ExcelSheet<Lumina.Excel.Sheets.Item> s)
        {
            // フィルタ確認 (アイテム名で絞り込まれているもののみ対象)
            var name = s.TryGetRow(itemId, out var row) ? row.Name.ExtractText() : $"#{itemId}";
            if (isHQ) name += " (HQ)";
            if (filter.Length > 0 && !name.Contains(filter, System.StringComparison.OrdinalIgnoreCase)) return;
            if (listableOnly && s.TryGetRow(itemId, out var r2) && r2.ItemSearchCategory.RowId == 0) return;

            seen.Add((itemId, isHQ));
            if (!marketCache.HasData(itemId, isHQ))
            {
                missing.Add((itemId, isHQ, name));
                return;
            }
            var lowest = marketCache.GetLowest(itemId, isHQ);
            if (lowest <= 0) return;
            var newPrice = lowest + offset;
            if (newPrice <= 0) newPrice = 1;
            editedPrice[MakePriceKey(sourceKey, itemId, isHQ)] = newPrice;
            applied++;
        }
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
                var (qtyPer, slots) = ResolveLots(key);
                result.AddRange(Planner.PlanNewListings(
                    new[] { snap }, entry.ItemId, entry.IsHQ, price, entry.MaxStackPerListing,
                    listingsCap: slots, perListingQty: qtyPer));
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
            var (qtyPer, slots) = ResolveLots(pkey);
            result.AddRange(Planner.PlanFromInventoryList(
                inventory, sourceKey, target, entry.ItemId, entry.IsHQ, price, entry.MaxStackPerListing,
                listingsCap: slots, perListingQty: qtyPer));
        }
    }

    /// <summary>
    /// edited dictionaries から (qtyPer, slots) を取り出す。
    /// dictionary に key 無し = ユーザがその行をまだ表示/操作していない → null (Planner default = 全枠埋め)。
    /// dictionary に key あり = 描画されたので 0 でも 0 件意図として扱う。
    /// 0 の Planner 側での扱いは <see cref="Planner.PlanNewListings"/> 参照。
    /// </summary>
    private (int? qtyPer, int? slots) ResolveLots(string priceKey)
    {
        int? qtyPer = editedQtyPer.TryGetValue(priceKey, out var q) ? q : (int?)null;
        int? slots = editedSlots.TryGetValue(priceKey, out var s) ? s : (int?)null;
        return (qtyPer, slots);
    }

    /// <summary>sourceKey が示すリテイナー (retainer source ならそれ自身、char source なら target) の空き出品枠。</summary>
    private int ResolveFreeListingSlots(string sourceKey)
    {
        if (configuration.Snapshots.TryGetValue(sourceKey, out var snap))
            return System.Math.Max(0, Planner.MaxListingSlots - snap.Listings.Count);
        if (charTargetRetainer.TryGetValue(sourceKey, out var targetKey)
            && configuration.Snapshots.TryGetValue(targetKey, out var targetSnap))
            return System.Math.Max(0, Planner.MaxListingSlots - targetSnap.Listings.Count);
        return 0;
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

    /// <summary>デフォルト = 折りたたみ。ユーザー操作のみ <see cref="expandedSections"/> に反映。</summary>
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

        // retainer source: Item, Owned, Listed, MaxStack, Price, LotsConfig, Plan = 7
        // char source: Item, Owned, MaxStack, Price, LotsConfig = 5
        if (!ImGui.BeginTable($"##list-table-{sourceKey}", isRetainerSource ? 7 : 5, flags)) return;

        ImGui.TableSetupColumn(Strings.ColItem, ImGuiTableColumnFlags.WidthStretch, 0.4f);
        ImGui.TableSetupColumn(Strings.ColOwned, ImGuiTableColumnFlags.WidthFixed, 70);
        if (isRetainerSource)
            ImGui.TableSetupColumn(Strings.ColListed, ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn(Strings.ColMaxStack, ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn(Strings.ColPrice, ImGuiTableColumnFlags.WidthStretch, 0.3f);
        ImGui.TableSetupColumn(Strings.ColLotsConfig, ImGuiTableColumnFlags.WidthFixed, 140);
        if (isRetainerSource)
            ImGui.TableSetupColumn(Strings.ColPlan, ImGuiTableColumnFlags.WidthStretch, 0.25f);
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

            if (isRetainerSource)
            {
                ImGui.TableNextColumn();
                DrawPlanCell(sourceKey, r);
            }
        }

        ImGui.EndTable();
    }

    /// <summary>
    /// 個数 × 枠数 + MAX ボタン。
    /// 行を一度描画したら editedQtyPer / editedSlots に key を作り (0 で初期化)、
    /// 「画面に表示されている 0×0 = 出品しない」「未表示 = Planner デフォルト」を区別する。
    /// MAX ボタンは min(MaxStack, 所持数) と必要枠数 (空き枠で頭打ち) を入れる。
    /// </summary>
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
            var freeSlots = ResolveFreeListingSlots(sourceKey);
            var neededSlots = qPer > 0 ? (r.Qty + qPer - 1) / qPer : 0;
            var targetSlots = System.Math.Min(freeSlots, neededSlots);
            if (targetSlots < 0) targetSlots = 0;
            editedQtyPer[key] = qPer;
            editedSlots[key] = targetSlots;
        }
        if (maxDisabled) ImGui.EndDisabled();
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

    /// <summary>
    /// 価格セル: 入力欄 + アイテム毎の「最安値」「-1g」クイックボタンを並べる。
    /// </summary>
    private void DrawPriceCell(string sourceKey, Row r)
    {
        var key = MakePriceKey(sourceKey, r.ItemId, r.IsHQ);
        var v = (int)editedPrice.GetValueOrDefault(key, 0);
        ImGui.SetNextItemWidth(90);
        if (ImGui.InputInt($"##price-{key}", ref v, 0))
        {
            if (v < 0) v = 0;
            editedPrice[key] = v;
        }
        ImGui.SameLine(0, 4);
        DrawPriceQuickButtons(sourceKey, r.ItemId, r.IsHQ);
    }

    /// <summary>
    /// 1 行ぶんの「最安値」/「-1ギル」クイックボタン。
    /// MarketCache にデータがあればそのまま使い、無ければ Executor に
    /// インベントリ経由 (新規出品ダイアログ → ComparePrices) で fetch を依頼。
    /// fetch 完了時に OnFetchMarketCompleted で価格を反映する。
    /// </summary>
    private void DrawPriceQuickButtons(string sourceKey, uint itemId, bool isHQ)
    {
        var key = MakePriceKey(sourceKey, itemId, isHQ);
        var cached = marketCache.HasData(itemId, isHQ);
        var busy = executor.IsRunning;

        if (busy) ImGui.BeginDisabled();
        if (ImGui.SmallButton($"{Strings.QuickLowest}##q-{sourceKey}-{itemId}-{(isHQ ? 1 : 0)}"))
        {
            ApplyOrFetchAndApply(itemId, isHQ, key, offset: 0);
        }
        ImGui.SameLine(0, 4);
        if (ImGui.SmallButton($"{Strings.QuickLowestMinus1}##q1-{sourceKey}-{itemId}-{(isHQ ? 1 : 0)}"))
        {
            ApplyOrFetchAndApply(itemId, isHQ, key, offset: -1);
        }
        if (busy) ImGui.EndDisabled();
        else if (!cached)
        {
            // disabled じゃなく押せる (fetch 発火する) ので tooltip だけ
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Strings.MarketDataNeeded);
        }
    }

    private void ApplyOrFetchAndApply(uint itemId, bool isHQ, string priceKey, long offset)
    {
        // ユーザー要望: 最安/-1g は **常に最新のマーケット相場を確認** してから
        // 適用する。古い cache を使うと「最安」と称して過去の値段が入ってしまうため。
        // よって cache 早期リターンは行わない。
        Plugin.Log.Information($"[Restocker] fetch market price (always fresh) for item={itemId} hq={isHQ} priceKey={priceKey}");

        executor.OnFetchMarketCompleted = () =>
        {
            var l = marketCache.GetLowest(itemId, isHQ);
            if (l <= 0)
            {
                Plugin.Log.Warning($"[Restocker] fetch completed but no market data for item={itemId} hq={isHQ}");
                return;
            }
            var p = l + offset;
            if (p <= 0) p = 1;
            editedPrice[priceKey] = p;
        };

        // 現在開いている active retainer の listing 経由でのみ fetch (別リテイナーへ
        // 自動巡回はしない)。出品中でなければ何も起きない (warning ログのみ)。
        TryFetchViaActiveRetainerListing(itemId, isHQ);
    }

    /// <summary>
    /// 「現在 RetainerSellList が開いている active retainer」でだけ fetch を試みる。
    /// 別リテイナーへの自動巡回は行なわない (ユーザの現在セッションが奪われ、
    /// 元のリテイナーに戻れず作業を中断させてしまうため)。active retainer で
    /// 該当アイテムが出品されていなければ何もしない (false)。
    /// </summary>
    private bool TryFetchViaActiveRetainerListing(uint itemId, bool isHQ)
    {
        if (!Common.AddonHelper.IsOpen("RetainerSellList"))
        {
            Plugin.Log.Warning("[Restocker] cannot fetch: RetainerSellList is not open. Open the retainer that has this item listed first.");
            return false;
        }
        var activeSnap = ResolveActiveRetainerSnapshot();
        if (activeSnap == null)
        {
            Plugin.Log.Warning("[Restocker] cannot fetch: no active retainer snapshot");
            return false;
        }
        if (!activeSnap.Listings.Any(l => l.ItemId == itemId && l.IsHQ == isHQ))
        {
            Plugin.Log.Warning($"[Restocker] active retainer {activeSnap.RetainerName} does not list item={itemId} hq={isHQ}; cannot fetch via listing slot");
            return false;
        }
        Plugin.Log.Information($"[Restocker] fetching via active retainer {activeSnap.RetainerName} (item={itemId} hq={isHQ})");
        return executor.StartFetchMarketPriceForListing(activeSnap.Key, itemId, isHQ);
    }

    private unsafe Data.RetainerSnapshot? ResolveActiveRetainerSnapshot()
    {
        var rm = RetainerManager.Instance();
        if (rm == null || !rm->IsReady) return null;
        var ar = rm->GetActiveRetainer();
        if (ar == null) return null;
        var name = ar->NameString;
        return configuration.Snapshots.Values.FirstOrDefault(s => s.RetainerName == name);
    }

    private void DrawPlanCell(string sourceKey, Row r)
    {
        var key = MakePriceKey(sourceKey, r.ItemId, r.IsHQ);
        var price = editedPrice.GetValueOrDefault(key, 0);
        if (price <= 0) { ImGui.TextDisabled("—"); return; }
        if (!r.IsListable) { ImGui.TextDisabled(Strings.Unsellable); return; }

        if (!configuration.Snapshots.TryGetValue(sourceKey, out var snap)) { ImGui.TextDisabled("—"); return; }
        var (qtyPer, slots) = ResolveLots(key);
        var plan = Planner.PlanNewListings(new[] { snap }, r.ItemId, r.IsHQ, price, r.MaxStack,
            listingsCap: slots, perListingQty: qtyPer);
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
