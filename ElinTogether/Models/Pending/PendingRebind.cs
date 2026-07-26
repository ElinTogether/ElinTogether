using System.Collections.Generic;
using System.Linq;

namespace ElinTogether.Models;

internal static class PendingRebind
{
    private const int MaxPending = 1 << 10;

    private static readonly Dictionary<int, int> _binds = [];
    private static readonly Queue<int> _order = [];

    internal static void Bind(int pending, int real)
    {
        if (!_binds.ContainsKey(pending)) {
            _order.Enqueue(pending);
        }

        _binds[pending] = real;

        while (_order.Count > MaxPending) {
            _binds.Remove(_order.Dequeue());
        }
    }

    internal static int Resolve(int pending)
    {
        return _binds.GetValueOrDefault(pending, 0);
    }

    internal static void ReleasePeer(int peer)
    {
        var kept = _order
            .Where(uid => PendingUid.GetPeerIndex(uid) != peer)
            .Select(uid => (provisional: uid, real: _binds[uid]))
            .ToArray();

        if (kept.Length == _order.Count) {
            return;
        }

        _binds.Clear();
        _order.Clear();
        foreach (var (provisional, real) in kept) {
            _binds[provisional] = real;
            _order.Enqueue(provisional);
        }
    }

    internal static void Clear()
    {
        _binds.Clear();
        _order.Clear();
        PendingUid.Reset();
    }
}