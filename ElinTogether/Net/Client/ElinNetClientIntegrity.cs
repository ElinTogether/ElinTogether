using ElinTogether.Models;
using ElinTogether.Net.Steam;

namespace ElinTogether.Net;

internal partial class ElinNetClient
{
    private NetHandshakePhase _handshakePhase = NetHandshakePhase.AwaitingVersion;

    private void BeginHandshake()
    {
        _handshakePhase = NetHandshakePhase.AwaitingVersion;

        EmpLog.Debug("Handshake started with host, awaiting version report");
    }

    private void AdvanceHandshake(NetHandshakePhase phase)
    {
        if (_handshakePhase == phase) {
            return;
        }

        EmpLog.Debug("Handshake advanced to stage {HandshakeStage}",
            phase);

        _handshakePhase = phase;
    }

    // copied from Host version
    private bool ShouldReceiveHostPacket(object packet, ISteamNetPeer peer)
    {
        // the host link of an away client, a zone session receives everything
        if (Session.IsAway && !IsZoneSession && !ShouldReceiveWhileAway(packet)) {
            return false;
        }

        // off the host map already, we are no longer in its list nor its snapshots
        if (IsAwaitingDeparture && packet is SessionPlayersSnapshot or WorldStateSnapshot) {
            return false;
        }

        if (_handshakePhase == NetHandshakePhase.Joined) {
            return true;
        }

        if (packet is NetIntegrityRejected) {
            return true;
        }

        if (!ReferenceEquals(peer, Host)) {
            EmpLog.Warning("Dropping {MessageType} from unexpected {@Peer}",
                packet.GetType().Name, peer);
            return false;
        }

        var allowed = _handshakePhase switch {
            NetHandshakePhase.AwaitingVersion => packet is NetIntegrityRequest,
            NetHandshakePhase.AwaitingIntegrity => packet is SourceValidationRequest or
                SourceValidationFailed or
                SteamLobbyRequest or
                SessionNewPlayerRequest or
                SessionCharaSelectRequest or
                NetSessionRules or
                SaveDataProbe || // ok
                // the player keeping the map we stay on, in place of its world (GitHub issue 46)
                (IsZoneSession && packet is ZoneSoftRejoin),
            _ => false,
        };

        if (!allowed) {
            EmpLog.Warning("Dropping {MessageType} from host at handshake stage {HandshakeStage}",
                packet.GetType().Name, _handshakePhase);
        }

        return allowed;
    }
}