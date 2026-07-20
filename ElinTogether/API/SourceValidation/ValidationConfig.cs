using System;
using System.Collections.Generic;

namespace ElinTogether.API.SourceValidation;

[Flags]
public enum ValidationFlags
{
    None = 0,
    Sources = 1 << 0,
    Plugins = 1 << 1,
    Files = 1 << 2,
    All = Sources | Plugins | Files,
}

public static class ValidationConfig
{
    // ReSharper disable StringLiteralTypo
    public static readonly HashSet<string> ExcludedPlugins = [
        //"lafrontier.minigame",        // Mod_Slot
        "com.sinai.unityexplorer",      // Unity Explorer
        "jp.cmbc.mod.elin.yk-devtool",  // 3400020855
    ];

    public static readonly HashSet<string> DefaultFilePaths = [];

    public static readonly HashSet<string> DefaultSources = [
        nameof(SourceBlock),
        nameof(SourceChara),
        nameof(SourceElement),
        nameof(SourceFaction),
        nameof(SourceHobby),
        nameof(SourceThing),
        nameof(SourceJob),
        nameof(SourceMaterial),
        nameof(SourceObj),
        nameof(SourceQuest),
        nameof(SourceRace),
        nameof(SourceRecipe),
        nameof(SourceReligion),
        nameof(SourceStat),
        nameof(SourceZone),
    ];

    public static ValidationFlags GetConfiguredFlags(string? config = null)
    {
        var raw = (config ?? EmpConfig.Server.SourceValidationSet.Value)?.Trim().Trim(',');
        if (raw.IsEmpty() || raw! == "none") {
            return ValidationFlags.None;
        }

        if (raw! == "all") {
            return ValidationFlags.All;
        }

        var flags = ValidationFlags.None;
        foreach (var part in raw!.Split(',')) {
            switch (part.Trim().ToLower()) {
                case "source":
                    flags |= ValidationFlags.Sources;
                    break;
                case "plugin":
                    flags |= ValidationFlags.Plugins;
                    break;
                case "file":
                    flags |= ValidationFlags.Files;
                    break;
            }
        }

        return flags;
    }

    /// <summary>
    ///     Parse file paths from config (after colon separator).
    ///     Format: "source,file:path1.dll,path2.dll"
    /// </summary>
    public static List<string> GetConfiguredFilePaths()
    {
        var filePaths = new List<string>(DefaultFilePaths);

        var raw = EmpConfig.Server.SourceValidationSet.Value?.Trim();
        if (raw.IsEmpty()) {
            return filePaths;
        }

        var split = raw!.IndexOf(':');
        if (split >= 0) {
            var pathsPart = raw[(split + 1)..];
            filePaths.AddRange(pathsPart.Split(',', StringSplitOptions.RemoveEmptyEntries));
        }

        return filePaths;
    }
}