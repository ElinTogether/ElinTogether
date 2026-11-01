using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     What the party learns together reaches every player, see <see cref="PartyExpDelta" />
/// </summary>
[HarmonyPatch(typeof(Party), nameof(Party.ModExp))]
internal static class PartyExpEvent
{
    [HarmonyPostfix]
    internal static void OnPartyExp(Party __instance, int ele, int a)
    {
        if (ElinDelta.IsApplying || a <= 0 || __instance != EClass.pc?.party ||
            NetSession.Instance.Connection is not { } connection) {
            return;
        }

        connection.Delta.AddRemote(new PartyExpDelta { Element = ele, Amount = a, Party = true });
    }
}
