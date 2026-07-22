using HeathenEngineering.SteamworksIntegration;
using Steamworks;

namespace ElinTogether.Net.Steam;

internal class SteamNetPeerFake : ISteamNetPeer
{
    public int Id => -1;
    public UserData User => CSteamID.Nil;
    public bool IsConnected => true;
    public SteamNetPeerStat Stat => field ??= new();

    public bool Send(byte[] bytes, SteamNetSendFlag sendFlags = SteamNetSendFlag.Reliable)
    {
        return true;
    }

    public bool Send<T>(T packet, SteamNetSendFlag sendFlags = SteamNetSendFlag.Reliable)
    {
        return true;
    }
}