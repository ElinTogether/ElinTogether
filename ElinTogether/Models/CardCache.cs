using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper.Extensions;
using ElinTogether.Net;

namespace ElinTogether.Models;

public static class CardCache
{
    private static readonly Dictionary<int, WeakReference<Card>> _cards = [];
    // prevent temporary item cache invalidation
    private static readonly List<Card> _keepalive = [];

    private static readonly List<Card> _invalidCards = [];

    internal static void Add(Card card)
    {
        var stored = Find(card.uid);
        if (stored is null) {
            Set(card);
            return;
        }

        if (stored == card) {
            return;
        }

        if (NetSession.Instance.IsClient) {
            // if there is nothing wrong, this shouldn't happen
            EmpLog.Warning("Card uid conflict: uid {Uid} held by local {LocalCardId} ({LocalCardNum}), refusing incoming {CardId}",
                card.uid, stored.id, stored.Num, card.id);
            return;
        }

        // reallocate uid
        if (stored.IsGlobal || !card.IsGlobal) {
            card.uid++;
            Add(card);
            EClass.game.cards.uidNext = Math.Max(card.uid, EClass.game.cards.uidNext);
            return;
        }

        Set(card);

        stored.uid++;
        Add(stored);
        EClass.game.cards.uidNext = Math.Max(stored.uid, EClass.game.cards.uidNext);
    }

    internal static void Set(Card card)
    {
        if (card.uid < 0) {
            EmpLog.Warning("Added card with negative uid");
            return;
        }

        _cards[card.uid] = new(card);
    }

    internal static void Remove(int uid)
    {
        _cards.Remove(uid);
    }

    internal static void Rebind(Card card, int uid)
    {
        _cards.Remove(card.uid);
        card.uid = uid;
        _cards[uid] = new(card);

        EClass.game.cards.uidNext = Math.Max(EClass.game.cards.uidNext, uid + 1);
    }

    internal static bool Contains(Card? card)
    {
        return card is not null && Find(card.uid) == card;
    }

    internal static Card? Find(int uid)
    {
        if (uid > 0 && _cards.TryGetValue(uid, out var reference)) {
            reference.TryGetTarget(out var card);
            return card;
        }

        return null;
    }

    internal static void CacheCurrentZone()
    {
        if (EClass.game?.activeZone?.map is not { } map) {
            return;
        }

        foreach (var card in map.Cards) {
            Set(card);
            foreach (var thing in card.things.Flatten()) {
                Set(thing);
            }
        }
    }

    internal static void CacheContainer(ThingContainer container)
    {
        foreach (var thing in container.Flatten()) {
            Add(thing);
        }
    }

    internal static void KeepAlive(Card card)
    {
        if (card.parent is null && !_keepalive.Contains(card)) {
            _keepalive.Add(card);
        }
    }

    internal static void DelayDestroy(Card card)
    {
        _invalidCards.Add(card);
    }

    private static void ClearCachedRefs()
    {
        _cards.Clear();
        _keepalive.Clear();
        _invalidCards.Clear();
        PendingContext.Reset();
        PendingRebind.Clear();
        PendingSplit.Clear();
        ThingRequest.Clear();
    }

    [ElinPreLoad]
    private static void ClearCachedRefs(GameIOContext context)
    {
        ClearCachedRefs();
    }

    [ElinPostSceneInit]
    private static void ClearCachedRefs(Scene.Mode mode)
    {
        if (mode == Scene.Mode.Title) {
            ClearCachedRefs();
        }
    }

    public static void Update()
    {
        _keepalive.RemoveAll(card => card.parent is not null);
        _invalidCards.ForEach(card => card.Destroy());
        _invalidCards.Clear();

        foreach (var (uid, reference) in _cards.ToArray()) {
            if (!reference.TryGetTarget(out _)) {
                _cards.Remove(uid);
            }
        }
    }

    extension(Card? card)
    {
        internal bool IsKeptAlive => _keepalive.Contains(card);
        internal bool IsHostOwned => card is not null && !PendingUid.IsPending(card.uid) && Find(card.uid) == card;
        internal bool IsResolved => NetSession.Instance.IsClient && card.IsHostOwned;
    }
}