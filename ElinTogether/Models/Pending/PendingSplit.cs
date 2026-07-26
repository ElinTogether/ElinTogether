using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace ElinTogether.Models;

internal static class PendingSplit
{
    private const int MaxTracked = 1 << 8;

    private static readonly Dictionary<int, int> _origins = [];
    private static readonly Queue<int> _order = [];

    internal static void Record(Card shadow, Card source)
    {
        if (!_origins.ContainsKey(shadow.uid)) {
            _order.Enqueue(shadow.uid);
        }

        _origins[shadow.uid] = Resolve(source.uid) is var origin and > 0 ? origin : source.uid;

        while (_order.Count > MaxTracked) {
            _origins.Remove(_order.Dequeue());
        }
    }

    internal static int Resolve(int shadow)
    {
        return PendingUid.IsPending(shadow) ? _origins.GetValueOrDefault(shadow, 0) : 0;
    }

    [return: NotNullIfNotNull(nameof(card))]
    internal static RemoteCard? Split(Card? card)
    {
        if (card is null) {
            return null;
        }

        if (Resolve(card.uid) is not (var origin and > 0) || CardCache.Find(origin) is not { } source) {
            return RemoteCard.Create(card);
        }

        var remote = RemoteCard.Create(source);
        remote.Num = card.Num;
        return remote;
    }

    internal static void Clear()
    {
        _origins.Clear();
        _order.Clear();
    }
}
