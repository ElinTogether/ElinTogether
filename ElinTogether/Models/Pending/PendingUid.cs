using ElinTogether.Net;

namespace ElinTogether.Models;

internal static class PendingUid
{
    // 0 1 6 24
    internal const int Flag = 0x40000000; // 1
    private const int PeerIndex = 0x3F; // 111111
    private const int Uid = 0xFFFFFF; // 1111-1111-1111-1111-1111-1111

    private static int _nextId;
    private static int _peer = -1;

    internal static bool IsPending(int uid)
    {
        return (uid & Flag) != 0;
    }

    internal static int GetPeerIndex(int uid)
    {
        return (uid >> 24) & PeerIndex;
    }

    internal static int GetNext()
    {
        if (NetSession.Instance.Self is { } self) {
            _peer = self.Index & PeerIndex;
        } else if (_peer < 0) {
            _peer = PeerIndex;
        }

        return Flag | (_peer << 24) | (++_nextId & Uid);
    }

    internal static void Reset()
    {
        _nextId = 0;
        _peer = -1;
    }
}