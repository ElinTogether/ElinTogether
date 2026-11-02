using System;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(Party), nameof(Party.RemoveMember))]
internal static class PartyMemberEvent
{
    [HarmonyPrefix]
    internal static bool OnRemoveMember(Chara c)
    {
        switch (NetSession.Instance.Connection) {
            case ElinNetHost host:
                if (c.IsPlayer) {
                    return true;
                }

                // maybe open a context here
                host.Delta.AddRemote(new PartyMemberDelta {
                    Member = c,
                    CaptureSource = c,
                });
                return true;
            case ElinNetClient client:
                if (c.IsPlayer || PendingUid.IsPending(c.uid)) {
                    return true;
                }

                // client drops local sim
                if (!ElinDelta.IsRemoteStateLanding) {
                    client.Delta.AddRemote(new PartyMemberDelta {
                        Member = c,
                    });
                }

                return false;
            default:
                return true;
        }
    }

    extension(Party party)
    {
        [HarmonyReversePatch(HarmonyReversePatchType.Snapshot)]
        internal void Stub_RemoveMember(Chara c)
        {
            throw new NotImplementedException("Party.RemoveMember");
        }

        /// <summary>
        ///     The removal another game decided, played here. The stub runs no postfix: CompanionAllyLimitPatch's
        ///     refreshes the speed of the members left (the exceeded party malus of the owner's companions), which a
        ///     guest's game otherwise kept as it was until something else refreshed them
        /// </summary>
        internal void RemoveMemberHere(Chara c)
        {
            party.Stub_RemoveMember(c);
            CompanionAllyLimitPatch.OnRemoveMember(party);
        }
    }
}