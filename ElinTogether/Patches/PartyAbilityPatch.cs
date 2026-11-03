using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     GitHub issue 72: a party ability (Absorb Mana, the party heals and buffs: ForceParty, or a spell cast on the
///     whole party) is cast on every member of the player's party in sight (Chara.UseAbility's ForeachParty), and
///     the game takes them all to be on this map. In a shared world the party holds the other players and their
///     companions, who can be on other maps: a member whose point (on its own map) passed the sight test was cast
///     on at that point, out of this map's cells (IndexOutOfRangeException, the cast lost), else cast on from another
///     map and counted in the cost. Only the members on this map are in sight
/// </summary>
[HarmonyPatch]
internal static class PartyAbilityPatch
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        // the local function ForeachParty of Chara.UseAbility(Act, Card, Point, bool), on its closure class
        return typeof(Chara).GetNestedTypes(AccessTools.all).Prepend(typeof(Chara))
            .SelectMany(t => t.GetMethods(AccessTools.all))
            .Where(mi => mi.Name.StartsWith("<UseAbility>g__ForeachParty"));
    }

    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> OnForeachParty(IEnumerable<CodeInstruction> instructions)
    {
        var canSee = AccessTools.Method(typeof(Chara), nameof(Chara.CanSeeLos), [typeof(Card), typeof(int)]);
        var host = AccessTools.Field(typeof(Chara), nameof(Chara.host));
        foreach (var code in instructions) {
            // "chara.host != null" lets a ridden member in without the sight test: not one on another map
            if (code.LoadsField(host)) {
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PartyAbilityPatch), nameof(HostHere))) {
                    labels = code.labels,
                    blocks = code.blocks,
                };
                continue;
            }

            if (code.Calls(canSee)) {
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PartyAbilityPatch), nameof(SeesHere))) {
                    labels = code.labels,
                    blocks = code.blocks,
                };
                continue;
            }

            yield return code;
        }
    }

    internal static Chara? HostHere(Chara c)
    {
        // (a ridden mount is off the map's cards: its rider tells where it is)
        return NetSession.Instance.Transport is not null && c.host is { } rider && rider.parent != EClass._zone ? null : c.host;
    }

    // (Transport, not Connection: a player away on a map of its own has no Connection, and its party holds the others)
    internal static bool SeesHere(Chara self, Card c, int dist)
    {
        if (NetSession.Instance.Transport is not null && c.parent != EClass._zone) {
            return false;
        }

        return self.CanSeeLos(c, dist);
    }
}
