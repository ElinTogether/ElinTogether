using System.Collections.Generic;
using System.Linq;
using ElinTogether.Common;
using ElinTogether.LangMod;
using ElinTogether.Models;
using ElinTogether.Net.Steam;
using UnityEngine;

namespace ElinTogether.Net;

internal partial class ElinNetHost
{
    private readonly Dictionary<int, HandshakeState> _handshakes = [];

    private void BeginHandshake(ISteamNetPeer peer)
    {
        _handshakes[peer.Id] = new() {
            Phase = NetHandshakePhase.AwaitingVersion,
            Timeout = Time.time + 5f,
        };

        EmpLog.Debug("Handshake started for {@Peer}, awaiting version report",
            peer);

        peer.Send(NetIntegrityRequest.Create());
    }

    private bool ShouldReceivePeerPacket(object packet, ISteamNetPeer peer)
    {
        if (!_handshakes.TryGetValue(peer.Id, out var state)) {
            EmpLog.Warning("Dropping {MessageType} from unregistered {@Peer}",
                packet.GetType().Name, peer);
            return false;
        }

        var allowed = state.Phase switch {
            NetHandshakePhase.AwaitingVersion => packet is NetIntegrityResponse,
            NetHandshakePhase.AwaitingIntegrity => packet is SourceValidationResponse or SourceValidationContinue,
            NetHandshakePhase.Joined => true,
            _ => false,
        };

        if (!allowed) {
            EmpLog.Warning("Dropping {MessageType} from {@Peer} at handshake stage {HandshakeStage}",
                packet.GetType().Name, peer, state.Phase);
        }

        return allowed;
    }

    private void RemoveStaleIntegrityCheck()
    {
        if (_handshakes.Count == 0) {
            return;
        }

        var now = Time.time;

        foreach (var peer in Socket.Peers) {
            if (!_handshakes.TryGetValue(peer.Id, out var state) ||
                state.Phase == NetHandshakePhase.Joined ||
                now < state.Timeout) {
                continue;
            }

            if (state.Phase == NetHandshakePhase.Rejected) {
                // gtfo
                EmpLog.Debug("Closing rejected {@Peer} that did not disconnect itself",
                    peer);
            } else {
                EmpLog.Warning("Handshake timed out for {@Peer} at stage {HandshakeStage}",
                    peer, state.Phase);
            }

            Socket.Disconnect(peer, state.DisconnectReason);
        }
    }

    private void RejectHandshake(
        ISteamNetPeer peer,
        NetIntegrityRejected.NetIntegrityRejectReason reason,
        IEnumerable<string>? details = null)
    {
        var trimmed = details?.Take(32).ToList() ?? [];

        EmpLog.Warning("Rejecting {@Peer}: {RejectReason}, {MismatchCount} mismatching entries",
            peer, reason, trimmed.Count);

        if (_handshakes.TryGetValue(peer.Id, out var state)) {
            state.Phase = NetHandshakePhase.Rejected;
            state.Timeout = Time.time + 5f;
            state.DisconnectReason = reason switch {
                NetIntegrityRejected.NetIntegrityRejectReason.ActMappingMismatch => EmpDisconnectInfo.ActMappingMismatch,
                NetIntegrityRejected.NetIntegrityRejectReason.IntegrityMismatch => EmpDisconnectInfo.InvalidSource,
                _ => EmpDisconnectInfo.VersionMismatch,
            };
        }

        peer.Send(NetIntegrityRejected.Create(reason, trimmed));
    }

    private void AcceptHandshake(ISteamNetPeer peer)
    {
        if (_handshakes.TryGetValue(peer.Id, out var state)) {
            state.Phase = NetHandshakePhase.Joined;
        }

        PreparePlayerJoin(peer);
    }

    private void OnNetHandshakeResponse(NetIntegrityResponse response, ISteamNetPeer peer)
    {
        if (!BuildVersionIntegrity.Ok(response.ClientModVersion, response.ClientGameVersion, response.APIVersion)) {
            EmpLog.Warning(
                "Version mismatch from {@Peer}: mod {ClientModVersion} -> {HostModVersion}, " +
                "game {ClientGameVersion} -> {HostGameVersion}, api {ClientProtocolVersion} -> {HostProtocolVersion}",
                peer,
                response.ClientModVersion, ModInfo.BuildVersion,
                response.ClientGameVersion, BuildVersionIntegrity.GameVersion,
                response.APIVersion, BuildVersionIntegrity.APIVersionLatest);

            EmpPop.Debug("emp_version_rejected_host".Loc(
                peer.User.Name,
                ModInfo.BuildVersion.TagColor(Color.green),
                BuildVersionIntegrity.GameVersion.TagColor(Color.green)));

            RejectHandshake(peer, NetIntegrityRejected.NetIntegrityRejectReason.VersionMismatch, [
                response.ClientModVersion,
                response.ClientGameVersion,
            ]);
            return;
        }

        EmpLog.Information("Version verified for {@Peer}: mod {HostModVersion}, game {HostGameVersion}",
            peer, ModInfo.BuildVersion, BuildVersionIntegrity.GameVersion);

        if (_handshakes.TryGetValue(peer.Id, out var state)) {
            state.Phase = NetHandshakePhase.AwaitingIntegrity;
            state.Timeout = Time.time + EmpConfig.Policy.Timeout.Value;
        }

        // and invite to steam lobby if clients aren't already in
        peer.Send(new SteamLobbyRequest {
            LobbyId = Session.Lobby.Current,
        });

        EmpLog.Debug("Requesting source validation from {@Peer} (flags={Flags})",
            peer, ValidFlags);

        // do source validations
        peer.Send(new SourceValidationRequest {
            SourceNames = GetValidationSourceNames(),
            FilePaths = GetValidationFilePaths(),
            ValidationFlags = (int)ValidFlags,
        });
    }

    private sealed class HandshakeState
    {
        public string DisconnectReason = EmpDisconnectInfo.Timeout;
        public NetHandshakePhase Phase;
        public float Timeout;
    }
}