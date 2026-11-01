using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     Asking a resident of the base to join the party (its dialog, the list of residents) adds it to the party
///     without making it an ally again, so it never went through <see cref="CharaMakeAllyEvent" />: the join
///     stayed in the game of the player who asked, and the resident followed nobody. <br />
///     A client asks the host, the host adds it for that player and tells everyone
/// </summary>
[HarmonyPatch]
internal static class PartyJoinEvent
{
    private static int _makingAlly;

    // Chara.MakeAlly adds to the party itself and is told as a whole
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(Chara), nameof(Chara.MakeAlly))]
    internal static void OnMakeAlly()
    {
        _makingAlly++;
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Chara), nameof(Chara.MakeAlly))]
    internal static void OnMakeAllyEnd()
    {
        _makingAlly--;
    }

    // the host runs the task of a remote player on its copy of that player (taming with a brush): an ally made
    // during its turn is that player's
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.Tick))]
    internal static void OnRemotePlayerTick(Chara __instance, out Chara? __state)
    {
        __state = CharaMakeAllyEvent.ActingRemotePlayer;
        if (NetSession.Instance.Connection is ElinNetHost && __instance.GetBool("remote_chara")) {
            CharaMakeAllyEvent.ActingRemotePlayer = __instance;
        }
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Chara), nameof(Chara.Tick))]
    internal static void OnRemotePlayerTickEnd(Chara? __state)
    {
        CharaMakeAllyEvent.ActingRemotePlayer = __state;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Party), nameof(Party.AddMemeber))]
    internal static bool OnAddMember(Party __instance, Chara c, bool showMsg)
    {
        if (c is null || c.party == __instance || c.IsPC || c.GetBool("remote_chara") || __instance != EClass.pc?.party) {
            return true;
        }

        var session = NetSession.Instance;

        // asked on our own (travelling alone, or holding a map): ours, not the host's
        if (session is { IsAway: true, Connection: not ElinNetClient } && !ElinDelta.IsApplying && c.CompanionOwnerUid == 0) {
            c.SetCompanionOwner(EClass.pc);
        }

        // the host adds it for a player who rides it (ActRide adds its mount to the party itself): it follows
        // that player. Only the mount: a companion of the host a guest brings back to life stays the host's
        if (_makingAlly == 0 && c.CompanionOwnerUid == 0 && session.Connection is ElinNetHost riding &&
            CharaMakeAllyEvent.Recruiter(c) is { } rider && (rider.ride == c || rider.parasite == c)) {
            c.SetCompanionOwner(rider);
            // told here: nothing else tells it while a player's delta is being applied
            riding.Delta.AddRemote(new CharaMakeAllyDelta {
                Owner = c,
                ShowMsg = showMsg,
                TemporaryAllyName = c.c_altName,
                OwnerUid = c.CompanionOwnerUid,
                JoinOnly = true,
            });
            return true;
        }

        if (_makingAlly > 0 || ElinDelta.IsApplying) {
            return true;
        }

        switch (session.Connection) {
            case ElinNetHost host:
                // the host takes it for itself: a player's companion sent away keeps its owner (PartyMemberDelta),
                // whoever takes it again is its owner from then on
                // (not when a guest is the one recruiting through the host's party: R2 of recruit_suite)
                // (nor when "the player" is a guest standing in, RemoteCraft.AsCrafter: a guest's revive of a companion
                // asked Recruiter, which never names "the player", and handed the companion to the host, GitHub issue 60;
                // a revived companion keeps its owner, see RemoteRevivePatch)
                if (!session.IsAway && c.CompanionOwnerUid != 0 && !RemoteCraft.IsStandingIn &&
                    CharaMakeAllyEvent.Recruiter(c) is null) {
                    c.SetCompanionOwner(null);
                }

                host.Delta.AddRemote(new CharaMakeAllyDelta {
                    Owner = c,
                    ShowMsg = showMsg,
                    TemporaryAllyName = c.c_altName,
                    OwnerUid = c.CompanionOwnerUid,
                    JoinOnly = true,
                });
                return true;
            case ElinNetClient client when !PendingUid.IsPending(c.uid):
                // we are clients, drop the update and wait for delta
                client.Delta.AddRemote(new CharaMakeAllyRequestDelta {
                    Owner = c,
                    LocalCardId = null,
                    ShowMsg = showMsg,
                    JoinOnly = true,
                });
                return false;
            default:
                return true;
        }
    }
}
