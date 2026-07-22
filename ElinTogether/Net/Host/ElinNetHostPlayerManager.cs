using System.Collections.Generic;
using System.Linq;
using ElinTogether.Models;
using ElinTogether.Net.Steam;

namespace ElinTogether.Net;

internal partial class ElinNetHost
{
    public readonly Dictionary<int, Chara> ActiveRemoteCharas = [];

    /// <summary>
    ///     A combination of all remote chara's act states
    /// </summary>
    public int SharedActState => States.Values.Sum(s => s.LastAct);

    /// <summary>
    ///     Shared speed of all players
    /// </summary>
    public int SharedSpeed => (int)States.Values.Average(s => s.Speed);

    [ElinGameIOProperty("remote_chara")]
    private static Dictionary<ulong, int> SavedRemoteCharas
    {
        get => field ??= [];
        set;
    }

    public static void RemoveRemoteChara(Chara remoteChara, bool broadcast = true)
    {
        if (!core.IsGameStarted) {
            return;
        }

        pc.party.RemoveMember(remoteChara);
        _zone.RemoveCard(remoteChara);

        if (broadcast && NetSession.Instance.Connection is ElinNetHost host) {
            host.Delta.AddRemote(new CharaRemoveFromGameDelta {
                Owner = remoteChara,
            });
        }
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

        chara.MakeAlly();
        chara.MoveZone(pc.currentZone);
        chara.SetBool("remote_chara", true);
        ActiveRemoteCharas[peer.Id] = chara;

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
        EmpLog.Information("Received remote chara creation from player {@Peer}",
            peer);

        var chara = response.Chara.Decompress<Chara>();
        chara.mapInt.Remove(CINT.IsPC);
        game.cards.AssignUID(chara);

        SavedRemoteCharas[peer.User] = chara.uid;

        SendSaveProbe(chara, peer);
    }

    /// <summary>
    ///     Request a specific client to reconnect for full synchronization
    /// </summary>
    public void RequestClientReconnect(int peerIndex)
    {
        if (!States.ContainsKey(peerIndex)) {
            EmpLog.Warning("Cannot request reconnect: peer {Index} not found",
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

        EmpLog.Warning("Cannot request reconnect: peer {Index} not connected",
            peerIndex);
    }

    [ElinPostLoad]
    private static void RemoveLeftOverCharas(GameIOContext context)
    {
        IEnumerable<Chara> excluded = Session.Connection is ElinNetHost host
            ? host.ActiveRemoteCharas.Values
            : [];

        var currentRemoteCharas = game.cards.globalCharas.Values
            .Where(c => c.GetBool("remote_chara"));

        foreach (var chara in currentRemoteCharas.Except(excluded)) {
            RemoveRemoteChara(chara);
        }

        pc.party?.members.RemoveAll(c => c is null);
        pc.party?.uidMembers.RemoveAll(uid => pc.party?.members.Find(c => c.uid == uid) is null);
    }
}