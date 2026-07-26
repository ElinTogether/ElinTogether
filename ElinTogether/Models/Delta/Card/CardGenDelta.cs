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
        if (Card.Data is null || CardCache.Find(Card.Uid) is not null) {
            return;
        }

        var card = Card.Data.Decompress<Card>();

        if (net.IsHost && PendingUid.IsPending(Card.Uid)) {
            Rebind(net, card);
        } else {
            card.uid = Card.Uid;
        }

        IEnumerable<Card> subtree = card.things.Flatten();
        game.cards.uidNext = subtree
            .Select(node => node.uid)
            .Prepend(Math.Max(card.uid, game.cards.uidNext))
            .Max();

        CardCache.Add(card);
        CardCache.CacheContainer(card.things);
        CardCache.KeepAlive(card);
    }

    private void Rebind(ElinNetBase net, Card card)
    {
        IEnumerable<Card> thingies = card.things.Flatten();
        var rebinds = new List<CardUidRebindDelta.UidBind>();

        foreach (var node in thingies.Prepend(card)) {
            if (!PendingUid.IsPending(node.uid)) {
                continue;
            }

            var pending = node.uid;
            game.cards.AssignUID(node);

            PendingRebind.Bind(pending, node.uid);
            _createdInCurrentFrame.Add(node.uid);
            rebinds.Add(new() {
                Pending = pending,
                Real = node.uid,
            });
        }

        Card.Uid = card.uid;

        net.Delta.AddRemote(new CardUidRebindDelta {
            Rebinds = rebinds,
        });

        net.Delta.AddRemote(this);
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

    internal static void ClearRecordedUids()
    {
        _createdInCurrentFrame.Clear();
    }
}