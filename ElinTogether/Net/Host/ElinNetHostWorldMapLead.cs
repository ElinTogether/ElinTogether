using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Patches;

namespace ElinTogether.Net;

/// <summary>
///     Council 2026-10-23, Personal Pace (<see cref="WorldMapTime.PersonalPace" />): the world map hours of a
///     player who travels while the others are elsewhere are held back as that player's lead instead of moving
///     the date. Its body (the turns of the step) and its own quests pay them, the world waits. Up to the host's
///     cap (EmpConfig.Server.WorldMapLeadCap): past it the hours move the date as with the Shared Clock. <br />
///     The world catches up once, by the largest lead, when everyone is on the world map together or a night is
///     slept together; the others lose nothing (WorldDateAdvanceEvent.CatchUp). The host keeps every player's
///     lead in its save, its own as anyone's, by character: 0 when there is none, an old save has none
/// </summary>
internal partial class ElinNetHost
{
    /// <summary>
    ///     Player chara uid -> minutes of world map travel the world has not caught up with yet
    /// </summary>
    [ElinGameIOProperty("world_map_leads")]
    private static Dictionary<int, int> WorldMapLeads
    {
        get => field ??= [];
        set;
    }

    // when the players were last told their lead (one who reconnects learns it again)
    private float _nextLeadTell;

    internal static int LeadOf(int charaUid)
    {
        return WorldMapLeads.GetValueOrDefault(charaUid);
    }

    /// <summary>
    ///     That player travels this many world map minutes: the part kept as its lead, 0 when the date moves for
    ///     all of them (another mode, everyone on the world map, the cap reached)
    /// </summary>
    internal int HoldLead(int charaUid, int minutes)
    {
        if (charaUid == 0 || minutes <= 0 || NetSession.Instance.Rules.WorldMapTime != WorldMapTime.PersonalPace ||
            EmpConfig.Server.WorldMapLeadCap.Value <= 0 || AllOnWorldMap(false)) {
            return 0;
        }

        // council 2026-11-17 (GitHub issues 48, 49: days flew for the others): one player's travel never moves the
        // others' date, all of it is that player's lead; the cap is now the most the world catches up at once
        WorldMapLeads[charaUid] = LeadOf(charaUid) + minutes;
        return minutes;
    }

    /// <summary>
    ///     The same for a player of the session, by its connection
    /// </summary>
    internal (int Held, int Lead) HoldLeadOfPeer(int peerId, int minutes)
    {
        var uid = PlayerUidOf(peerId);
        var held = HoldLead(uid, minutes);
        return (held, LeadOf(uid));
    }

    /// <summary>
    ///     Everyone is on the world map together, or sleeps the same night: the world catches up with the
    ///     largest lead of the players here, once, and every lead is paid. A player not connected keeps its own
    /// </summary>
    internal void ReleaseLeads()
    {
        if (WorldMapLeads.Count == 0 || pc is null) {
            return;
        }

        // a player still loading the host's world map (registered, not settled yet) would get the catch-up while its
        // world copy is being replaced: it would never read who travelled (found 2026-10-24, host travels, guest joins)
        if (Socket.Peers.Any(p => ActiveRemoteCharas.ContainsKey(p.Id) && !_departed.Contains(p.Id) && !_settled.Contains(p.Id))) {
            return;
        }

        var players = new List<(int Uid, int Peer)> { (pc.uid, WorldTravelClock.Host) };
        foreach (var peer in Socket.Peers) {
            if (PlayerUidOf(peer.Id) is not 0 and var uid) {
                players.Add((uid, peer.Id));
            }
        }

        var (top, topPeer) = players.OrderByDescending(p => LeadOf(p.Uid)).First();
        // (the most the world catches up at once, about one sleep; the rest the traveller paid alone, its own quests)
        var max = Math.Min(LeadOf(top), Math.Max(0, EmpConfig.Server.WorldMapLeadCap.Value) * Date.HourToken);
        if (max <= 0) {
            return;
        }

        foreach (var (uid, peer) in players) {
            if (!WorldMapLeads.Remove(uid)) {
                continue;
            }

            if (peer == WorldTravelClock.Host) {
                WorldMapLead.Set(0);
            } else {
                SendDeltaTo(peer, new PlayerLeadDelta { Lead = 0 });
            }
        }

        var traveller = topPeer == WorldTravelClock.Host
            ? (NetSession.Instance.Self?.User.Name ?? pc.NameSimple, pc.uid)
            : TravellerOf(topPeer);
        EmpLog.Debug("World time: the world catches up with the lead of {Traveller}, {Minutes} minute(s)",
            traveller.Item1, max);
        WorldDateAdvanceEvent.CatchUpNextFrame(WorldDateAdvanceEvent.AfterPending(max), traveller, top);
    }

    /// <summary>
    ///     Twice a second (see UpdateWorldMapTogether): leads are paid when everyone is on the world map, and every
    ///     5 s each player ahead is told its lead (it shows it, it does not count it)
    /// </summary>
    private void UpdateLeads(bool allOnWorldMap, float now)
    {
        if (allOnWorldMap) {
            ReleaseLeads();
        }

        if (now < _nextLeadTell) {
            return;
        }

        _nextLeadTell = now + 5f;
        WorldMapLead.Set(LeadOf(pc.uid));
        foreach (var peer in Socket.Peers) {
            if (LeadOf(PlayerUidOf(peer.Id)) is > 0 and var lead) {
                SendDeltaTo(peer.Id, new PlayerLeadDelta { Lead = lead });
            }
        }
    }
}
