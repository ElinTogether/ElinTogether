using System.Collections.Generic;
using System.Linq;
using ElinTogether.Net;

namespace ElinTogether.Helper;

/// <summary>
///     Whose story, and whose book of recipes and codex, a player plays with (the user, 2026-10-28: "each player does the
///     story at its own pace, and does it as a team"; "the codex, the recipes, etc." too). The OWNER is the player's
///     team when it is in a real team (teammates share), otherwise the player alone: minus its chara uid, never the
///     Wanderers as a group (players without a team share nothing). Used by <see cref="TeamOwnStory" /> and
///     <see cref="TeamKnowledge" /> alike, with the same rule when a player moves: joining a team takes the team's,
///     or gives the team the joiner's when it has none yet (its first member); leaving for no team keeps a copy of
///     the team's as the player's own
/// </summary>
internal static class StoryOwner
{
    /// <summary>
    ///     The ledgers' entry for an owner they never heard of (a player new to the world): what the world held when
    ///     the box was ticked
    /// </summary>
    internal const int Seed = int.MinValue;

    // (the Teams box is not asked: ticking or unticking it must not re-assign every owner without a move, a rollback to
    // the seed. The team list stays what it is, the box only decides whether it changes)
    /// <summary>
    ///     The owner that player plays with
    /// </summary>
    internal static int Of(int charaUid)
    {
        return Of(Teams.TeamOf(charaUid), charaUid);
    }

    /// <summary>
    ///     The owner of a player in that team (the Wanderers: the player alone)
    /// </summary>
    internal static int Of(int team, int charaUid)
    {
        return team != Teams.Wanderers ? team : -charaUid;
    }

    /// <summary>
    ///     Every owner of this world the host knows: its teams, and each player without one
    /// </summary>
    internal static IEnumerable<int> All()
    {
        return ElinNetHost.TeamList.Keys
            .Concat(ElinNetHost.PlayerCharaUids().Select(uid => Of(uid)))
            .Distinct();
    }

    /// <summary>
    ///     A player moved to owner <paramref name="to" />: true when that owner takes what the player had (it left for
    ///     no team, or joined a team that has nothing of its own yet), false when the player takes the team's
    /// </summary>
    internal static bool TakesMovers(int to, bool toHasOwn)
    {
        return to < 0 || !toHasOwn;
    }

    /// <summary>
    ///     A team that is no more (its members kept their own copies when they left)
    /// </summary>
    internal static bool IsGoneTeam(int owner)
    {
        return owner > 0 && !ElinNetHost.TeamList.ContainsKey(owner);
    }

    private static float _officialAt = float.MinValue;
    private static bool _official;

    /// <summary>
    ///     This game hosts the official public server (the user, 2026-10-28: there the story and the knowledge are
    ///     separated by player and team, whatever the boxes say). Read once a second: it looks at a file, and the
    ///     boxes are read for every message the host sends
    /// </summary>
    internal static bool OfficialHosting
    {
        get {
            var now = UnityEngine.Time.unscaledTime;
            if (now - _officialAt > 1f || now < _officialAt) {
                _officialAt = now;
                // (a peer-hosted world plays its signed rules only, even kept by the PC with the key: PeerRules)
                _official = Net.Steam.OfficialServer.Hosting && !PeerHosting.Active;
            }

            return _official;
        }
    }

    internal static string Name(int owner)
    {
        return owner == Seed ? "seed" : owner > 0 ? $"team {owner}" : owner == 0 ? "wanderers (old ledger)" : $"player {-owner}";
    }
}
