using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ElinTogether.Helper;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     Attract Backer (ConDrawBacker) and Attract Metal (ConDrawMetal) shape what a new map holds: backer pets and
///     trees in a field (Zone_Field.OnGenerateMap, Zone_Yowyn), the follower waiting for rescue (Zone.TrySpawnFollower),
///     the metal slime of an encounter (ZonePreEnterEncounter.Execute). The game asks "the player" only. With
///     independent travel a player who walks into a field first makes it in its own game (a guest: a lease without a
///     map, ElinNetHostTravel), with its own spell: the same for everyone. Travelling together, the host's game makes
///     every map and every encounter for all the players who come along (those not away): the spell of any of them
///     counts, not only the host's
/// </summary>
[HarmonyPatch]
internal static class AttractPlayersPatch
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(Zone_Field), nameof(Zone_Field.OnGenerateMap));
        yield return AccessTools.Method(typeof(Zone_Yowyn), nameof(Zone_Yowyn.OnVisitNewMapOrRegenerate));
        yield return AccessTools.Method(typeof(Zone), nameof(Zone.TrySpawnFollower));
        yield return AccessTools.Method(typeof(ZonePreEnterEncounter), nameof(ZonePreEnterEncounter.Execute));
    }

    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> OnAskCondition(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var anyPlayer = AccessTools.Method(typeof(AttractPlayersPatch), nameof(AnyPlayerHas));
        var changed = 0;
        foreach (var code in instructions) {
            if ((code.opcode == OpCodes.Callvirt || code.opcode == OpCodes.Call) &&
                code.operand is MethodInfo { Name: nameof(Card.HasCondition), IsGenericMethod: true } asked &&
                asked.GetGenericArguments() is [var condition] &&
                (condition == typeof(ConDrawBacker) || condition == typeof(ConDrawMetal))) {
                // (the same stack: the card asked, then the answer)
                code.opcode = OpCodes.Call;
                code.operand = anyPlayer.MakeGenericMethod(condition);
                changed++;
            }

            yield return code;
        }

        if (changed == 0) {
            EmpLog.Warning("Attract spells: no condition asked in {Method}, the game changed", original.FullDescription());
        }
    }

    /// <summary>
    ///     The local player has it, or (the host's maps, travelling together) a player who comes along does
    /// </summary>
    internal static bool AnyPlayerHas<T>(Card asked) where T : Condition
    {
        if (asked.HasCondition<T>()) {
            return true;
        }

        if (!asked.IsPC || NetSession.Instance.Connection is not ElinNetHost host || NetSession.Instance.Rules.AllowIndependentTravel) {
            return false;
        }

        return host.ActiveRemoteCharas.Any(p => !host.IsAwayPeer(p.Key) && p.Value is { isDead: false } player && player.HasCondition<T>());
    }
}
