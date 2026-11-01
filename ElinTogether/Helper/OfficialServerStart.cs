using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ElinTogether.Net;
using ElinTogether.Net.Steam;
using UnityEngine;

namespace ElinTogether.Helper;

/// <summary>
///     What the official public server sets up by itself at each start, so that its operator only starts it
///     (PLAN_official_server sections 1 and 3): the whole official preset, the admins and moderators listed in two
///     plain files of its ElinMP folder, and the message of the day and the rules when the operator wrote none.
///     Only for a game started headless with -empserver on the Elin Together account with the key (CanHost): a game
///     hosted from the menu, or a bench game forcing the official limits, is left as it is
/// </summary>
internal static class OfficialServerStart
{
    private static string Folder => Path.Combine(Application.persistentDataPath, "ElinMP");

    // (a peer-hosted world plays its signed rules and lists only, even kept by this PC: PeerRules, PeerHosting)
    internal static bool Applies => EmpServer.Requested && OfficialServer.CanHost() && !PeerHosting.Active;

    /// <summary>
    ///     Called at the start of the session, before the rules are read for the players
    /// </summary>
    internal static void Run()
    {
        if (!Applies) {
            return;
        }

        try {
            Components.SettingsTable.ApplyOfficialPreset(File.Exists(Path.Combine(Folder, "official_members_only")));
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Official server: the official preset could not be applied");
        }

        Seed("official_admins.txt", ElinNetHost.Admins, "admin");
        Seed("official_moderators.txt", ElinNetHost.Moderators, "mod");
        WriteIfMissing("motd.txt", Motd);
        WriteIfMissing("rules.txt", Rules);
    }

    /// <summary>
    ///     One Steam id per line ('#' starts a comment): added to that list of the world, never removed from it
    /// </summary>
    private static void Seed(string file, Dictionary<ulong, int> list, string verb)
    {
        var path = Path.Combine(Folder, file);
        if (!File.Exists(path)) {
            return;
        }

        try {
            var added = 0;
            foreach (var raw in File.ReadAllLines(path)) {
                var line = raw.Split('#')[0].Trim();
                if (!ulong.TryParse(line, out var user) || user == 0) {
                    if (line.Length > 0) {
                        EmpLog.Warning("Official server: {File}: '{Line}' is not a Steam id, skipped", file, line);
                    }

                    continue;
                }

                // (an admin needs no moderator role)
                if (list.ContainsKey(user) || verb == "mod" && ElinNetHost.Admins.ContainsKey(user)) {
                    continue;
                }

                list[user] = 1;
                ElinNetHost.AdminLog($"{verb} {user} (from {file})");
                added++;
            }

            EmpLog.Information("Official server: {File} read, {Added} added, {Count} in the list", file, added, list.Count);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            EmpLog.Warning(ex, "Official server: {File} could not be read", file);
        }
    }

    private static void WriteIfMissing(string file, string text)
    {
        if (File.Exists(Path.Combine(Folder, file))) {
            return;
        }

        ServerInfo.WriteFile(file, text);
        EmpLog.Information("Official server: {File} was missing, the plan's text was written", file);
    }

    // PLAN_official_server section 3: English, then Chinese and Japanese on the next lines (the files are the
    // server's, the same for every player whatever its language)
    private const string Motd =
        "Welcome to the Elin Together public server (run by the mod's team, not by the developer of Elin). Beta: the world may roll back. Report bugs with Multiplayer > Report a bug. Be kind.\n" +
        "欢迎来到 Elin Together 公共服务器（由本模组团队运营，与 Elin 开发者无关）。测试阶段：世界可能回档。遇到问题请用 多人游戏 > 反馈问题。请友善待人。\n" +
        "Elin Together 公開サーバーへようこそ（MODのチームが運営しており、Elinの開発者とは関係ありません）。ベータ版：ワールドが巻き戻ることがあります。不具合は Multiplayer > バグ報告 から。みんなに優しく。";

    private const string Rules =
        "1. No harassment, spam or hate.\n" +
        "2. No exploits, duping or modified clients.\n" +
        "3. Do not take from others' lands or block towns.\n" +
        "4. One player, up to 3 characters.\n" +
        "5. Moderators: see the Players page.\n" +
        "Breaking a rule: mute, kick, then ban.\n" +
        "1. 禁止骚扰、刷屏与仇恨言论。\n" +
        "2. 禁止利用漏洞、复制物品或使用修改客户端。\n" +
        "3. 不得拿取他人领地物品或堵塞城镇。\n" +
        "4. 每位玩家最多 3 个角色。\n" +
        "5. 管理员：见玩家页面。\n" +
        "违规：禁言、踢出、封禁。\n" +
        "1. 嫌がらせ、スパム、差別的な発言は禁止。\n" +
        "2. 不具合の悪用、アイテム複製、改造クライアントは禁止。\n" +
        "3. 他人の土地から持ち出したり、町をふさいだりしないこと。\n" +
        "4. 1人につきキャラクターは3人まで。\n" +
        "5. モデレーター：プレイヤー一覧のページを参照。\n" +
        "違反した場合：ミュート、キック、BANの順。";
}
