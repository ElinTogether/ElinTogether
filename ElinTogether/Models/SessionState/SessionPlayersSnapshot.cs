using System.Collections.Immutable;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     Net packet: Host -> Client
/// </summary>
[MessagePackObject]
public class SessionPlayersSnapshot
{
    [Key(0)]
    public required ImmutableArray<NetPeerState> Current { get; init; }

    public static SessionPlayersSnapshot Create()
    {
        return new() {
            Current = [..NetSession.Instance.CurrentPlayers],
        };
    }

    public void Apply()
    {
        var session = NetSession.Instance;
        session.CurrentPlayers.Clear();
        session.CurrentPlayers.AddRange(Current);

        // resolve self state
        session.Self =
            session.CurrentPlayers.Find(n => session.Player is { } player && n.CharaUid == player.uid) ??
            session.CurrentPlayers.Find(n => n.User.IsMe);
    }
}