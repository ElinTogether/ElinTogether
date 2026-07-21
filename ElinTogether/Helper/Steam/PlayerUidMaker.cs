using System;
using System.Security.Cryptography;
using HeathenEngineering.SteamworksIntegration;
using Steamworks;

namespace ElinTogether.Helper.Steam;

internal class PlayerUidMaker
{
    private static readonly byte[] _seed = new byte[32];

    static PlayerUidMaker()
    {
        RandomNumberGenerator.Fill(_seed);
    }

    internal static ulong MakeSteamUid()
    {
        return (ulong)SteamUser.GetSteamID();
    }

    internal static string MakeConnectionKey(UserData user)
    {
        var input = new byte[_seed.Length + sizeof(ulong)];

        Buffer.BlockCopy(_seed, 0, input, 0, _seed.Length);
        Buffer.BlockCopy(BitConverter.GetBytes(user), 0, input, _seed.Length, sizeof(ulong));

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(input);

        return BitConverter.ToInt64(hash, 0).ToString();
    }
}