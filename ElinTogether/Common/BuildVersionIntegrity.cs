using System;
using System.Security.Cryptography;
using System.Text;

namespace ElinTogether.Common;

public class BuildVersionIntegrity : EClass
{
    public enum APIVersion
    {
        V1 = 1,
    }

    public const APIVersion APIVersionLatest = APIVersion.V1;

    public static string GameVersion => $"{core.version.major}.{core.version.minor}.{core.version.batch}.{core.version.fix}";

    // HSteamConnection.m_UserData
    public static long VersionStringToLong()
    {
        return VersionStringToLong(ModInfo.BuildVersion, GameVersion);
    }

    public static long VersionStringToLong(string mod, string version)
    {
        var raw = $"{APIVersionLatest}|{mod}|{version}";
        using var sha = SHA256.Create();
        var folded = BitConverter.ToInt64(sha.ComputeHash(Encoding.UTF8.GetBytes(raw)), 0);
        return ((long)APIVersionLatest << 56) | (folded & 0x00FFFFFFFFFFFFFFL);
    }

    public static bool Ok(string? mod, string? version, APIVersion api = APIVersionLatest)
    {
        return api == APIVersionLatest &&
               string.Equals(mod, ModInfo.BuildVersion, StringComparison.Ordinal) &&
               string.Equals(version, GameVersion, StringComparison.Ordinal);
    }

    public static string GtfoReason()
    {
        return $"emp_version_mismatch|{ModInfo.BuildVersion}|{GameVersion}";
    }

    public static bool GetGtfoReason(string? reason, out string mod, out string version)
    {
        mod = version = "";
        if (reason is null || !reason.StartsWith("emp_version_mismatch|", StringComparison.Ordinal)) {
            return false;
        }

        var parts = reason.Split('|');
        if (parts.Length != 3) {
            return false;
        }

        mod = parts[1];
        version = parts[2];
        return true;
    }
}