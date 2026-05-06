using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Restocker.Common;

/// <summary>
/// ECommons.Automation.UIInput.ClickHelper.ClickAddonButton と同等のボタンクリック。
/// addon->ReceiveEvent でボタンの保持する AtkEvent を投げ込む。
/// Confirm / Cancel など FireCallback では発火しないボタンをクリックするために必要。
/// </summary>
public static unsafe class ButtonClick
{
    public static bool Click(AtkComponentButton* button, AtkUnitBase* addon)
    {
        if (button == null || addon == null) return false;
        var ownerNode = button->AtkComponentBase.OwnerNode;
        if (ownerNode == null) return false;
        var btnRes = &ownerNode->AtkResNode;
        var evtMgr = btnRes->AtkEventManager;
        var evt = (AtkEvent*)evtMgr.Event;
        if (evt == null) return false;
        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, (AtkEvent*)evtMgr.Event);
        return true;
    }
}
