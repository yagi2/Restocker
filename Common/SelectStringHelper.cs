using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Restocker.Common;

/// <summary>SelectString addon (FFXIV のコンテキストメニュー風 UI) の操作ヘルパ。</summary>
public static unsafe class SelectStringHelper
{
    /// <summary>テキスト一致でエントリを探し、見つかれば click する。完了したら true。</summary>
    public static bool ClickEntryByText(string targetText)
    {
        var addon = AddonHelper.GetVisible("SelectString");
        if (addon == null) return false;
        var ss = (AddonSelectString*)addon;

        var count = ss->PopupMenu.PopupMenu.EntryCount;
        for (var i = 0; i < count; i++)
        {
            var entryName = ss->PopupMenu.PopupMenu.EntryNames[i].ToString() ?? string.Empty;
            if (entryName == targetText)
            {
                Callback.Fire(addon, true, i);
                return true;
            }
        }
        return false;
    }

    /// <summary>SelectString に指定テキストのエントリが存在するか（クリックはしない）。</summary>
    public static bool HasEntry(string targetText)
    {
        var addon = AddonHelper.GetVisible("SelectString");
        if (addon == null) return false;
        var ss = (AddonSelectString*)addon;

        var count = ss->PopupMenu.PopupMenu.EntryCount;
        for (var i = 0; i < count; i++)
        {
            if (ss->PopupMenu.PopupMenu.EntryNames[i].ToString() == targetText) return true;
        }
        return false;
    }
}
