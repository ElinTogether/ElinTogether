using System.Collections.Generic;
using ElinTogether.Helper;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     A brewery (barrel, aging rack) turns its food into the product as "the player" made it: the feats of the
///     cook (creative cooking 487, the gourmet's 1650) were the local player's, in the game that keeps the map the
///     host's, for another player's barrel too. The player who carries it (or whose companion does), else the player
///     who owns the land on its own (a land of a player in no team); otherwise the local player, as before
/// </summary>
[HarmonyPatch(typeof(TraitBrewery), nameof(TraitBrewery.OnChildDecay))]
internal static class TraitBreweryPatch
{
    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> OnChildDecay(IEnumerable<CodeInstruction> instructions)
    {
        var getPc = AccessTools.PropertyGetter(typeof(EClass), nameof(EClass.pc));
        foreach (var instruction in instructions) {
            if (instruction.Calls(getPc)) {
                yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Ldarg_0).MoveLabelsFrom(instruction);
                yield return CodeInstruction.Call(typeof(TraitBreweryPatch), nameof(BrewerOf));
                continue;
            }

            yield return instruction;
        }
    }

    internal static Chara BrewerOf(TraitBrewery brewery)
    {
        if (NetSession.Instance.Transport is null || brewery.owner is not { } barrel) {
            return EClass.pc;
        }

        if (barrel.GetRootCard() is Chara holder && PlayerKarma.PlayerBehind(holder) is { } carrier) {
            return carrier;
        }

        return Teams.SoloOwnerOf(EClass._zone) is > 0 and var solo &&
               EClass.game.cards.globalCharas.Find(solo) is { } landOwner
            ? landOwner
            : EClass.pc;
    }
}
