using System;
using System.Collections.Generic;
using ElinTogether.Net;
using Steamworks;

namespace ElinTogether.Helper.Steam;

/// <summary>
///     The Elin Together games this player's Steam friends are in right now: a friend playing Elin in a Steam lobby
///     (Elin itself makes none, the mod's host does, see SteamNetLobbyManager.CreateLobby). Steam tells a friend's
///     lobby to its friends without asking anything (GetFriendGamePlayed), the lobby's own data comes later
/// </summary>
internal static class FriendGames
{
    internal readonly record struct Game(ulong Lobby, string Friend);

    /// <summary>One entry per game (several friends in one game: the first one found names it)</summary>
    internal static List<Game> Now()
    {
        var games = new List<Game>();
        try {
            var app = SteamUtils.GetAppID();
            var mine = ((CSteamID)NetSession.Instance.Lobby.Current).m_SteamID;
            var seen = new HashSet<ulong>();
            const EFriendFlags flags = EFriendFlags.k_EFriendFlagImmediate;
            var count = SteamFriends.GetFriendCount(flags);
            for (var i = 0; i < count; i++) {
                var friend = SteamFriends.GetFriendByIndex(i, flags);
                if (!SteamFriends.GetFriendGamePlayed(friend, out var played) || played.m_gameID.AppID() != app ||
                    !played.m_steamIDLobby.IsValid()) {
                    continue;
                }

                var lobby = played.m_steamIDLobby.m_SteamID;
                if (lobby == mine || !seen.Add(lobby)) {
                    continue;
                }

                games.Add(new(lobby, SteamFriends.GetFriendPersonaName(friend)));
            }
        } catch (Exception ex) {
            // (no Steam: no friends to list)
            EmpLog.Debug(ex, "The friends' games could not be listed");
        }

        return games;
    }
}
