using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Restocker.Common;

public static unsafe class ContextMenuHelper
{
    private const int EntryNameOffset = 8;

    public static int FindEntryIndex(Predicate<string> predicate)
    {
        var addon = AddonHelper.GetVisible("ContextMenu");
        if (addon == null) return -1;
        var values = addon->AtkValues;
        if (values == null) return -1;
        var count = (int)values[0].UInt;
        var totalValues = (int)addon->AtkValuesCount;
        for (var i = 0; i < count; i++)
        {
            var idx = i + EntryNameOffset;
            if (idx >= totalValues) break;
            var v = values[idx];
            if (v.Type != AtkValueType.String) continue;
            var s = v.String;
            if (s.Value == null) continue;
            var text = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)s.Value) ?? string.Empty;
            if (predicate(text)) return i;
        }
        return -1;
    }

    public static bool ClickEntry(Predicate<string> predicate)
    {
        var addon = AddonHelper.GetVisible("ContextMenu");
        if (addon == null) return false;
        var idx = FindEntryIndex(predicate);
        if (idx < 0) return false;
        Callback.Fire(addon, true, 0, idx, 0);
        return true;
    }

    /// <summary>診断用: ContextMenu に表示されているエントリを全部読み取る。</summary>
    public static List<string> EnumerateEntries()
    {
        var result = new List<string>();
        var addon = AddonHelper.GetVisible("ContextMenu");
        if (addon == null) return result;
        var values = addon->AtkValues;
        if (values == null) return result;
        var count = (int)values[0].UInt;
        var totalValues = (int)addon->AtkValuesCount;
        for (var i = 0; i < count; i++)
        {
            var idx = i + EntryNameOffset;
            if (idx >= totalValues) break;
            var v = values[idx];
            if (v.Type != AtkValueType.String)
            {
                result.Add($"[type={v.Type}]");
                continue;
            }
            var s = v.String;
            if (s.Value == null)
            {
                result.Add("(null)");
                continue;
            }
            result.Add(System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)s.Value) ?? string.Empty);
        }
        return result;
    }

    public static void Close()
    {
        var addon = AddonHelper.GetVisible("ContextMenu");
        if (addon == null) return;
        // ContextMenu close: index = -1 で発火
        Callback.Fire(addon, true, 0, -1, 0);
    }
}
