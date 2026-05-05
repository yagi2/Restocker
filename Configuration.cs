using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Restocker.Data;

namespace Restocker;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>
    /// リテイナーベル（RetainerList addon）が開いている間に MainWindow を自動表示するか。
    /// </summary>
    public bool AutoOpenOnBell { get; set; } = true;

    /// <summary>
    /// パッシブ収集したリテイナーごとのスナップショット。
    /// キーは <see cref="RetainerSnapshot.Key"/>（CharacterContentId + RetainerId の文字列）。
    /// </summary>
    public Dictionary<string, RetainerSnapshot> Snapshots { get; set; } = new();

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
