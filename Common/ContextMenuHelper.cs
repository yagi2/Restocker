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
            var text = ExtractText(values[idx]);
            if (text != null && predicate(text)) return i;
        }
        return -1;
    }

    /// <summary>
    /// AtkValue から表示テキストを抽出。ManagedString は SeString として
    /// 読み取らないと payload (制御コード等) のせいで PtrToStringUTF8 が壊れる。
    /// </summary>
    private static string? ExtractText(AtkValue v)
    {
        switch (v.Type)
        {
            case AtkValueType.String:
            case AtkValueType.String8:
            case AtkValueType.ManagedString:
            case AtkValueType.Managed:
                if (v.String.Value == null) return null;
                try
                {
                    var seStr = Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated((nint)v.String.Value);
                    return seStr.TextValue;
                }
                catch
                {
                    return System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)v.String.Value);
                }
            default:
                return null;
        }
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
            var text = ExtractText(values[idx]);
            result.Add(text ?? $"[type={values[idx].Type}]");
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
