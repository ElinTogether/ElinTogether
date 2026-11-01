using System.Collections.Generic;
using System.Linq;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     Host -> every game: the Steam ids muted in this world (admin tools, council 2026-10-27). Each game that passes
///     chat on (the host, a player holding a map for others) drops theirs, and a muted player's own game says so
/// </summary>
[MessagePackObject]
public class ModerationDelta : ElinDelta
{
    /// <summary>
    ///     What this game knows to be muted, wherever it learnt it
    /// </summary>
    internal static HashSet<ulong> Muted { get; } = [];

    /// <summary>
    ///     The admins of this world, as this game knows them (the host keeps the list)
    /// </summary>
    internal static HashSet<ulong> AdminIds { get; } = [];

    /// <summary>
    ///     The moderators and the frozen or jailed players of this world (admin extras, council 2026-10-28)
    /// </summary>
    internal static HashSet<ulong> ModeratorIds { get; } = [];

    internal static HashSet<ulong> FrozenIds { get; } = [];

    [Key(0)]
    public required ulong[] Mutes { get; init; }

    [Key(1)]
    public ulong[] Admins { get; set; } = [];

    [Key(2)]
    public ulong[] Moderators { get; set; } = [];

    [Key(3)]
    public ulong[] Frozen { get; set; } = [];

    protected override void OnApply(ElinNetBase net)
    {
        // only the host of the world says who is muted
        if (net is ElinNetHost) {
            return;
        }

        Muted.Clear();
        Muted.UnionWith(Mutes);
        AdminIds.Clear();
        // (a peer-hosted world: an admin is one of the signed list, whatever its keeper says)
        AdminIds.UnionWith(Helper.PeerHosting.Active ? Admins.Where(Helper.PeerHosting.AdminSet().Contains) : Admins);
        ModeratorIds.Clear();
        ModeratorIds.UnionWith(Moderators);
        FrozenIds.Clear();
        FrozenIds.UnionWith(Frozen);
    }

    internal static bool IsMuted(ulong user)
    {
        return Muted.Contains(user);
    }
}
