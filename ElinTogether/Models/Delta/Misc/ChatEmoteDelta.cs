using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.Net;
using MessagePack;
using UnityEngine;

namespace ElinTogether.Models;

/// <summary>
///     A chat line made of one emote (":happy:" in the Chat tab): the game's emote over the speaker's character, as
///     the game shows a character's mood (Card.ShowEmo), for the players on that map. The line itself travels as any
///     chat line
/// </summary>
[MessagePackObject]
public class ChatEmoteDelta : ElinDelta
{
    [Key(0)]
    public int SpeakerUid { get; init; }

    [Key(1)]
    public int Emo { get; init; }

    // peer -> when its last emote was shown: one a second at most (the line itself has the chat's flood limit); a peer
    // is forgotten when it leaves (ElinNetHost) and old entries when a new one comes, so the table never grows
    private static readonly Dictionary<int, float> LastByPeer = [];

    internal static void Forget(int peer) => LastByPeer.Remove(peer);

    /// <summary>The speaker's own character, one of the game's emotes</summary>
    internal override string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        if (Emo <= 0 || Emo >= Enum.GetValues(typeof(Emo)).Length) {
            return $"emote {Emo}";
        }

        return sender?.uid == SpeakerUid ? null : $"an emote over {SpeakerUid}, not the sender";
    }

    /// <summary>This game's own character shows it and tells the players of its map</summary>
    internal static void Send(Emo emo)
    {
        EClass.pc.ShowEmo(emo, 0f, false);
        NetSession.Instance.Connection?.Delta.AddRemote(new ChatEmoteDelta { SpeakerUid = EClass.pc.uid, Emo = (int)emo });
    }

    protected override void OnApply(ElinNetBase net)
    {
        if (net is ElinNetHost from && FromPeer) {
            if (from.FromMuted(OriginPeer) || (LastByPeer.TryGetValue(OriginPeer, out var last) && Time.realtimeSinceStartup - last < 1f)) {
                return;
            }

            var now = Time.realtimeSinceStartup;
            foreach (var old in LastByPeer.Where(p => now - p.Value > 60f).Select(p => p.Key).ToList()) {
                LastByPeer.Remove(old);
            }

            LastByPeer[OriginPeer] = now;
        }

        // relay to the players of this map
        if (net.IsHost) {
            net.Delta.AddRemote(this);
        }

        if (SpeakerUid == EClass.pc.uid || PlayerSocial.IsIgnoredUid(SpeakerUid) ||
            EClass._map?.charas.Find(c => c.uid == SpeakerUid) is not { } chara) {
            return;
        }

        chara.ShowEmo((Emo)Emo, 0f, false);
    }
}
