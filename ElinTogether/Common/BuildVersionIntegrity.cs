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
        // branch resource channel
        V5 = 5,
        // identify + invowner effect channels
        V6 = 6,
        // build side effects ride in CharaBuildDelta
        V7 = 7,
    }

    public const APIVersion APIVersionLatest = APIVersion.V7;

    public static string GameVersion => $"{core.version.major}.{core.version.minor}.{core.version.batch}.{core.version.fix}";

    // HSteamConnection.m_UserData
    // the version of the game is not part of it: two players on different versions of Elin are let in (and told),
    // unless the host asks for the same one, see ElinNetHostIntegrity
    public static long VersionStringToLong()
    {
        return VersionStringToLong(ModInfo.BuildVersion, "");
    }

    public static long VersionStringToLong(string mod, string version)
    {
        var raw = $"{APIVersionLatest}|{mod}|{version}";
        var folded = BitConverter.ToInt64([..raw.GetSha256Hash()], 0);
        return ((long)APIVersionLatest << 56) | (folded & 0x00FFFFFFFFFFFFFFL);
    }

    public static bool Ok(string? mod, string? version, APIVersion api = APIVersionLatest)
    {
        // the same mod, speaking the same protocol. The version of the game is looked at apart (SameGame): a
        // difference there is a warning, not a wall
        return api == APIVersionLatest &&
               string.Equals(mod, ModInfo.BuildVersion, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The Elin build this release is compiled and tested against (the Nightly branch). Bump it with each release
    /// </summary>
    public const int BuiltForGame = 23357;

    public static string BuiltForGameText => $"EA {BuiltForGame / 1000}.{BuiltForGame % 1000}";

    /// <summary>
    ///     This game is older than the build the mod was made for (the Stable branch lags behind the Nightly one)
    /// </summary>
    public static bool OlderGame => core.version.minor * 1000 + core.version.batch < BuiltForGame;

    /// <summary>
    ///     "0.23.356.1" -> "EA 23.356 Patch 1", as the game itself names its versions (anything else as it is)
    /// </summary>
    public static string Display(string? version)
    {
        var parts = version?.Split('.');
        if (parts is not { Length: 4 } || !int.TryParse(parts[1], out var minor) ||
            !int.TryParse(parts[2], out var batch) || !int.TryParse(parts[3], out var fix)) {
            return version ?? "?";
        }

        return $"EA {minor}.{batch}" + (fix == 0 ? "" : $" Patch {fix}");
    }

    public static bool SameGame(string? version)
    {
        return string.Equals(version, GameVersion, StringComparison.Ordinal);
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