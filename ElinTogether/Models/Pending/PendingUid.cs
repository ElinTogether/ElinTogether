using ElinTogether.Net;

namespace ElinTogether.Models;

/// <summary>
///     The uid of a card a client made before the host numbered it: the top 1/32 of the int range,
///     [<see cref="Floor" />, int.MaxValue], 6 bits of peer index and 20 bits of counter. GitHub issue 71: it was any
///     uid with bit 30 set, and the lease ranges (1.05M uids a trip alone) had pushed a long-played world's own counter
///     past 2^30 (public servers at 1.42 and 1.85 billion: the floor is above both): every card made since then looked pending to the guests, who
///     refused to identify, enchant, drop... them. The world's counter stays below the floor, see
///     <see cref="IsNearFloor" />
/// </summary>
internal static class PendingUid
{
    internal const int Floor = 0x7C000000;
    private const int PeerShift = 20;
    private const int PeerIndex = 0x3F; // 111111
    private const int Uid = 0xFFFFF; // 20 bits

    // the world's counter this close to the floor is told loudly: the uids left would soon look pending
    private const int FloorMargin = 50_000_000;

    private static int _nextId;
    private static int _peer = -1;

    internal static bool IsPending(int uid)
    {
        return uid >= Floor;
    }

    internal static int GetPeerIndex(int uid)
    {
        return (uid >> PeerShift) & PeerIndex;
    }

    /// <summary>
    ///     A real uid this high leaves less than the margin before the pending range
    /// </summary>
    internal static bool IsNearFloor(int uid)
    {
        return uid >= Floor - FloorMargin;
    }

    internal static int GetNext()
    {
        if (NetSession.Instance.Self is { } self) {
            _peer = self.Index & PeerIndex;
        } else if (_peer < 0) {
            _peer = PeerIndex;
        }

        return Floor | (_peer << PeerShift) | (++_nextId & Uid);
    }

    internal static void Reset()
    {
        _nextId = 0;
        _peer = -1;
    }
}
