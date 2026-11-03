using System;
using System.Collections.Concurrent;
using System.Linq;
using ElinTogether.Net;

namespace ElinTogether.Helper;

internal static class RemoteCardHelper
{
    internal static readonly ConcurrentDictionary<Chara, RemoteCharaNetProfile> RemoteCardNetProfile = [];

    [ElinPreLoad]
    private static void ClearProfiles(GameIOContext context)
    {
        RemoteCardNetProfile.Clear();
    }

    [ElinPreSceneInit]
    private static void ClearProfiles(Scene.Mode mode)
    {
        // None: game torn down before rejoining from independent travel
        if (mode is Scene.Mode.Title or Scene.Mode.None) {
            RemoteCardNetProfile.Clear();
        }
    }

    extension(Chara chara)
    {
        internal RemoteCharaNetProfile NetProfile => RemoteCardNetProfile.GetOrAdd(chara, chara => new());

        internal bool IsRemotePlayer
        {
            get {
                switch (NetSession.Instance.Connection) {
                    case ElinNetHost host:
                        return host.ActiveRemoteCharas.ContainsValue(chara);
                    case ElinNetClient:
                        if (chara.IsPC) {
                            return false;
                        }

                        // (no closure: asked for many cards each frame)
                        var players = NetSession.Instance.CurrentPlayers;
                        for (var i = 0; i < players.Count; i++) {
                            if (players[i].CharaUid == chara.uid) {
                                return true;
                            }
                        }

                        return false;
                    default:
                        return false;
                }
            }
        }

        internal bool IsPlayer => chara.IsPC || chara.IsRemotePlayer;

        internal bool IsInActiveMap => chara.currentZone == EClass._zone && chara.pos is { IsValid: true };

        internal bool IsActiveRemoteChara => NetSession.Instance.Connection is ElinNetHost host &&
                                             host.ActiveRemoteCharas.ContainsValue(chara);

        internal NetPeerState? RemoteState
        {
            get {
                // (read from hot getters, Chara.Speed among them: a plain loop, no closure)
                var players = NetSession.Instance.CurrentPlayers;
                var uid = chara.uid;
                for (var i = 0; i < players.Count; i++) {
                    if (players[i].CharaUid == uid) {
                        return players[i];
                    }
                }

                return null;
            }
        }
    }

    internal class RemoteCharaNetProfile
    {
        public WeakReference<Thing> RemoteMainHand { get; set; } = new(null!, false);
        public WeakReference<Thing> RemoteOffHand { get; set; } = new(null!, false);
        public int LastPrayedDay { get; set; } = -1;
    }
}