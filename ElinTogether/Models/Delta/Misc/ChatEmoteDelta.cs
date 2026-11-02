using System;
using System.Collections.Generic;
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

    // peer -> when its last emote was shown: one a second at most (the line itself has the chat's flood limit)
    private static readonly Dictionary<int, float> LastByPeer = [];

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

            LastByPeer[OriginPeer] = Time.realtimeSinceStartup;
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
