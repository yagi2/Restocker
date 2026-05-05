using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Restocker.Common;

/// <summary>
/// ContextMenu addon の操作ヘルパ。
/// ECommons の AddonMaster.ContextMenu と同じ仕組み:
///   AtkValues[0].UInt = entries count
///   AtkValues[i + 8].String = entry text (null-terminated SeString)
///   click: Callback.Fire(addon, true, 0, index, 0)
/// </summary>
public static unsafe class ContextMenuHelper
{
    private const int EntryNameOffset = 8;

    public static int FindEntryIndexByText(string targetText)
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
            // SeString → text 変換は Lumina/Dalamud 経由が本来。
            // ここでは UTF-8 を直読みして安価に比較（contextmenu 文字列は通常 plain）。
            var text = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)s.Value);
            if (text == targetText) return i;
        }
        return -1;
    }

    public static bool ClickEntryByText(string targetText)
    {
        var addon = AddonHelper.GetVisible("ContextMenu");
        if (addon == null) return false;
        var idx = FindEntryIndexByText(targetText);
        if (idx < 0) return false;
        Callback.Fire(addon, true, 0, idx, 0);
        return true;
    }
}
