using System;
using System.IO;
using System.Linq;
using ElinTogether.Net;
using HarmonyLib;
using UnityEngine;

namespace ElinTogether.Helper;

/// <summary>
///     The host's message of the day and rules (council 2026-10-28, gap 1). The host writes them in two plain files of
///     its ElinMP folder (motd.txt, rules.txt) or from the Server page; they travel with the world's rules, so a player
///     reads them whenever it likes in the Elin Together menu. A player who has not read this text yet gets one line in
///     the game's own message log, never a window or a question
/// </summary>
[HarmonyPatch]
internal static class ServerInfo
{
    internal const int MaxLength = 2000;
    private static bool _pending;

    private static string FilePath(string name)
    {
        return Path.Combine(Application.persistentDataPath, "ElinMP", name);
    }

    /// <summary>
    ///     What the host wrote in that file; empty when there is none
    /// </summary>
    internal static string ReadFile(string name)
    {
        // a peer-hosted world: the signed texts, whatever the keeper's files say
        if (PeerRules.Text(name) is { } signed) {
            return signed;
        }

        return ReadLocalFile(name);
    }

    /// <summary>
    ///     What this game's own file says, signed texts or not (the world admin page signs these)
    /// </summary>
    internal static string ReadLocalFile(string name)
    {
        try {
            var text = File.Exists(FilePath(name)) ? File.ReadAllText(FilePath(name)).Replace("\r", "").Trim() : "";
            return text.Length > MaxLength ? text.Substring(0, MaxLength) : text;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return "";
        }
    }

    /// <summary>
    ///     A bar in the text is a line break (the game's input box is one line)
    /// </summary>
    internal static void WriteFile(string name, string text)
    {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath(name))!);
            File.WriteAllText(FilePath(name), text.Replace("|", "\n").Trim());
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            EmpLog.Warning(ex, "Server text {Name} not written", name);
        }
    }

    internal static string Motd => NetSession.Instance.Rules.Motd ?? "";
    internal static string RulesText => NetSession.Instance.Rules.RulesText ?? "";

    /// <summary>
    ///     Same in every game: a hash of what this player would read
    /// </summary>
    private static string Digest()
    {
        var h = 17;
        foreach (var c in Motd + "\n" + RulesText) {
            h = h * 31 + c;
        }

        return h.ToString("x");
    }

    /// <summary>
    ///     The rules of the world reached this game (the host's own at its start, a guest's at its join)
    /// </summary>
    internal static void Arrived()
    {
        _pending = true;
        if (Since <= 0f) {
            Since = UnityEngine.Time.realtimeSinceStartup;
        }
    }

    /// <summary>When this game started hosting or joined (real time), for the Status page's session time</summary>
    internal static float Since { get; set; }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Zone), nameof(Zone.Activate))]
    internal static void OnZoneActive()
    {
        if (!_pending || EClass.pc is null) {
            return;
        }

        _pending = false;
        if (Motd.Length + RulesText.Length == 0 || PlayerSocial.HasSeen(Digest())) {
            return;
        }

        PlayerSocial.MarkSeen(Digest());
        Msg.SayRaw("emp_info_notice".lang());
    }

    internal static string[] Lines(string text)
    {
        return text.Split('\n').Where(l => l.Length > 0).ToArray();
    }
}
