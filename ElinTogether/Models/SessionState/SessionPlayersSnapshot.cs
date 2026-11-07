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

    /// <summary>
    ///     The map of the sender as it holds it, for the players standing on it, see <see cref="NetDesync" />
    /// </summary>
    [Key(1)]
    public MapSums? Sums { get; init; }

    /// <summary>
    ///     Only the players whose line changed since the last list this game got (by <see cref="NetPeerState.Index" />),
    ///     the others kept as they were; a whole list comes now and then (ElinNetHost.SendPlayersOnChange: the whole
    ///     list to everyone every 2 s grew with the square of the players, 15% of the host's upload at 50)
    /// </summary>
    [Key(2)]
    public bool Partial { get; init; }

    /// <summary>
    ///     With <see cref="Partial" />: the players gone since the last list
    /// </summary>
    [Key(3)]
    public int[]? Gone { get; init; }

    public static SessionPlayersSnapshot Create()
    {
        return new() {
            Current = [..NetSession.Instance.CurrentPlayers],
            // nobody to compare with while alone
            Sums = NetSession.Instance.CurrentPlayers.Count > 1 ? NetDesync.Collect() : null,
        };
    }

    public void Apply()
    {
        var session = NetSession.Instance;
        if (Partial) {
            if (Gone is { Length: > 0 } gone) {
                session.CurrentPlayers.RemoveAll(p => System.Array.IndexOf(gone, p.Index) >= 0);
            }

            foreach (var state in Current) {
                var at = session.CurrentPlayers.FindIndex(p => p.Index == state.Index);
                if (at >= 0) {
                    session.CurrentPlayers[at] = state;
                } else {
                    var before = session.CurrentPlayers.FindIndex(p => p.Index > state.Index);
                    session.CurrentPlayers.Insert(before < 0 ? session.CurrentPlayers.Count : before, state);
                }
            }
        } else {
            session.CurrentPlayers.Clear();
            session.CurrentPlayers.AddRange(Current);
        }

        // resolve self state
        session.Self =
            session.CurrentPlayers.Find(n => session.Player is { } player && n.CharaUid == player.uid) ??
            session.CurrentPlayers.Find(n => n.User.IsMe);

        NetDesync.Compare(Sums);
    }
}