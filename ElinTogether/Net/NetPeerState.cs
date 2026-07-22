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

    [Key(7)]
    public float AvgPingMs { get; set; }

    [Key(8)]
    public float ConnectionQualityLocal { get; set; }

    [Key(9)]
    public float ConnectionQualityRemote { get; set; }

    public Chara? FindChara()
    {
        return EClass.pc.party.members.Find(c => c.uid == CharaUid);
    }
}