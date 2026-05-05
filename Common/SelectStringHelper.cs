using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace Restocker.Common;

/// <summary>SelectString addon (FFXIV のリテイナーメニュー UI 等) の操作ヘルパ。</summary>
public static unsafe class SelectStringHelper
{
    /// <summary>predicate に一致する最初のエントリの index を返す（見つからなければ -1）。</summary>
    public static int FindEntryIndex(Predicate<string> predicate)
    {
        var addon = AddonHelper.GetVisible("SelectString");
        if (addon == null) return -1;
        var ss = (AddonSelectString*)addon;
        var count = ss->PopupMenu.PopupMenu.EntryCount;
        for (var i = 0; i < count; i++)
        {
            var raw = ss->PopupMenu.PopupMenu.EntryNames[i].ToString() ?? string.Empty;
            if (predicate(raw)) return i;
        }
        return -1;
    }

    public static bool ClickEntry(Predicate<string> predicate)
    {
        var addon = AddonHelper.GetVisible("SelectString");
        if (addon == null) return false;
        var idx = FindEntryIndex(predicate);
        if (idx < 0) return false;
        Callback.Fire(addon, true, idx);
        return true;
    }

    public static bool HasEntry(Predicate<string> predicate) => FindEntryIndex(predicate) >= 0;

    /// <summary>デバッグ用: 現在 SelectString に表示されているエントリ一覧。</summary>
    public static List<string> EnumerateEntries()
    {
        var result = new List<string>();
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
