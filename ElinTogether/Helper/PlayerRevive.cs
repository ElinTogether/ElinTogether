using System.Linq;
using ElinTogether.Elements;
using ElinTogether.Net;

namespace ElinTogether.Helper;

/// <summary>
///     Permadeath worlds: a dead player waits for a revive at a bartender instead of the game ending (council
///     2026-10-27, dev/COUNCIL_2026-10-27_permadeath_revive.md, GitHub issue 9). The host's box PermadeathRevive
/// </summary>
internal static class PlayerRevive
{
    /// <summary>
    ///     The death of this player was told once (last words): not again at a reload
    /// </summary>
    internal const string DeadFlag = "emp_permadead";

    /// <summary>
    ///     A player character of this world, played now or not: the name of its player
    /// </summary>
    internal const string PlayerKey = "emp_player";

    /// <summary>
    ///     A revive of a player is under way (no death penalty, no second broadcast)
    /// </summary>
    internal static bool Reviving { get; set; }

    internal static bool Active
    {
        get
        {
            if (EClass.game?.principal is not { permadeath: true }) {
                return false;
            }

            var session = NetSession.Instance;
            return session.Connection is ElinNetClient
                ? session.Rules.PermadeathRevive
                : EmpConfig.Server.PermadeathRevive.Value && (session.Connection is not null || ElinNetHost.IsSharedWorld);
        }
    }

    internal static bool IsPlayerChara(Chara c)
    {
        return c.IsPC || !c.GetStr(PlayerKey).IsEmpty() || c.GetBool("remote_chara") ||
               NetSession.Instance.CurrentPlayers.Any(p => p.CharaUid == c.uid);
    }

    /// <summary>
    ///     The host's game brings a dead player back (the price is paid by the caller): next to the reviver when both
    ///     are on this map, else where it fell, or at its next arrival when nobody plays it now. No death penalty: the
    ///     price was the cost
    /// </summary>
    internal static void ReviveOnHost(Chara dead, Chara reviver)
    {
        Point? point = null;
        if (reviver.IsInActiveMap && (dead.IsPC || dead.IsRemotePlayer)) {
            point = reviver.pos.GetNearestPoint(allowChara: false) ?? reviver.pos;
        } else if (dead.IsPC || (dead.IsRemotePlayer && dead.currentZone == EClass._zone)) {
            // GitHub issue 70: the reviver is on another map (travelling alone). Chara.Revive puts the character back
            // on the map only with a point: without one the host stood up nowhere, "You are dead" gone, nobody on
            // screen until a restart. Where it fell
            point = dead.pos is { IsValid: true, IsInBounds: true } fell
                ? fell.GetNearestPoint(allowChara: false) ?? fell.Copy()
                : EClass._map.GetCenterPos().GetNearestPoint(allowChara: false) ?? EClass._map.GetCenterPos();
        }

        EmpLog.Information("Player character {Uid} revived at the bartender by {Reviver}", dead.uid, reviver.uid);
        Reviving = true;
        try {
            dead.Revive(point, msg: true);
        } finally {
            Reviving = false;
        }

        dead.SetBool(DeadFlag, false);
        if (dead.IsPC) {
            EClass.player.deathDialog = false;
        } else if (dead.IsRemotePlayer) {
            dead.SetAI(GoalRemote.Default);
        }
    }
}
