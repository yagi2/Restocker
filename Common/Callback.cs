using System;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Restocker.Common;

/// <summary>
/// AtkUnitBase.FireCallback を可変長 params で叩く ECommons.Automation.Callback の最小ポート。
/// 型から AtkValueType を自動推論する。
///
/// 使い方:
///   Callback.Fire(addon, true, 2, (uint)retainerIndex, ZeroAtkValue, ZeroAtkValue);
///   Callback.Fire(addon, true, entryIndex);
/// </summary>
public static unsafe class Callback
{
    /// <summary>type=Int / value=0 のダミー値。RetainerList などで pad に使う。</summary>
    public static readonly AtkValue ZeroAtkValue = new() { Type = AtkValueType.Int, Int = 0 };

    public static void Fire(AtkUnitBase* addon, bool updateState, params object[] values)
    {
        if (addon == null) throw new ArgumentNullException(nameof(addon));
        if (values == null || values.Length == 0)
        {
            addon->FireCallback(0, null, updateState);
            return;
        }

        var atk = (AtkValue*)Marshal.AllocHGlobal(values.Length * sizeof(AtkValue));
        var allocatedStrings = new System.Collections.Generic.List<IntPtr>();
        try
        {
            for (var i = 0; i < values.Length; i++)
            {
                switch (values[i])
                {
                    case uint u:
                        atk[i].Type = AtkValueType.UInt;
                        atk[i].UInt = u;
                        break;
                    case int n:
                        atk[i].Type = AtkValueType.Int;
                        atk[i].Int = n;
                        break;
                    case float f:
                        atk[i].Type = AtkValueType.Float;
                        atk[i].Float = f;
                        break;
                    case bool b:
                        atk[i].Type = AtkValueType.Bool;
                        atk[i].Byte = (byte)(b ? 1 : 0);
                        break;
                    case string s:
                    {
                        var bytes = Encoding.UTF8.GetBytes(s + '\0');
                        var p = Marshal.AllocHGlobal(bytes.Length);
                        Marshal.Copy(bytes, 0, p, bytes.Length);
                        allocatedStrings.Add(p);
                        atk[i].Type = AtkValueType.String;
                        atk[i].String = (byte*)p;
                        break;
                    }
                    case AtkValue v:
                        atk[i] = v;
                        break;
                    default:
                        throw new ArgumentException($"Callback.Fire: unsupported argument type {values[i]?.GetType().Name ?? "null"} at index {i}");
                }
            }
            addon->FireCallback((uint)values.Length, atk, updateState);
        }
        finally
        {
            foreach (var p in allocatedStrings) Marshal.FreeHGlobal(p);
            Marshal.FreeHGlobal((IntPtr)atk);
        }
    }
}
