using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EModding.Helper.Runtime;

namespace ElinTogether.Helper;

/// <summary>
///     The mods the host accepts on a player's side only (server setting ClientOnlyMods, user 2026-10-30: Somewhat
///     Enhanced Display on the public server): a player may have them or not. They never make a player refused,
///     are never asked of anyone (no fetch, no "restart without"), and their DLLs are left out of the plugin check on
///     both sides. Named by Elin mod id (package.xml), Workshop number or BepInEx plugin GUID. What they add to the
///     game's data (items, characters...) still counts in the data check: only for mods that add none
/// </summary>
internal static class ClientOnlyMods
{
    /// <summary>
    ///     The list as the host writes it (comma, semicolon or line separated), lower case
    /// </summary>
    internal static HashSet<string> Parse(string? raw)
    {
        return [
            ..(raw ?? "").Split([',', ';', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().ToLowerInvariant())
                .Where(s => s.Length > 0),
        ];
    }

    /// <summary>
    ///     This game's list, as a host
    /// </summary>
    internal static HashSet<string> Hosted => Parse(EmpConfig.Server.ClientOnlyMods.Value);

    /// <summary>
    ///     The list as told to a joining player: with the Workshop numbers of this game's own listed mods. The host's
    ///     mod list names a mod by its Workshop number only, so a listed mod the host runs itself (named by its id)
    ///     was "missing" on every player's side and the join was cancelled to fetch it
    /// </summary>
    internal static string Sent
    {
        get {
            var list = Hosted;
            return string.Join(",", list.Concat(ModList.Here
                .Where(m => m.Workshop.Length > 0 && Lists(list, m.Id, m.Workshop))
                .Select(m => m.Workshop)).Distinct());
        }
    }

    /// <summary>
    ///     That mod of the list (its id or its Workshop number is in it)
    /// </summary>
    internal static bool Lists(HashSet<string> list, string id, string workshop)
    {
        return list.Count > 0 && (list.Contains(id.Trim().ToLowerInvariant()) || workshop.Length > 0 && list.Contains(workshop.Trim()));
    }

    /// <summary>
    ///     The GUIDs of the plugins of this game that belong to a mod of the list: the plugin's own GUID is in it, or
    ///     its DLL is in the folder of a listed mod
    /// </summary>
    internal static HashSet<string> PluginGuids(HashSet<string> list)
    {
        var guids = new HashSet<string>();
        if (list.Count == 0) {
            return guids;
        }

        var folders = ModList.ActivePackages()
            .Where(p => p.dirInfo is { Exists: true } && Lists(list, p.id ?? "", ModList.WorkshopOf(p)))
            .Select(p => (p, Path.GetFullPath(p.dirInfo.FullName).TrimEnd('\\', '/') + Path.DirectorySeparatorChar))
            .ToList();

        foreach (var (package, _) in folders) {
            if (AddsData(package)) {
                EmpLog.Warning("Client-side mod {Mod} has game data files: what it adds still counts in the data check",
                    package.id);
            }
        }

        foreach (var plugin in TypeQualifier.Plugins) {
            var guid = plugin.Info.Metadata.GUID;
            if (list.Contains(guid.ToLowerInvariant())) {
                guids.Add(guid);
                continue;
            }

            try {
                var location = plugin.GetType().Assembly.Location;
                if (string.IsNullOrEmpty(location)) {
                    continue;
                }

                var path = Path.GetFullPath(location);
                if (folders.Any(f => path.StartsWith(f.Item2, StringComparison.OrdinalIgnoreCase))) {
                    guids.Add(guid);
                }
            } catch (Exception ex) {
                EmpLog.Debug(ex, "Could not place the plugin {Guid} in a mod folder", guid);
            }
        }

        return guids;
    }

    /// <summary>
    ///     That mod has tables the game may read its data from (an Excel sheet, or a map):
    ///     the data check counts them whatever this list says
    /// </summary>
    internal static bool AddsData(BaseModPackage package)
    {
        try {
            var dir = package.dirInfo;
            if (dir is null || !dir.Exists) {
                return false;
            }

            // (a mod's tables of items and characters are Excel sheets, in LangMod too: any sheet counts, a text-only
            // mod such as a display mod has SourceLocalization.json files only)
            return dir.GetFiles("*", SearchOption.AllDirectories)
                .Any(file => file.Extension.ToLowerInvariant() is ".xlsx" or ".xls" or ".z");
        } catch (Exception ex) {
            EmpLog.Debug(ex, "Could not read the files of the mod {Mod}", package.id);
            return false;
        }
    }
}
