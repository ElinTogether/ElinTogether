using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class SessionReconnectRequest
{
    public static SessionReconnectRequest Current => new() {
        LobbyId = (ulong)NetSession.Instance.Lobby.Current!.LobbyId,
    };

    [Key(0)]
    public required ulong LobbyId { get; init; }
}