using System.Collections.Generic;
using ElinTogether.Helper;

namespace ElinTogether.Net;

internal partial class ElinNetHost
{
    /// <summary>
    ///     Team ranks (<see cref="TeamRanks" />): team id -> its ranks and who holds which, as the text of
    ///     <see cref="TeamRanks.Pack" />. A team without a row never edited its ranks and has the default ones (an old
    ///     save has none: its teams play as before)
    /// </summary>
    [ElinGameIOProperty("team_ranks")]
    internal static Dictionary<int, string> TeamRankBook
    {
        get => field ??= [];
        set;
    }
}
