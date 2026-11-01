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
            Timeout = Time.time + EmpConfig.Policy.Timeout.Value,
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

        // away players keep sending snapshots until their own state catches up, not worth a warning
        if (state.Phase == NetHandshakePhase.Joined && IsAway(peer) && packet is CharaStateSnapshot) {
            return false;
        }

        var allowed = state.Phase switch {
            NetHandshakePhase.AwaitingVersion => packet is NetIntegrityResponse,
            NetHandshakePhase.AwaitingIntegrity => packet is SourceValidationResponse or SourceValidationContinue,
            // an away player simulates its own zone, nothing it does applies to the host map
            // (its delta lists only carry chat, see OnWorldStateDeltaResponse)
            NetHandshakePhase.Joined when IsAway(peer) => packet is not (CharaStateSnapshot or
                ZoneDataReceivedResponse or NetIntegrityResponse or SourceValidationResponse or SourceValidationContinue),
            // a handshake packet once joined would send the player back to an earlier stage (from upstream 6f0fda1)
            NetHandshakePhase.Joined => packet is not (NetIntegrityResponse or SourceValidationResponse or
                SourceValidationContinue),
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
        if (!AcceptOwnerSeat(response, peer)) {
            return;
        }

        // another version of Elin: let in and told, unless the host wants the same one for everyone
        var otherGame = !BuildVersionIntegrity.SameGame(response.ClientGameVersion);
        // a zone session decides nothing of its own: the host of the game let this player in with its rules, the
        // settings of the player keeping this map are not those of the game
        var sameGameOnly = EmpConfig.Server.SameGameVersion.Value && !IsZoneSession;
        if (otherGame && !sameGameOnly && !IsZoneSession &&
            BuildVersionIntegrity.Ok(response.ClientModVersion, response.ClientGameVersion, response.APIVersion)) {
            EmpLog.Warning("Player {@Peer} runs game {ClientGameVersion}, host {HostGameVersion}: allowed",
                peer, response.ClientGameVersion, BuildVersionIntegrity.GameVersion);
            EmpPop.Information("emp_game_version_differs".Loc(peer.User.Name,
                BuildVersionIntegrity.Display(response.ClientGameVersion),
                BuildVersionIntegrity.Display(BuildVersionIntegrity.GameVersion)));
        }

        if ((otherGame && sameGameOnly) ||
            !BuildVersionIntegrity.Ok(response.ClientModVersion, response.ClientGameVersion, response.APIVersion)) {
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
        // (a zone session has none, its guests stay in the lobby of the real host)
        if (!IsZoneSession) {
            peer.Send(new SteamLobbyRequest {
                LobbyId = Session.Lobby.Current,
            });
        } else {
            // no source check of its own either: with the check of the player keeping this map (its own boxes,
            // its own mods) a guest the host accepted was asked "continue?" while walking into a map, and was
            // out of it on a no. Its acts were matched against the host's, as ours were
            EmpLog.Information("Guest {@Peer} was let in by the host of the game, no source validation here",
                peer);
            AcceptHandshake(peer);
            return;
        }

        EmpLog.Debug("Requesting source validation from {@Peer} (flags={Flags})",
            peer, ValidFlags);

        // do source validations
        peer.Send(new SourceValidationRequest {
            SourceNames = GetValidationSourceNames(),
            FilePaths = GetValidationFilePaths(),
            ValidationFlags = (int)ValidFlags,
            // what differs is named to the guest, before the world is sent; the acts still decide who comes in
            Mods = EmpConfig.Server.PublishMods.Value ? Helper.ModList.Reference : null,
            // mods a player may have or not: the guest leaves them out of its report and of its comparison
            ClientOnlyMods = EmpConfig.Server.ClientOnlyMods.Value,
        });
    }

    private sealed class HandshakeState
    {
        public string DisconnectReason = EmpDisconnectInfo.Timeout;
        public NetHandshakePhase Phase;
        public float Timeout;

        // the player was told its differences and may go on with them (SourceValidationFailed)
        public bool MayContinue;
    }
}