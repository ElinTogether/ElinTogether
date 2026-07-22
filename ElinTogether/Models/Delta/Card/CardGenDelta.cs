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
    [Key(0)]
    public required RemoteCard Card { get; init; }

    private static readonly HashSet<int> _createdInCurrentFrame = [];

    protected override void OnApply(ElinNetBase net)
    {
        if (Card.Data is null) {
            return;
        }

        var maxId = Math.Max(Math.Abs(Card.Uid), game.cards.uidNext);
        Card card = Card.Type == RemoteCard.CardType.Thing
            ? Card.Data.Decompress<Thing>()
            : Card.Data.Decompress<Chara>();

        maxId = card.things
            .Flatten()
            .Select(thing => thing.uid)
            .Prepend(maxId)
            .Max();

        game.cards.uidNext = maxId;

        CardCache.Add(card);
        CardCache.CacheContainer(card.things);
    }

    internal static CardGenDelta Create(Card card)
    {
        var remoteCard = RemoteCard.Create(card, addToCache: true);
        _createdInCurrentFrame.Add(remoteCard.Uid);

        return new CardGenDelta {
            Card = remoteCard,
        };
    }

    internal override bool OnRefresh()
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

    internal static void ClearRecordedUids()
    {
        _createdInCurrentFrame.Clear();
    }
}