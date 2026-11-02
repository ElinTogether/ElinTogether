using System;
using System.IO;
using System.Linq;
using ElinTogether.Helper.Extensions;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;
using MessagePack;
using MessagePack.Resolvers;
using UnityEngine;

namespace ElinTogether.Helper;

/// <summary>
///     Taking a co-op character to one of the player's own solo saves (council 2026-10-27, upstream issue #15). The
///     character MOVES: the host marks it away with a one-time token and sends its own copy; the player's game keeps
///     a plain copy of the solo save in the game's backup folder, then writes the character beside the saves
///     (ElinMP/Incoming, not in the save folder: a Steam Cloud save's folder is deleted and unpacked again at load).
///     When that save is next loaded, the character takes the place of its player character in the live game, and
///     the game saves. It comes back only through the import, with the matching token (see ElinNetHost)
/// </summary>
[HarmonyPatch]
[AlwaysPatched]
internal static class CharaExport
{
    /// <summary>
    ///     Card string of the character in the solo save: the token it left with. Host side, the import of a
    ///     character that carries one is a return, never a plain import
    /// </summary>
    internal const string StampKey = "emp_export";

    /// <summary>
    ///     Card string of the character that left, at the host: the token of its trip. Away = not playable
    /// </summary>
    internal const string AwayKey = "emp_away";

    // the save it went to, as the player named it, and what it was when it left (compared in admin.log on return)
    internal const string AwaySaveKey = "emp_away_save";
    internal const string AwayFromKey = "emp_away_from";

    private static readonly MessagePackSerializerOptions FileOptions =
        MessagePackSerializerOptions.Standard
            .WithResolver(StandardResolver.Instance)
            .WithCompression(MessagePackCompression.Lz4BlockArray);

    private static string IncomingFolder => Path.Combine(Application.persistentDataPath, "ElinMP", "Incoming");

    /// <summary>
    ///     ElinMP/Incoming/&lt;save id&gt;.lz4 (Steam Cloud saves have ids of their own, hence the prefix)
    /// </summary>
    internal static string IncomingPath(string id, bool cloud)
    {
        return Path.Combine(IncomingFolder, $"{(cloud ? "cloud_" : "")}{id}.lz4");
    }

    internal static bool IsAway(Chara chara)
    {
        return !chara.GetStr(AwayKey).IsEmpty();
    }

    /// <summary>
    ///     What the admin log compares between the departure and the return
    /// </summary>
    internal static string Summary(Chara chara)
    {
        return $"Lv {chara.LV}, gold {chara.GetCurrency()}, items {chara.things.Flatten().Count()}";
    }

    /// <summary>
    ///     Host side: a copy of the character with nothing of this world around it, stamped with the token
    /// </summary>
    internal static LZ4Bytes Pack(Chara chara, string token)
    {
        var copy = LZ4Bytes.Create(chara).Decompress<Chara>();
        CharaImport.Detach(copy);
        // marks of a player of this world, which mean nothing in a solo game
        copy.SetBool("remote_chara", false);
        copy.SetStr(PlayerRevive.PlayerKey, null);
        copy.SetStr(Net.ElinNetHost.PlayerIdKey, null);
        copy.SetStr(CharaImport.SourceKey, null);
        copy.SetStr(AwayKey, null);
        copy.SetStr(AwaySaveKey, null);
        copy.SetStr(AwayFromKey, null);
        copy.SetStr(StampKey, token);
        return LZ4Bytes.Create(copy);
    }

    /// <summary>
    ///     Player side: a plain copy of the solo save folder in the game's backup folder, then the character beside
    ///     the saves. False when anything failed: the save is untouched and the host releases the character
    /// </summary>
    internal static bool Write(GameIndex save, SessionCharaExportData data)
    {
        var cloud = CharaImport.IsCloud(save);
        var file = IncomingPath(save.id, cloud);
        var temp = file + ".tmp";
        try {
            // another character already waits for that save: it would be lost
            if (File.Exists(file)) {
                EmpLog.Warning("A character already waits for save {Id}, not replaced", save.id);
                return false;
            }

            if (!Directory.Exists(save.path)) {
                EmpLog.Warning("Save folder {Path} not found", save.path);
                return false;
            }

            // not a numbered folder: the game's own backups (GameIO.MakeBackup) only rotate those
            var backup = Path.Combine((cloud ? CorePath.PathBackupCloud : CorePath.PathBackup) + save.id,
                $"emp_export_{DateTime.Now:yyyyMMdd_HHmmss}");
            IO.CopyDir(save.path, backup, name => name == "Temp");
            if (!Directory.Exists(backup)) {
                EmpLog.Warning("No backup of save {Id} in {Backup}", save.id, backup);
                return false;
            }

            Directory.CreateDirectory(IncomingFolder);
            File.WriteAllBytes(temp, MessagePackSerializer.Serialize(data, FileOptions));
            File.Move(temp, file);
            EmpLog.Information("Character {Uid} written for save {Id} ({File}), backup {Backup}", data.Uid, save.id, file, backup);
            return true;
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Could not write the character for save {Id}", save.id);
            try {
                File.Delete(temp);
            } catch {
                // (nothing written)
            }

            return false;
        }
    }

    internal static SessionCharaExportData ReadIncoming(string file)
    {
        return MessagePackSerializer.Deserialize<SessionCharaExportData>(File.ReadAllBytes(file), FileOptions);
    }

    /// <summary>
    ///     The player brings a character back unchanged: its files still waiting beside solo saves are dropped, or
    ///     loading one of them would bring it in a second time
    /// </summary>
    internal static void DropIncoming(int uid)
    {
        try {
            if (!Directory.Exists(IncomingFolder)) {
                return;
            }

            foreach (var file in Directory.GetFiles(IncomingFolder, "*.lz4")) {
                try {
                    if (ReadIncoming(file).Uid == uid) {
                        File.Delete(file);
                        EmpLog.Information("Incoming file {File} of recalled chara {Uid} deleted", file, uid);
                    }
                } catch (Exception ex) {
                    EmpLog.Warning(ex, "Could not check the incoming file {File}", file);
                }
            }
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Could not drop the incoming files of chara {Uid}", uid);
        }
    }

    // the swap of the last load, until a save holds it
    private static (string File, string Token)? _applied;

    /// <summary>
    ///     A save is loaded (not a copy of a shared world: those have no file here): before the game picks its player
    ///     character by uid, the incoming character takes the place of the old one under the same uid, so that the
    ///     party, the companions, the zone and the place on the map are the game's own
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Game), nameof(Game.OnGameInstantiated))]
    internal static void ApplyIncoming(Game __instance)
    {
        _applied = null;
        if (!__instance.isLoading || NetSession.Instance.Transport is not null) {
            return;
        }

        var file = IncomingPath(Game.id, __instance.isCloud);
        if (!File.Exists(file)) {
            return;
        }

        string name;
        try {
            name = Swap(__instance, ReadIncoming(file), file);
        } catch (Exception ex) {
            // the file stays for the next load, the save as it was
            EmpLog.Warning(ex, "Could not bring the incoming character {File} into save {Id}", file, Game.id);
            EClass.core.actionsNextFrame.Add(() => Dialog.Ok("emp_ui_chara_export_load_fail".lang()));
            return;
        }

        if (name.IsEmpty()) {
            return;
        }

        var loaded = __instance;
        EClass.core.actionsNextFrame.Add(() => {
            if (EClass.core.game != loaded) {
                return;
            }

            Msg.Say("emp_ui_chara_export_arrived".lang(name));
            // the save that holds it removes the file (OnSaved)
            loaded.Save(false, true);
        });
    }

    /// <returns>the name of the character that arrived, empty when this save already holds it</returns>
    private static string Swap(Game game, SessionCharaExportData data, string file)
    {
        var old = game.cards.globalCharas.Find(game.player.uidChara)
                  ?? throw new InvalidOperationException("the save has no player character");

        // written by a save already (the file outlived it): nothing to do but forget it
        if (old.GetStr(StampKey) == data.Token) {
            File.Delete(file);
            return "";
        }

        var chara = data.Chara.Decompress<Chara>();
        if (chara.GetStr(StampKey) != data.Token) {
            throw new InvalidOperationException("the character does not carry the token of its file");
        }

        var before = chara.uid;

        // debts of the co-op world
        foreach (var bill in chara.things.Flatten().Where(t => t.trait is TraitBill).ToList()) {
            bill.Destroy();
        }

        // uids of this world for what it carries, the old player character's for itself
        game.cards.AssignUIDRecursive(chara);
        var assigned = chara.uid;
        chara.uid = old.uid;
        foreach (var thing in chara.things.Flatten()) {
            if (thing.c_uidAttune == before || thing.c_uidAttune == assigned) {
                thing.c_uidAttune = chara.uid;
            }
        }

        // what ties the player character to this world stays this world's: zone, home, party, faction (mount below)
        chara._cints[1] = old._cints[1];
        chara._cints[2] = old._cints[2];
        chara.party = old.party;
        chara.global = old.global;
        chara.idFaction = old.idFaction;
        chara.memberType = old.memberType;
        chara.orgPos = old.orgPos;
        chara.pos.Set(old.pos);
        chara.SetBool(CINT.IsPC, old.GetBool(CINT.IsPC));

        if (chara.isDead || chara.hp <= 0) {
            chara.isDead = false;
            chara.hp = 1;
        }

        // (everything above only touched the character that arrives: a failure there leaves the save as it was)
        game.cards.globalCharas.Remove(old.uid);
        game.cards.globalCharas.Add(chara);

        // its mount stays this world's too
        chara.ride = old.ride;
        if (chara.ride is { } ride) {
            ride.host = chara;
        }

        chara.parasite = old.parasite;
        if (chara.parasite is { } parasite) {
            parasite.host = chara;
        }

        // the hotbar points at the old character's things, which stay in the backup only
        foreach (var bar in game.player.hotbars.bars) {
            foreach (var page in bar?.pages ?? []) {
                for (var i = 0; i < page.items.Count; i++) {
                    if (page.items[i] is HotItemThing) {
                        page.items[i] = null;
                    }
                }
            }
        }

        game.player.fame = data.Fame;
        game.player.karma = data.Karma;

        _applied = (file, data.Token);
        EmpLog.Information("Character {Name} ({Summary}) arrived in save {Id} in place of {Old}",
            chara.Name, Summary(chara), Game.id, old.Name);
        return chara.Name;
    }

    /// <summary>
    ///     The save holds the character that arrived: its file goes
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameIO), nameof(GameIO.SaveGame))]
    internal static void OnSaved()
    {
        if (_applied is not ({ } file, { } token) || EClass.pc?.GetStr(StampKey) != token) {
            return;
        }

        try {
            File.Delete(file);
            _applied = null;
            EmpLog.Information("Incoming character saved, {File} removed", file);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            EmpLog.Warning(ex, "Could not remove {File}", file);
        }
    }
}
