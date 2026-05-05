using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Restocker.Common;

/// <summary>SelectString addon (FFXIV のコンテキストメニュー風 UI) の操作ヘルパ。</summary>
public static unsafe class SelectStringHelper
{
    /// <summary>
    /// テキスト一致でエントリを探す。完全一致 / 末尾の "." 等を除去した startsWith / 部分一致 の順で寛容に判定。
    /// 言語別にゲーム文字列のフォーマットに揺れがあっても拾えるようにする。
    /// </summary>
    private static int FindEntryIndex(AddonSelectString* ss, string target)
    {
        var count = ss->PopupMenu.PopupMenu.EntryCount;
        var trimmedTarget = target.TrimEnd('.', '。', ' ', '\t');
        for (var i = 0; i < count; i++)
        {
            var raw = ss->PopupMenu.PopupMenu.EntryNames[i].ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(raw)) continue;
            var trimmed = raw.TrimEnd('.', '。', ' ', '\t');
            if (trimmed == trimmedTarget) return i;
            if (trimmed.StartsWith(trimmedTarget, System.StringComparison.Ordinal)) return i;
            if (trimmedTarget.StartsWith(trimmed, System.StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    public static bool ClickEntryByText(string targetText)
    {
        var addon = AddonHelper.GetVisible("SelectString");
        if (addon == null) return false;
        var ss = (AddonSelectString*)addon;
        var idx = FindEntryIndex(ss, targetText);
        if (idx < 0) return false;
        Callback.Fire(addon, true, idx);
        return true;
    }

    public static bool HasEntry(string targetText)
    {
        var addon = AddonHelper.GetVisible("SelectString");
        if (addon == null) return false;
        var ss = (AddonSelectString*)addon;
        return FindEntryIndex(ss, targetText) >= 0;
    }

    /// <summary>デバッグ用: 現在 SelectString に表示されているエントリ一覧を返す。</summary>
    public static System.Collections.Generic.List<string> EnumerateEntries()
    {
        var result = new System.Collections.Generic.List<string>();
        var addon = AddonHelper.GetVisible("SelectString");
        if (addon == null) return result;
        var ss = (AddonSelectString*)addon;
        var count = ss->PopupMenu.PopupMenu.EntryCount;
        for (var i = 0; i < count; i++)
        {
            result.Add(ss->PopupMenu.PopupMenu.EntryNames[i].ToString() ?? string.Empty);
        }
        return result;
    }
}
