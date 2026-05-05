using System;
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
        for (var i = 0; i < count; i++)
        {
            var v = values[i + EntryNameOffset];
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
}
