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
            NetHandshakePhase.AwaitingIntegrity => packet is SourceValidationRequest or SourceValidationFailed or
                SteamLobbyRequest or SessionNewPlayerRequest or SaveDataProbe, // ok
            _ => false,
        };

        if (!allowed) {
            EmpLog.Warning("Dropping {MessageType} from host at handshake stage {HandshakeStage}",
                packet.GetType().Name, _handshakePhase);
        }

        return allowed;
    }
}