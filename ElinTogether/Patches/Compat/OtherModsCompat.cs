using System;
using System.Linq;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     Small guards around other mods that assume one game from start to end. A session replaces the game of a
///     client (joining, travelling) and removes it when it ends; what such a mod still holds from the old game
///     then throws every frame. Applied once, the first time a session starts, and left in place: the trouble
///     shows after the session
/// </summary>
internal static class OtherModsCompat
{
    private const string HarmonyId = ModInfo.Guid + ".compat";

    private static bool _applied;

    internal static void Apply()
    {
        if (_applied) {
            return;
        }

        _applied = true;

        // Somewhat Enhanced Display (Workshop 3781674985): its health bar reads the last hovered character every
        // frame, game or no game
        Guard("Somewhat Enhanced Display", "Macchacoffee.ElinMods.SomewhatEnhancedDisplay.UI.ModUI", "Update");

        // Same mod, its hover bars: while the guest's map is replaced (zone state, travel) the bar's Image/Text
        // objects are destroyed under it and every frame throws a NullReferenceException from UpdateValue
        // (found 2026-10-23, guest Player.log). The game is fine a frame later, so the NRE is swallowed there only
        Guard("Somewhat Enhanced Display", "Macchacoffee.ElinMods.SomewhatEnhancedDisplay.UI.HoverGuide.HealthBar", "Update", true);

        // Better Elin (Workshop 3799020293): two of its own patches throw a NullReferenceException when what they
        // read is not there yet, as its players reported (comments of 3799020293): its prefix on CardRenderer.Draw
        // (a boss whose actor is not made, which a session's map replacement makes more frequent), and its postfix
        // on FactionBranch.AddMemeber (Card.PurgeDuplicateArtifact without a map: a base member added while a game
        // loads, which aborted the load). Only that exception is swallowed; the game's own method then runs as it
        // would without the mod. Its namespace is not known here: found among the patches of the game's method by
        // the patch's class and method names
        GuardPatchOf("Better Elin", AccessTools.Method(typeof(CardRenderer), nameof(CardRenderer.Draw),
            [typeof(RenderParam), typeof(UnityEngine.Vector3).MakeByRefType(), typeof(bool)]), "DungeonBossDrawPatch", "Prefix");
        GuardPatchOf("Better Elin", AccessTools.Method(typeof(FactionBranch), nameof(FactionBranch.AddMemeber)),
            "HoardRecordMemberPatch", "AfterAddMember");
    }

    /// <summary>
    ///     Another mod's own patch (prefix or postfix) of a game method, named by its class and method: only that
    ///     exception thrown in it is swallowed
    /// </summary>
    private static void GuardPatchOf(string modName, System.Reflection.MethodBase? original, string typeName, string methodName)
    {
        try {
            if (original is null || Harmony.GetPatchInfo(original) is not { } info) {
                return;
            }

            var methods = info.Prefixes.Concat(info.Postfixes)
                .Select(p => p.PatchMethod)
                .Where(m => m is { IsStatic: true, ContainsGenericParameters: false } && m.Name == methodName && m.DeclaringType?.Name == typeName)
                .Distinct()
                .ToList();
            foreach (var method in methods) {
                // (a prefix that says whether the game's method runs: on that exception, it runs)
                var finalizer = method.ReturnType == typeof(bool) ? nameof(SwallowNreRun) : nameof(SwallowNre);
                new Harmony(HarmonyId).Patch(method, finalizer: new(typeof(OtherModsCompat), finalizer));
                EmpLog.Information("Compatibility guard set on {ModName} {Type}.{Method}", modName, typeName, methodName);
            }
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Compatibility guard for {ModName} could not be set", modName);
        }
    }

    private static Exception? SwallowNreRun(Exception __exception, ref bool __result)
    {
        if (__exception is not NullReferenceException) {
            return __exception;
        }

        __result = true;
        return null;
    }

    private static void Guard(string modName, string typeName, string methodName, bool swallowNre = false)
    {
        try {
            if (AccessTools.TypeByName(typeName) is not { } type || AccessTools.Method(type, methodName) is not { } method) {
                return;
            }

            if (swallowNre) {
                new Harmony(HarmonyId).Patch(method, finalizer: new(typeof(OtherModsCompat), nameof(SwallowNre)));
            } else {
                new Harmony(HarmonyId).Patch(method, new(typeof(OtherModsCompat), nameof(OnlyWithAGame)));
            }

            EmpLog.Information("Compatibility guard set on {ModName}", modName);
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Compatibility guard for {ModName} could not be set", modName);
        }
    }

    private static Exception? SwallowNre(Exception __exception)
    {
        return __exception is NullReferenceException ? null : __exception;
    }

    private static bool OnlyWithAGame()
    {
        return EClass.core?.game?.player?.chara is not null;
    }
}
