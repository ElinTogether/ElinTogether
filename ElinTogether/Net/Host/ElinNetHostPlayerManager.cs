using System.Collections.Generic;
using System.Linq;
using ElinTogether.Common;
using ElinTogether.Helper;
using ElinTogether.Helper.Extensions;
using ElinTogether.Models;
using ElinTogether.Net.Steam;
using UnityEngine;

namespace ElinTogether.Net;

internal partial class ElinNetHost
{
    public readonly Dictionary<int, Chara> ActiveRemoteCharas = [];

    /// <summary>
    ///     A combination of all remote chara's act states
    /// </summary>
    public int SharedActState => States.Values.Sum(s => s.LastAct);

    /// <summary>
    ///     Shared speed of all players that reported one, 0 before anyone did
    /// </summary>
    public int SharedSpeed
    {
        get {
            var reported = States.Values.Where(s => s.Speed > 0).ToArray();
            return reported.Length == 0 ? 0 : (int)reported.Average(s => s.Speed);
        }
    }

    [ElinGameIOProperty("remote_chara")]
    internal static Dictionary<ulong, int> SavedRemoteCharas
    {
        get => field ??= [];
        set;
    }

    [ElinPreSceneInit]
    private static void ResetSavedRemoteCharas(Scene.Mode mode)
    {
        if (mode == Scene.Mode.Title) {
            SavedRemoteCharas = [];
        }
    }

    public static void RemoveRemoteChara(Chara remoteChara, bool broadcast = true)
    {
        if (!core.IsGameStarted) {
            return;
        }

        remoteChara.SetNoGoal();

        pc.party.RemoveMember(remoteChara);
        _zone.RemoveCard(remoteChara);

        if (broadcast && NetSession.Instance.Connection is ElinNetHost host) {
            host.Delta.AddRemote(new CharaRemoveFromGameDelta {
                Owner = remoteChara,
            });
        }
    }

    public static void DeleteRemoteChara(ulong steamId)
    {
        if (!SavedRemoteCharas.Remove(steamId, out var charaUid)) {
            return;
        }

        if (game.cards.globalCharas.Find(charaUid) is not { IsSavedRemoteChara: true } chara ||
            SavedRemoteCharas.ContainsValue(charaUid)) {
            EmpLog.Warning("Removed stale saved remote chara {Uid} of {RemoteIdentity}",
                charaUid, steamId.ToString());
            return;
        }

        if (Session.Connection is ElinNetHost host) {
            var active = host.ActiveRemoteCharas.FirstOrDefault(kv => kv.Value == chara);
            if (active.Value is not null) {
                host.DisconnectPeer(active.Key, EmpDisconnectInfo.HostCharaDeleted);
            } else {
                host.Delta.AddRemote(new CharaRemoveFromGameDelta {
                    Owner = chara,
                });
            }
        }

        CardCache.Remove(chara.uid);
        foreach (var thing in chara.things.Flatten()) {
            CardCache.Remove(thing.uid);
        }

        chara.held?.Destroy();
        chara.RemoveGlobal();
        chara.DestroyImmediate();

        EmpLog.Information("Deleted remote chara {Uid} of {RemoteIdentity}",
            charaUid, steamId.ToString());
    }

    /// <summary>
    ///     PeerConnect -> Prepare -> MoveZone -> SaveProbe
    /// </summary>
    public void PreparePlayerJoin(ISteamNetPeer peer)
    {
        EmpLog.Information("Preparing player {@Peer} for joining",
            peer);

        if (!SavedRemoteCharas.TryGetValue(peer.User, out var charaUid) ||
            game.cards.globalCharas.Find(charaUid) is not { } chara) {
            EmpLog.Debug("Remote character does not exist, request for new character generation");
            peer.Send(new SessionNewPlayerRequest());
        } else if (!chara.IsSavedRemoteChara || ActiveRemoteCharas.Any(kv => kv.Key != peer.Id && kv.Value == chara)) {
            EmpLog.Warning("Discarding saved remote chara {Uid} of {@Peer}, it is not a free remote player chara",
                charaUid, peer);
            SavedRemoteCharas.Remove(peer.User);
            peer.Send(new SessionNewPlayerRequest());
        } else {
            // remote character exists
            SendSaveProbe(chara, peer);
        }
    }

    /// <summary>
    ///     Send a save snapshot for replication
    /// </summary>
    public void SendSaveProbe(Chara chara, ISteamNetPeer peer)
    {
        EmpLog.Information("Sending save probe to player {@Peer} for replication",
            peer);

        // register before any SetAI
        ActiveRemoteCharas[peer.Id] = chara;

        chara.MakeAlly();
        chara.MoveZone(pc.currentZone);
        chara.SetBool("remote_chara", true);
        GiveAxeToRemotePlayer(chara);

        var state = States[peer.Id] = new() {
            Index = peer.Id,
            User = peer.User,
            CharaUid = chara.uid,
        };

        CardCache.Add(chara);
        CardCache.CacheContainer(chara.things);

        Session.CurrentPlayers.Add(state);

        peer.Send(NetSession.Instance.Rules);
        peer.Send(SaveDataProbe.Create(chara.uid));
    }

    /// <summary>
    ///     Net event: Client finished character generation and is ready for save probe
    /// </summary>
    private void OnSessionNewPlayerResponse(SessionNewPlayerResponse response, ISteamNetPeer peer)
    {
        if (States.ContainsKey(peer.Id)) {
            EmpLog.Warning("Dropping unrequested remote chara creation from player {@Peer}",
                peer);
            return;
        }

        EmpLog.Information("Received remote chara creation from player {@Peer}",
            peer);

        var chara = response.Chara.Decompress<Chara>();
        chara.SetBool(CINT.IsPC, false);
        chara.SetBool("emp_creating", true);
        game.cards.AssignUID(chara);

        var host = pc;
        try {
            player.chara = chara;

            chara.hp = chara.MaxHP;
            chara.SetFaith(game.religions.list[0]);
            chara.elements.SetBase(SKILL.strategy, 1);
            chara.elements.SetBase(ABILITY.AI_Meditate, 1);
            chara.elements.SetBase(ABILITY.ActQuickCraft, 1);
            chara.elements.SetBase(ABILITY.AI_SelfHarm, 1);
            chara.elements.SetBase(ABILITY.ActPray, 1);
            foreach (var e in chara.elements.dict.Values.ToList()) {
                if (e.Value == 0) {
                    chara.elements.Remove(e.id);
                    continue;
                }
                if (e.HasTag("primary")) {
                    e.vTempPotential = Mathf.Max(30, (e.ValueWithoutLink - 8) * 7);
                }
            }

            foreach (var slot in chara.body.slots) {
                chara.body.Unequip(slot);
            }
            chara.things.DestroyAll();
            chara.elements.CheckSkillActions();
            chara.hunger.value = 30;
            chara.CalculateMaxStamina();
            chara.stamina.Set(chara.stamina.max / 2);
            chara.Refresh();

            player.CreateEquip();
            chara.AddThing("purse");
        } finally {
            chara.SetBool("emp_creating", false);
            player.chara = host;
        }

        foreach (var stale in SavedRemoteCharas.Where(kv => kv.Value == chara.uid).ToArray()) {
            SavedRemoteCharas.Remove(stale.Key);
        }
        SavedRemoteCharas[peer.User] = chara.uid;

        SendSaveProbe(chara, peer);
    }

    /// <summary>
    ///     Request a specific client to reconnect for full synchronization
    /// </summary>
    public void RequestClientReconnect(int peerIndex)
    {
        if (!States.ContainsKey(peerIndex)) {
            EmpLog.Warning("Cannot request reconnect: peer {PeerIndex} not found",
                peerIndex);
            return;
        }

        foreach (var peer in Socket.Peers) {
            if (peer.Id == peerIndex) {
                EmpLog.Information("Requesting reconnect for peer {@Peer}",
                    peer);
                peer.Send(SessionReconnectRequest.Current);
                return;
            }
        }

        EmpLog.Warning("Cannot request reconnect: peer {PeerIndex} not connected",
            peerIndex);
    }

    private static void GiveAxeToRemotePlayer(Chara chara)
    {
        if (!chara.GetBool("remote_axe_given")) {
            try {
                chara.SetBool("emp_creating", true);
                chara.AddThing("axe");
            } finally {
                chara.SetBool("emp_creating", false);
            }
            chara.SetBool("remote_axe_given", true);
        }
    }

    [ElinPostLoad]
    private static void RemoveLeftOverCharas(GameIOContext? context)
    {
        IEnumerable<Chara> excluded = Session.Connection is ElinNetHost host
            ? host.ActiveRemoteCharas.Values
            : [];

        var currentRemoteCharas = game.cards.globalCharas.Values
            .Where(c => c.IsSavedRemoteChara);

        foreach (var chara in currentRemoteCharas.Except(excluded)) {
            RemoveRemoteChara(chara);
        }

        pc.party?.members.RemoveAll(c => c is null);
        pc.party?.uidMembers.RemoveAll(uid => pc.party?.members.Find(c => c.uid == uid) is null);
    }
}