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
    }
}