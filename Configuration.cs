using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Restocker.Data;
using Restocker.Localization;

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

    /// <summary>
    /// キャラクター側の所持品スナップショット。キーは <see cref="CharacterSnapshot.MakeKey"/>。
    /// 新規出品でキャラ側のアイテムも対象にするため。
    /// </summary>
    public Dictionary<string, CharacterSnapshot> Characters { get; set; } = new();

    /// <summary>
    /// UI 言語。
    /// -1 = FFXIV クライアント言語に従う（ZH/KO はグローバルクライアントから返らないため EN/JA/DE/FR にフォールバック）。
    /// 0..5 = <see cref="Language"/> 列挙の値で明示指定。
    /// </summary>
    public int Language { get; set; } = -1;

    /// <summary>「最安値 -X ギル」ボタンで使う undercut 値 (gil)。デフォルト 1。</summary>
    public int UndercutDelta { get; set; } = 1;

    public Language ResolveLanguage(Dalamud.Game.ClientLanguage clientLanguage)
    {
        if (Language >= 0 && Language <= 5)
            return (Language)Language;

        return clientLanguage switch
        {
            Dalamud.Game.ClientLanguage.Japanese => Localization.Language.Japanese,
            Dalamud.Game.ClientLanguage.German => Localization.Language.German,
            Dalamud.Game.ClientLanguage.French => Localization.Language.French,
            _ => Localization.Language.English,
        };
    }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
