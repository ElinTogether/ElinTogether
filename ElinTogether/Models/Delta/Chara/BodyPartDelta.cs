using ElinTogether.Net;
using HarmonyLib;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     GitHub issue 36 (a mutant's extra limbs): the feat gives a random body part in the game of the player who took
///     it (Feat.Apply, Chara.AddRandomBodyPart), the others only got the feat's level. The host's copy had no new limb
///     and the next world copy took it back from the player (the point stayed spent). The part the player's game drew
///     goes to every game, through the host, the same for one lost when the feat goes down
/// </summary>
[MessagePackObject]
public class BodyPartDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Player { get; init; }

    /// <summary>the part added, 0 = the last one removed</summary>
    [Key(1)]
    public int Element { get; init; }

    /// <summary>how many parts the player's body had after it: a game already there skips it</summary>
    [Key(2)]
    public int Count { get; init; }

    private static readonly int[] Parts = [30, 31, 33, 35, 36, 34, 37, 39];

    protected override void OnApply(ElinNetBase net)
    {
        if (Player.Find() is not Chara chara || (Element != 0 && System.Array.IndexOf(Parts, Element) < 0)) {
            return;
        }

        if (net is ElinNetHost host) {
            if (!host.ActiveRemoteCharas.TryGetValue(OriginPeer, out var sender) || sender != chara) {
                return;
            }

            host.Delta.AddRemote(this);
        }

        var slots = chara.body.slots.Count;
        if (Element != 0 && slots == Count - 1) {
            chara.body.AddBodyPart(Element);
        } else if (Element == 0 && slots == Count + 1) {
            chara.body.RemoveBodyPartAt(slots - 1);
        } else {
            EmpLog.Debug("Body part {Element} of {Uid} not taken: {Slots} part(s) here, {Count} told", Element, chara.uid, slots, Count);
            return;
        }

        chara.body.RefreshBodyParts();
        if (chara.IsPC) {
            WidgetEquip.OnChangeBodyPart();
        }
    }

    private static void Tell(Chara chara, int element)
    {
        if (IsApplying || !EClass.core.IsGameStarted || chara != EClass.pc || NetSession.Instance.Connection is not { } net) {
            return;
        }

        net.Delta.AddRemote(new BodyPartDelta { Player = chara, Element = element, Count = chara.body.slots.Count });
    }

    [HarmonyPatch]
    internal static class Watch
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Chara), nameof(Chara.AddRandomBodyPart))]
        internal static void OnAdd(Chara __instance)
        {
            if (__instance.body.slots.LastItem() is { } slot) {
                Tell(__instance, slot.elementId);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Chara), nameof(Chara.RemoveLastBodyPart))]
        internal static void OnRemove(Chara __instance)
        {
            Tell(__instance, 0);
        }
    }
}
