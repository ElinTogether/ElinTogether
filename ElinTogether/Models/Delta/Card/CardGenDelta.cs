using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper.Extensions;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CardGenDelta : ElinDelta
{
    private static readonly HashSet<int> _createdInCurrentFrame = [];

    [Key(0)]
    public required RemoteCard Card { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (net.IsHost) {
            EmpLog.Warning("Refusing CardGenDelta from peer {PeerIndex}, uid {Uid}",
                OriginPeer, Card.Uid);
            return;
        }

        if (Card.Data is null) {
            EmpLog.Warning("Dropping {DeltaType}, uid {Uid} has no data attached",
                nameof(CardGenDelta), Card.Uid);
            return;
        }

        if (CardCache.Find(Card.Uid) is { } cached) {
            var sameKind = cached is Thing == (Card.Type == RemoteCard.CardType.Thing);
            if (sameKind && !cached.isDestroyed) {
                return;
            }

            // host card uid somehow falls into our local uid space
            EmpLog.Warning("Uid {Uid} taken by client local card, evicting for host {CardType}",
                Card.Uid, Card.Type);
            if (!cached.isDestroyed) {
                cached.Destroy();
            }
            CardCache.Remove(Card.Uid);
        }

        if (Card.Type == RemoteCard.CardType.Chara &&
            game.cards.globalCharas.GetValueOrDefault(Card.Uid) is { } existing) {
            CardCache.Add(existing);
            return;
        }

        var card = Card.Data.Decompress<Card>();
        card.uid = Card.Uid;

        IEnumerable<Card> subtree = card.things.Flatten();
        game.cards.uidNext = subtree
            .Select(node => node.uid)
            .Prepend(Math.Max(card.uid, game.cards.uidNext))
            .Max();

        CardCache.Add(card);
        CardCache.CacheContainer(card.things);
        CardCache.KeepAlive(card);
    }

    internal static CardGenDelta Create(Card card)
    {
        var remoteCard = RemoteCard.Create(card, true);
        _createdInCurrentFrame.Add(remoteCard.Uid);

        return new() {
            Card = remoteCard,
        };
    }

    protected override bool OnRefresh()
    {
        var card = Card.Find();
        if (card is null || card.isDestroyed) {
            return false;
        }

        if (card.parent is Card parent) {
            if (_createdInCurrentFrame.Contains(parent.uid)) {
                return false;
            }
        } else if (card.parent is null && card.things.Count == 0 && !card.IsKeptAlive) {
            return false;
        }

        Card.Data = LZ4Bytes.Create(card);
        return true;
    }

    internal static bool WasCreatedThisFrame(int uid)
    {
        return _createdInCurrentFrame.Contains(uid);
    }

    internal static void ClearRecordedUids()
    {
        _createdInCurrentFrame.Clear();
    }
}