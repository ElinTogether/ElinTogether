using ElinTogether.Helper.String;
using ElinTogether.Net.Steam;
using UnityEngine;

namespace ElinTogether.Helper;

internal static class PeerColorizer
{
    internal static int GetColorInt(int peerIndex)
    {
        return peerIndex switch {
            1 => 0x0072b2,
            2 => 0xe69f00,
            3 => 0xcc79a7,
            4 => 0x009e73,
            _ => HSVColorInt(peerIndex * 0.618f % 1f, 0.65f, 0.85f),
        };

        static int HSVColorInt(float h, float s, float v)
        {
            var c = Color.HSVToRGB(h, s, v);
            return ((int)(c.r * 255) << 16) | ((int)(c.g * 255) << 8) | (int)(c.b * 255);
        }
    }

    internal static Color GetColor(int peerIndex)
    {
        var rgb = GetColorInt(peerIndex);
        return new Color32(
            (byte)((rgb >> 16) & 0xFF),
            (byte)((rgb >> 8) & 0xFF),
            (byte)(rgb & 0xFF),
            255);
    }

    extension(ISteamNetPeer peer)
    {
        internal string Colorize(object input)
        {
            return input.TagColor(GetColorInt(peer.Id));
        }
    }
}