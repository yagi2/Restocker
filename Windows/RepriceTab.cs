using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using Restocker.Data;
using Restocker.Execution;
using Restocker.Localization;

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
    private string filter = string.Empty;
    /// <summary>編集中の新価格。キーは "{retainerKey}#{listingIndex}"。</summary>
    private readonly Dictionary<string, long> editedPrice = new();

    public RepriceTab(Configuration configuration, Executor executor, ConfirmDialog confirmDialog)
    {
        this.configuration = configuration;
        this.executor = executor;
        this.confirmDialog = confirmDialog;
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
        ImGui.BeginDisabled();
        ImGui.Button(Strings.RepriceMatchLowest);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled(Strings.HQNQNote);

        // 適用ボタンを toolbar 右側に
        ImGui.SameLine();
        DrawApplyButton();
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
            if (!ImGui.CollapsingHeader(header + "##reprice-" + snap.Key, ImGuiTreeNodeFlags.DefaultOpen))
                continue;

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
