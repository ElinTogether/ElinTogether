using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using ElinTogether.Common;
using ElinTogether.Helper.Steam;
using Steamworks;

namespace ElinTogether.Net.Steam;

public partial class SteamNetManager(ISteamNetSerializer? serializer = null) : IDisposable
{
    // read by batches of this size until the queue is empty or PollBudgetMs is spent
    // ponytail: local constant, EmpConstants.MaxBatchedMessages is no longer read here
    private const int BatchSize = 32;
    private const long PollBudgetMs = 4;

    // what a player that has not joined yet may send in one message
    private const int UntrustedMessageSize = 4 * 1024 * 1024;

    private readonly IntPtr[] _batchedMessages = new IntPtr[BatchSize];
    private readonly SteamNetPeerBroadcast _broadcast = new(serializer ?? new SteamNetSerializer());
    private readonly List<SteamNetPeer> _peers = [];
    private readonly ISteamNetSerializer _serializer = serializer ?? new SteamNetSerializer();

    internal ISteamNetSerializer Serializer => _serializer;

    private bool _disposed;
    private HSteamListenSocket _listenSocket;
    private ISteamNetListener? _listener;
    private HSteamNetPollGroup _pollGroup;

    public bool IsHost { get; private set; }
    public bool IsListening { get; private set; }

    /// <summary>
    ///     Fake placeholder peer
    /// </summary>

    private ISteamNetPeer FakePeer => field ??= new SteamNetPeerFake();

    /// <summary>
    ///     The first connected peer, mainly client->host
    /// </summary>
    public ISteamNetPeer FirstPeer => _peers.FirstOrDefault(p => p.IsConnected) ?? FakePeer;

    /// <summary>
    ///     The optimized broadcast peer, mainly host->multiple clients
    /// </summary>
    public ISteamNetPeer Broadcast => _broadcast;

    /// <summary>
    ///     All connected peers. A copy made once per change of the list, not at every call: it is asked several
    ///     times per peer per frame (efficiency audit 2026-10-27). Never changed in place, so a caller may go
    ///     through it while a peer comes or goes
    /// </summary>
    public IReadOnlyList<ISteamNetPeer> Peers => _peersCopy ??= [.._peers];

    private ISteamNetPeer[]? _peersCopy;

    /// <summary>
    ///     The peers without a copy, for NetMetrics
    /// </summary>
    internal List<SteamNetPeer> PeerList => _peers;

    /// <summary>
    ///     Any connected peers
    /// </summary>
    public bool IsConnected
    {
        get {
            // (asked every frame: a plain loop, no LINQ)
            for (var i = 0; i < _peers.Count; i++) {
                if (_peers[i].IsConnected) {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    ///     Initialize poll group and optionally steam lobby
    /// </summary>
    public void Initialize(ISteamNetListener listener)
    {
        if (_pollGroup != HSteamNetPollGroup.Invalid) {
            return;
        }

        _pollGroup = SteamNetworkingSockets.CreatePollGroup();
        _listener = listener;

        SteamCallback<SteamNetConnectionStatusChangedCallback_t>.Add(HandleStatusChange);
    }

    /// <summary>
    ///     Poll batched events on all peers
    /// </summary>
    public void Poll()
    {
        if (_pollGroup == HSteamNetPollGroup.Invalid || NetShutdown.IsQuitting) {
            return;
        }

        // drain the queue, bounded per frame: a fixed small batch never catches up after a freeze
        var clock = Stopwatch.StartNew();
        int received;
        do {
            received = SteamNetworkingSockets.ReceiveMessagesOnPollGroup(_pollGroup, _batchedMessages, _batchedMessages.Length);

            for (var i = 0; i < received; ++i) {
                var msg = SteamNetworkingMessage_t.FromIntPtr(_batchedMessages[i]);
                try {
                    var peer = _peers.Find(p => p.Connection == msg.m_conn);
                    if (peer is null) {
                        continue;
                    }

                    var bytes = new byte[msg.m_cbSize];
                    Marshal.Copy(msg.m_pData, bytes, 0, msg.m_cbSize);

                    peer.Stat.Received(msg.m_cbSize);

                    // a piece of a big message: nothing for the listener before the last one, see SteamNetPeer.Send
                    if (NetFragments.IsFragment(bytes)) {
                        NetFragments.PiecesReceived++;
                        // a player not in yet sends small things (versions, a character): a big message from it is
                        // refused before it is pieced together (security audit 2026-10-27: 64 MB were read first)
                        if (IsHost && !peer.Trusted && NetFragments.ReadInt(bytes, 16) > UntrustedMessageSize) {
                            EmpLog.Warning("A message of {Size} bytes from {@Peer}, who has not joined yet, was refused",
                                NetFragments.ReadInt(bytes, 16), peer);
                            continue;
                        }

                        var interrupted = peer.Fragments.InterruptedMessages;
                        var whole = peer.Fragments.Add(bytes);
                        if (peer.Fragments.InterruptedMessages != interrupted) {
                            EmpLog.Warning("A big message of {@Peer} was given up: another one started before its last piece",
                                peer);
                        }

                        if (whole is null) {
                            continue;
                        }

                        bytes = whole;
                    }

                    var typeHash = BitConverter.ToUInt32(bytes, 0);
                    var type = SteamNetTypeRegistry.Resolve(typeHash);
                    if (type == null) {
                        EmpLog.Warning("Failed to parse type hash {TypeHash}",
                            typeHash);
                        continue;
                    }

                    var start = Stopwatch.GetTimestamp();
                    var packet = _serializer.Deserialize(SteamNetSerializer.Payload(bytes), type);
                    _listener?.OnMessageReceived(packet, peer);
                    NetMetrics.Received(peer.Id, typeHash, bytes.Length, start);
                } catch (Exception ex) {
                    // one bad message must not leave the rest of the batch unread and unreleased
                    EmpLog.Warning(ex, "Message dropped");
                } finally {
                    SteamNetworkingMessage_t.Release(_batchedMessages[i]);
                }
            }
            // a handler may have shut the manager down
        } while (received == _batchedMessages.Length && clock.ElapsedMilliseconds < PollBudgetMs
                 && _pollGroup != HSteamNetPollGroup.Invalid && !NetShutdown.IsQuitting);

        // the next pieces of the big messages on their way out, a handler may have dropped a peer
        foreach (var peer in _peers.ToArray()) {
            peer.Flush();

            // a reliable message was lost for this peer (it says so in the log): its stream has a hole
            if (peer.BrokenReason is { } reason) {
                Disconnect(peer, reason);
            }
        }
    }

#region Connection Management

    /// <summary>
    ///     Ungracefully discard the listen socket
    /// </summary>
    public void Stop()
    {
        foreach (var peer in Peers) {
            Disconnect(peer, EmpDisconnectInfo.HostShutdown);
        }

        DiscardListenSocket();
    }

    internal void Shutdown()
    {
        if (_disposed) {
            return;
        }

        _disposed = true;

        SteamCallback<SteamNetConnectionStatusChangedCallback_t>.Remove(HandleStatusChange);

        foreach (var peer in _peers) {
            EmpLog.Verbose("Closing connection {@Peer}",
                peer);

            SteamNetworkingSockets.SetConnectionPollGroup(peer.Connection, HSteamNetPollGroup.Invalid);

            SteamNetworkingSockets.CloseConnection(peer.Connection, 0, EmpDisconnectInfo.HostShutdown, false);

            peer.Dispose();
        }

        _peers.Clear();
        _peersCopy = null;
        _broadcast.ClearTargets();
        _broadcast.Dispose();

        DiscardListenSocket();
        DestroyPollGroup();

        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Disconnect with reason
    /// </summary>
    public void Disconnect(ISteamNetPeer disconnectPeer, string reason)
    {
        if (disconnectPeer is not SteamNetPeer peer) {
            return;
        }

        if (!_peers.Contains(peer)) {
            return;
        }

        EmpLog.Verbose("Closing connection {@Peer}",
            peer);

        // reciprocal disconnect. Whatever the game does for the player leaving, the link goes: a throw there left
        // the peer in the list for good, a ghost every message still went to (reliability audit 2026-10-27)
        try {
            _listener?.OnPeerDisconnected(peer, reason);
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Exception while {@Peer} left, its link is closed anyway", peer);
        } finally {
            if (!NetShutdown.IsQuitting) {
                // last chance for what still waits, the connection lingers on what Steam took
                peer.Flush();

                SteamNetworkingSockets.SetConnectionPollGroup(peer.Connection, HSteamNetPollGroup.Invalid);

                SteamNetworkingSockets.CloseConnection(peer.Connection, 0, reason, true);
            }

            _peers.Remove(peer);
            _peersCopy = null;
            _broadcast.RemoveTarget(peer);

            peer.Dispose();
        }
    }

    private ISteamNetPeer AddConnection(HSteamNetConnection connection)
    {
        EmpLog.Verbose("Adding connection handle to poll group");

        if (_peers.FirstOrDefault(p => p.Connection == connection) is { } duplicate) {
            EmpLog.Verbose("Duplicate connection! Ignored");
            return duplicate;
        }

        var peer = new SteamNetPeer(connection, _serializer);
        if (_devIdentities.Remove(connection, out var devIdentity)) {
            peer.UseDevIdentity(devIdentity);
        }

        if (!peer.IsConnected) {
            SteamNetworkingSockets.CloseConnection(peer.Connection, 0, EmpDisconnectInfo.RemoteClosed, true);
            return FakePeer;
        }

        peer.User.SetPlayedWith();

        // the same account again while its old link is still held (a crash, a cut on its side): the new link
        // replaces the old one, which would stay a ghost until it timed out (upstream 6f0fda1)
        foreach (var stale in _peers.Where(p => p.User == peer.User).ToArray()) {
            EmpLog.Information("Replacing connection of {@Peer} with a new one", stale);
            Disconnect(stale, EmpDisconnectInfo.RemoteClosed);
        }

        SteamNetworkingSockets.SetConnectionPollGroup(connection, _pollGroup);

        _peers.Add(peer);
        _peersCopy = null;
        _broadcast.AddTarget(peer);

        NetCompany.Invalidate();

        try {
            _listener?.OnPeerConnected(peer);
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Exception while {@Peer} arrived", peer);
        }

        EmpLog.Debug("Player {PlayerName} {RemoteIdentity} connected, claiming index {PeerIndex}",
            peer.User.Name, peer.User, peer.Id);

        return peer;
    }

    private void HandleStatusChange(SteamNetConnectionStatusChangedCallback_t status)
    {
        var connection = status.m_hConn;

        // the callback is process-wide: an incoming connection is only ours on our own listen socket
        // (an outgoing one is tracked in _peers, the cases below look it up there)
        if (IsHost && status.m_info.m_hListenSocket != HSteamListenSocket.Invalid
            && status.m_info.m_hListenSocket != _listenSocket
            // (the Steam socket a dedicated server opens next to its local port: the players from the internet come
            // in there; left out, they entered the lobby and timed out, "no answer", user's two-PC test 2026-10-30)
            && status.m_info.m_hListenSocket != _steamListenSocket
            && !_devListenSockets.ContainsKey(status.m_info.m_hListenSocket)) {
            return;
        }

        switch (status.m_info.m_eState) {
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                if (IsHost) {
                    // we only accept as host
                    AcceptIfHost(connection, status.m_info);
                }

                break;
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                // this should only call for host
                // but check just in case
                if (IsHost && (_peers.Any(p => p.Connection == connection) || status.m_info.m_hListenSocket != HSteamListenSocket.Invalid)) {
                    AddConnection(connection);
                }

                break;
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                var peer = _peers.FirstOrDefault(p => p.Connection == connection);
                if (peer is not null) {
                    Disconnect(peer, status.m_info.m_szEndDebug);
                }

                break;
            // the link is lost on this side (no answer for too long): closed now, not left as a ghost (upstream 6f0fda1)
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                var lost = _peers.FirstOrDefault(p => p.Connection == connection);
                if (lost is not null) {
                    EmpLog.Debug("Lost connection to {@Peer}: {Reason}", lost, status.m_info.m_szEndDebug);
                    Disconnect(lost, EmpDisconnectInfo.Timeout);
                }

                break;
        }
    }

#endregion

#region Cleanups

    ~SteamNetManager()
    {
        Dispose(false);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) {
            return;
        }

        _disposed = true;

        if (!disposing) {
            return;
        }

        _broadcast.Dispose();

        if (NetShutdown.IsQuitting) {
            return;
        }

        SteamCallback<SteamNetConnectionStatusChangedCallback_t>.Remove(HandleStatusChange);

        DiscardListenSocket();
        DestroyPollGroup();
    }

#endregion
}