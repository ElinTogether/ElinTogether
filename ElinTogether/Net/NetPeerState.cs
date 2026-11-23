using System;
using HeathenEngineering.SteamworksIntegration;
using MessagePack;

namespace ElinTogether.Net;

[MessagePackObject]
public class NetPeerState
{
    [Key(0)]
    public required int Index { get; init; }

    [Key(1)]
    public required UserData User { get; init; }

    [Key(2)]
    public required int CharaUid { get; init; }

    [Key(3)]
    public int Speed { get; set; }

    [Key(4)]
    public int LastAct { get; set; }

    [Key(5)]
    public int LastReceivedTick { get; set; } = -1;

    [Key(6)]
    public int LastPingMs { get; set; }

    [IgnoreMember]
    internal float AvgPingMs { get; set; }

    [IgnoreMember]
    internal float ConnectionQualityLocal { get; set; }

    [IgnoreMember]
    internal float ConnectionQualityRemote { get; set; }

    // on the wire a whole millisecond and a whole percent (one or two bytes instead of five per float): this goes
    // to every player every 2 s, and the panel shows them that way (efficiency audit 2026-10-27)
    [Key(7)]
    public int AvgPingMsWire
    {
        get => (int)Math.Round(AvgPingMs);
        set => AvgPingMs = value;
    }

    [Key(8)]
    public int ConnectionQualityLocalWire
    {
        get => (int)Math.Round(ConnectionQualityLocal * 100f);
        set => ConnectionQualityLocal = value / 100f;
    }

    [Key(9)]
    public int ConnectionQualityRemoteWire
    {
        get => (int)Math.Round(ConnectionQualityRemote * 100f);
        set => ConnectionQualityRemote = value / 100f;
    }

    [Key(10)]
    public bool Turbo { get; set; }

    [Key(11)]
    public bool Talking { get; set; }

    /// <summary>
    ///     Away from the keyboard, as the host of the world knows it (see PlayerIdle)
    /// </summary>
    [Key(12)]
    public bool Idle { get; set; }

    /// <summary>
    ///     The owner's admin character of the public world, as the host decided it from this peer's Steam id
    ///     (Helper.OwnerAdmin): it counts in no shared speed
    /// </summary>
    [Key(13)]
    public bool OwnerAdmin { get; set; }

    public Chara? FindChara()
    {
        return EClass.pc?.party?.members.Find(c => c?.uid == CharaUid);
    }
}