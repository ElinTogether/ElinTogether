using System.Linq;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     A fertilized egg hatches in the game that runs its map (a hot spring: TraitFoodEgg.Update, a bed in a base:
///     FactionBranch.OnSimulateHour), and the chick goes to "the player": the branch of the zone or the player's home,
///     the player's party (TraitFoodEggFertilized.Incubate). On the host's map that was the host, for a guest's egg
///     too. The egg keeps the number of the player who last put it down, and its chick is that player's: its home
///     when the zone has no branch, its companion in the party, the message to it. An incubator run for a guest
///     (AIUseCrafterPatch) already has the guest standing in: only the companion's owner was missing there
/// </summary>
[HarmonyPatch]
internal static class EggOwnerPatch
{
    // on a fertilized egg: the character number of the player who last put it down
    internal const string OwnerKey = "emp_egg_owner";

    private static Chara? _hatchFor;

    private static ElinNetHost? Host => NetSession.Instance.Connection as ElinNetHost;

    /// <summary>
    ///     Put down on a map, here or for a guest (ZoneAddCardDelta): whoever held it is its owner from now on
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Zone), nameof(Zone.AddCard), typeof(Card), typeof(int), typeof(int))]
    internal static void OnAddCard(Card t)
    {
        if (t is not Thing { trait: TraitFoodEggFertilized } || Host is null) {
            return;
        }

        var holder = t.parent is Card ? t.GetRootCard() as Chara : null;
        if (holder is not { IsPlayer: true } && ElinDelta.IsApplying && CharaMakeAllyEvent.Actor is { IsRemotePlayer: true } actor) {
            holder = actor;
        }

        if (holder is { IsPlayer: true }) {
            t.SetInt(OwnerKey, holder.uid);
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(TraitFoodEggFertilized), nameof(TraitFoodEggFertilized.Incubate))]
    internal static void OnIncubate(Thing egg, Card? incubator, out ScopeExit? __state)
    {
        __state = null;
        if (Host is not { } host || egg.isNPCProperty) {
            return;
        }

        // the incubator of a guest: it stands in already
        if (EClass.pc is { IsRemotePlayer: true } standing) {
            __state = For(standing, null, null);
            return;
        }

        var uid = egg.GetInt(OwnerKey);
        if (uid == 0 || uid == EClass.pc.uid ||
            host.ActiveRemoteCharas.Values.FirstOrDefault(c => c.uid == uid) is not { isDead: false } owner) {
            return;
        }

        // its home when the zone has none (a guest with no home: the game's way, the host's home, but its companion)
        var standIn = EClass.Branch is not null || owner.homeBranch is not null ? RemoteCraft.AsCrafter(owner, false) : null;
        __state = For(owner, standIn, MsgRelayContext.RedirectTo(owner));
    }

    private static ScopeExit For(Chara owner, ScopeExit? standIn, ScopeExit? told)
    {
        var previous = _hatchFor;
        _hatchFor = owner;
        var gift = CharaMakeAllyEvent.GiftsFor(owner);
        return new() {
            OnExit = () => {
                gift.Dispose();
                told?.Dispose();
                standIn?.Dispose();
                _hatchFor = previous;
            },
        };
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(TraitFoodEggFertilized), nameof(TraitFoodEggFertilized.Incubate))]
    internal static void OnIncubateEnd(ScopeExit? __state)
    {
        __state?.Dispose();
    }

    /// <summary>
    ///     The chick joins the party (out of a base): it follows the egg's owner. Before PartyJoinEvent, which tells it
    ///     with its owner (and keeps it, as the owner is the recruiter of the moment, see GiftsFor)
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    [HarmonyPatch(typeof(Party), nameof(Party.AddMemeber))]
    internal static void OnAddMember(Chara c)
    {
        if (_hatchFor is { } owner && c is not null && c != owner && !c.IsPlayer && c.CompanionOwnerUid == 0) {
            c.SetCompanionOwner(owner);
        }
    }
}
