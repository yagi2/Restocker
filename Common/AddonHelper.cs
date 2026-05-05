using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Restocker.Common;

public static unsafe class AddonHelper
{
    /// <summary>名前指定で AtkUnitBase* を取り、Visible なら返す。それ以外は null。</summary>
    public static AtkUnitBase* GetVisible(string name)
    {
        var addon = Plugin.GameGui.GetAddonByName(name);
        if (addon.Address == nint.Zero) return null;
        var unitBase = (AtkUnitBase*)addon.Address;
        if (unitBase == null || !unitBase->IsVisible) return null;
        return unitBase;
    }

    public static bool IsOpen(string name) => GetVisible(name) != null;
}
