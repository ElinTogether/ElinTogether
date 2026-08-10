using System;

namespace ElinTogether.Common;

public class BuildVersionIntegrity : EClass
{
    public enum APIVersion
    {
        V1 = 1,
        // sleep
        V2 = 2,
        // currency
        V3 = 3,
        // refuel + toggle/charge channels
        V4 = 4,
    }

    public const APIVersion APIVersionLatest = APIVersion.V4;

    public static string GameVersion => $"{core.version.major}.{core.version.minor}.{core.version.batch}.{core.version.fix}";

    // HSteamConnection.m_UserData
    public static long VersionStringToLong()
    {
        return VersionStringToLong(ModInfo.BuildVersion, GameVersion);
    }

    public static long VersionStringToLong(string mod, string version)
    {
        var raw = $"{APIVersionLatest}|{mod}|{version}";
        var folded = BitConverter.ToInt64([..raw.GetSha256Hash()], 0);
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