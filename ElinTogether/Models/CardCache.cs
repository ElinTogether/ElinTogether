using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.Helper.Extensions;
using ElinTogether.Net;

namespace ElinTogether.Models;

public static class CardCache
{
    private static readonly Dictionary<int, WeakReference<Card>> _cards = [];
    // prevent temporary item cache invalidation
    private static readonly List<Card> _keepalive = [];

    private static readonly List<Card> _invalidCards = [];

    // dead weak entries are swept once a second, into a reused list: no per-frame copy of the table
    private static readonly List<int> _deadUids = [];
    private static float _nextSweep;

    /// <summary>Entries of the table, cards kept alive, cards waiting to be destroyed (the server's memory log)</summary>
    internal static (int Cards, int KeptAlive, int Invalid) Counts => (_cards.Count, _keepalive.Count, _invalidCards.Count);

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
            EmpLog.Warning(
                "Card uid conflict: uid {Uid} held by local {LocalCardId} ({LocalCardNum}), refusing incoming {CardId}",
                card.uid, stored.id, stored.Num, card.id);
            return;
        }

        // reallocate uid: a fresh one from the game's counter (robustness audit 2026-11-01: uid + 1 again and again
        // walked onto the next cards' uids, renumbering them in turn). The global one keeps its number
        var moved = stored.IsGlobal || !card.IsGlobal ? card : stored;
        if (moved == stored) {
            Set(card);
        }

        var old = moved.uid;
        // (the counter is above any uid seen, a card from a lease reported above it is the only way to hit one)
        for (var i = 0; i < 16 && (i == 0 || Find(moved.uid) is { } other && other != moved); i++) {
            EClass.game.cards.AssignUID(moved);
        }

        Set(moved);

        // the players' games may know this card by its old number: what they send about it now reaches the other one
        EmpLog.Warning(
            "Card uid conflict: uid {Uid} held by {StoredId}, {MovedId} renumbered to {NewUid}; a client that knew it by the old " +
            "uid keeps that number until it is sent the card again",
            old, (moved == card ? stored : card).id, moved.id, moved.uid);
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

    internal static bool TryAdopt(Card? card)
    {
        if (card is null || PendingUid.IsPending(card.uid)) {
            return false;
        }

        if (Find(card.uid) is { } cached) {
            return cached == card;
        }

        if (card.GetRootCard() is not Chara { IsPC: true }) {
            return false;
        }

        EmpLog.Warning("Adopting uncached {CardId} {Uid} from pc into card cache",
            card.id, card.uid);
        Set(card);
        return true;
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

        CacheGlobalContainers();
    }

    // off map
    internal static void CacheGlobalContainers()
    {
        // (and the boxes of each owner, see TeamBoxes)
        var containers = new List<Thing?> {
            EClass.game?.cards.container_shipping,
            EClass.game?.cards.container_deliver,
            EClass.game?.cards.container_deposit,
        };
        containers.AddRange(TeamBoxes.All());

        foreach (var container in containers) {
            if (container is null) {
                continue;
            }

            Set(container);
            foreach (var thing in container.things.Flatten()) {
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

    internal static void UndoDestroy(Card card)
    {
        _invalidCards.Remove(card);
    }

    /// <summary>
    ///     Forget every card before a world is torn down: what it destroys is no host card anymore
    /// </summary>
    internal static void Reset()
    {
        ClearCachedRefs();
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
        CharaMoveDelta.ClearRecentMoves();
    }

    [ElinPreLoad]
    private static void ClearCachedRefs(GameIOContext context)
    {
        ClearCachedRefs();
    }

    [ElinPostSceneInit]
    private static void ClearCachedRefs(Scene.Mode mode)
    {
        // None: game torn down before rejoining from independent travel
        if (mode is Scene.Mode.Title or Scene.Mode.None) {
            ClearCachedRefs();
        }
    }

    public static void Update()
    {
        _keepalive.RemoveAll(card => card.parent is not null || card.isDestroyed);
        _invalidCards.ForEach(card => card.Destroy());
        _invalidCards.Clear();

        var now = UnityEngine.Time.unscaledTime;
        if (now < _nextSweep) {
            return;
        }

        _nextSweep = now + 1f;

        // a dead entry already reads as null in Find, so one second of delay changes nothing
        _deadUids.Clear();
        foreach (var (uid, reference) in _cards) {
            if (!reference.TryGetTarget(out _)) {
                _deadUids.Add(uid);
            }
        }

        foreach (var uid in _deadUids) {
            _cards.Remove(uid);
        }

        _deadUids.Clear();
    }

    extension(Card? card)
    {
        internal bool IsKeptAlive => _keepalive.Contains(card);
        internal bool IsHostOwned => card is not null && !PendingUid.IsPending(card.uid) && Find(card.uid) == card;
        internal bool IsResolved => NetSession.Instance.IsClient && card.IsHostOwned;
    }
}