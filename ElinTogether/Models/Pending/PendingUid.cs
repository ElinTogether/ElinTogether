using System;
using ElinTogether.Net;

namespace ElinTogether.Models;

/// <summary>
///     The uid of a card a client made before the host numbered it: a negative number, int.MinValue with 6 bits of
///     peer index and 20 bits of counter. The game's own numbers are all positive, the whole positive range is the
///     world's. GitHub issue 71: pending was bit 30 set, and long-played worlds' counters went past 2^30; then the top
///     1/32 [0x7C000000, int.MaxValue], and a world's counter got there too (issues 78-81, 2.147 billion): every card
///     made since then looked pending to the guests, who refused to buy, bank, identify, drop... them. The world's
///     counter stays below its ceiling, see <see cref="IsNearCeiling" /> and UidRepair
/// </summary>
internal static class PendingUid
{
    private const int PeerShift = 20;
    private const int PeerIndex = 0x3F; // 111111
    private const int Uid = 0xFFFFF; // 20 bits

    // the world's counter this close to its ceiling is told loudly: the numbers left would soon run out
    private const int CeilingMargin = 50_000_000;

    /// <summary>
    ///     The first number the world's counter must not reach (UidRepair puts it below the cards of a world whose
    ///     counter it moved down); int.MaxValue otherwise
    /// </summary>
    internal static int Ceiling => Helper.UidRepair.Ceiling;

    private static int _nextId;
    private static int _peer = -1;

    internal static bool IsPending(int uid)
    {
        return uid < 0;
    }

    /// <summary>
    ///     The world's next card number above this uid, unless the uid is a pending one: taken as a real number, a
    ///     client's pending uid (up to int.MaxValue) put the world's counter there for good, every card made since
    ///     looked pending to the guests (GitHub issues 78, 79, 81: a world at 2.147 billion, 250 000 numbers from the end)
    /// </summary>
    internal static int Above(int uidNext, int uid)
    {
        return IsPending(uid) ? uidNext : Math.Max(uidNext, uid + 1);
    }

    internal static int GetPeerIndex(int uid)
    {
        return (uid >> PeerShift) & PeerIndex;
    }

    /// <summary>
    ///     A real uid this high leaves less than the margin before the world's ceiling
    /// </summary>
    internal static bool IsNearCeiling(int uid)
    {
        return uid < 0 || uid >= Ceiling - CeilingMargin;
    }

    internal static int GetNext()
    {
        if (NetSession.Instance.Self is { } self) {
            _peer = self.Index & PeerIndex;
        } else if (_peer < 0) {
            _peer = PeerIndex;
        }

        return int.MinValue | (_peer << PeerShift) | (++_nextId & Uid);
    }

    internal static void Reset()
    {
        _nextId = 0;
        _peer = -1;
    }
}
