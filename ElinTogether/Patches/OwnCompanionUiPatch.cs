using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using ElinTogether.Helper;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     Every player's companions are in the one party of the session: each player saw the other's in its party
///     bar, and could send it home from its dialog (GitHub issue 11, a Golden Knight in both parties). A player's
///     bar and dialog show its own companions only; the other players themselves stay in the bar
/// </summary>
[HarmonyPatch]
internal static class OwnCompanionUiPatch
{
    /// <summary>
    ///     A companion of another player. Transport, not Connection: a guest alone in an away zone has no
    ///     Connection (it plays that zone as single player) but the party still holds the other players'
    ///     companions, and its bar listed the host's (GitHub issue 36)
    /// </summary>
    internal static bool IsOthers(Chara c)
    {
        return NetSession.Instance.Transport is not null && !c.IsPC && !c.GetBool("remote_chara") &&
               CompanionHelper.OwnerOf(c) is { } owner && owner != EClass.pc;
    }

    // (the server's stand-in leads the session's party but is nobody's to see: it stood in every player's bar)
    [HarmonyPrefix]
    [HarmonyPatch(typeof(WidgetRoster), nameof(WidgetRoster.Add))]
    internal static bool OnRosterAdd(Chara c)
    {
        return !IsOthers(c) && !ServerAvatar.Is(c);
    }

    // "c.IsPCParty && !c.isSummon": the block that offers "make home" and "wait at your home"
    [HarmonyTranspiler]
    [HarmonyPatch(typeof(DramaCustomSequence), nameof(DramaCustomSequence.Build))]
    internal static IEnumerable<CodeInstruction> OnBuild(IEnumerable<CodeInstruction> instructions)
    {
        var isPcParty = AccessTools.PropertyGetter(typeof(Card), nameof(Card.IsPCParty));
        var isSummon = AccessTools.PropertyGetter(typeof(Card), nameof(Card.isSummon));
        var list = instructions.ToList();
        for (var i = 0; i < list.Count; i++) {
            if (list[i].Calls(isPcParty) && list.Skip(i + 1).Take(4).Any(x => x.Calls(isSummon))) {
                list[i] = CodeInstruction.Call(typeof(OwnCompanionUiPatch), nameof(IsOwnPartyMember)).MoveLabelsFrom(list[i]);
                break;
            }
        }

        return list;
    }

    internal static bool IsOwnPartyMember(Card c)
    {
        return c.IsPCParty && !(c is Chara chara && IsOthers(chara));
    }
}
