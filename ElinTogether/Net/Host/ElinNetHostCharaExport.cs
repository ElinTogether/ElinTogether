using System;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net.Steam;

namespace ElinTogether.Net;

/// <summary>
///     A player takes one of its characters to one of its own solo saves (council 2026-10-27, see CharaExport). The
///     host sends its own copy from the character choice screen (the character is not played: nothing in flight)
///     and keeps the character away, in the roster and counted by MaxCharacters, not playable, until it comes back
///     through the import with the matching token or is brought back unchanged. The world's own player character
///     IS the world and has no choice screen: it is not exportable (the one host/guest exception, for the user to
///     decide later)
/// </summary>
internal partial class ElinNetHost
{
    // the way back is the import: no export without it, and never on the official server (it adds no trust)
    private bool ExportAllowed => !IsZoneSession && EmpConfig.Server.ExportCharacter.Value &&
                                  EmpConfig.Server.ImportCharacter.Value && !OfficialServer.Hosting;

    /// <summary>
    ///     Net event: the player takes a character to a solo save, or brings one back unchanged
    /// </summary>
    private void OnSessionCharaExportRequest(SessionCharaExportRequest request, ISteamNetPeer peer)
    {
        if (ActiveRemoteCharas.ContainsKey(peer.Id) || IsZoneSession) {
            return;
        }

        if (RosterOf(peer.User).Find(c => c.uid == request.Uid) is not { } chara) {
            EmpLog.Warning("Player {@Peer} asked for chara {Uid} which is not one of its own", peer, request.Uid);
            PreparePlayerJoin(peer);
            return;
        }

        // whatever the settings say now: a character is never stuck away
        if (request.Recall) {
            if (CharaExport.IsAway(chara)) {
                AdminLog($"recall character {chara.uid} ({chara.Name}) of {peer.User} from {chara.GetStr(CharaExport.AwaySaveKey)}: back unchanged, its token void");
                EmpLog.Information("Player {@Peer} brings {Name} back unchanged", peer, chara.Name);
                ClearAway(chara);
            }

            PreparePlayerJoin(peer, "emp_ui_chara_recalled");
            return;
        }

        if (!ExportAllowed || !ImportTrusted(peer.User)) {
            EmpLog.Warning("Player {@Peer} asked to take chara {Uid} to a solo save while it is not allowed", peer, chara.uid);
            PreparePlayerJoin(peer);
            return;
        }

        if (CharaExport.IsAway(chara) || chara.isDead) {
            PreparePlayerJoin(peer, "emp_ui_chara_export_fail");
            return;
        }

        var token = Guid.NewGuid().ToString("N");
        LZ4Bytes data;
        try {
            data = CharaExport.Pack(chara, token);
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Could not pack chara {Uid} for player {@Peer}", chara.uid, peer);
            PreparePlayerJoin(peer, "emp_ui_chara_export_fail");
            return;
        }

        // away before the copy leaves: whatever happens next (no answer, a lost link), it is not played twice.
        // Only its player's recall frees it
        var save = request.Save is { Length: > 80 } named ? named[..80] : request.Save ?? "?";
        chara.SetStr(CharaExport.AwayKey, token);
        chara.SetStr(CharaExport.AwaySaveKey, save);
        chara.SetStr(CharaExport.AwayFromKey, CharaExport.Summary(chara));
        if (SavedRemoteCharas.TryGetValue(peer.User, out var played) && played == chara.uid) {
            SavedRemoteCharas.Remove(peer.User);
        }

        var standing = PlayerStandings.TryGetValue(chara.uid, out var kept) && kept.Length >= 2 ? kept : [0, 0];
        AdminLog($"export character {chara.uid} ({chara.Name}, {CharaExport.Summary(chara)}) of {peer.User} to its solo save {save}");
        EmpLog.Information("Player {@Peer} takes {Name} to its solo save {Save}", peer, chara.Name, save);

        // saved before the copy leaves: a crash before the next autosave would reload the character playable while
        // the player's incoming file brings a second one into the solo save
        EmpAutoHost.SaveFromMessage("a character left for a solo save");

        peer.Send(new SessionCharaExportData {
            Uid = chara.uid,
            Chara = data,
            Fame = standing[0],
            Karma = standing[1],
            Token = token,
        });
    }

    /// <summary>
    ///     Net event: the player's game wrote the character beside its solo save, or could not (then it never left)
    /// </summary>
    private void OnSessionCharaExportResult(SessionCharaExportResult result, ISteamNetPeer peer)
    {
        if (ActiveRemoteCharas.ContainsKey(peer.Id) || IsZoneSession) {
            return;
        }

        if (RosterOf(peer.User).Find(c => c.uid == result.Uid) is not { } chara ||
            chara.GetStr(CharaExport.AwayKey) != result.Token) {
            PreparePlayerJoin(peer);
            return;
        }

        if (!result.Written) {
            AdminLog($"export of character {chara.uid} ({chara.Name}) of {peer.User} not written by its game: released");
            ClearAway(chara);
        }

        PreparePlayerJoin(peer, result.Written ? "emp_ui_chara_exported" : "emp_ui_chara_export_fail");
    }

    private static void ClearAway(Chara chara)
    {
        chara.SetStr(CharaExport.AwayKey, null);
        chara.SetStr(CharaExport.AwaySaveKey, null);
        chara.SetStr(CharaExport.AwayFromKey, null);
    }

    /// <summary>
    ///     The character back from a solo save takes the place of the one that waited: its slot in the roster, its
    ///     companions, its team. The token is spent with the one that waited. What changed is in admin.log (the
    ///     solo save is the player's: no refusal, the host reads it)
    /// </summary>
    private void ReplaceAway(ulong user, Chara away, Chara back)
    {
        back.SetStr(CharaExport.StampKey, null);

        if (PlayerRosters.TryGetValue(user, out var roster) && roster.IndexOf(away.uid) is var slot and >= 0) {
            roster[slot] = back.uid;
        }

        if (SavedRemoteCharas.TryGetValue(user, out var played) && played == away.uid) {
            SavedRemoteCharas[user] = back.uid;
        }

        foreach (var companion in game.cards.globalCharas.Values.Where(c => c is not null && c.CompanionOwnerUid == away.uid).ToList()) {
            companion.SetCompanionOwner(back);
        }

        if (Teams.TeamOf(away.uid) is var team and not Teams.Wanderers) {
            Teams.Join(back, team);
            Teams.Leave(away);
        } else {
            // (in no team: its own lands are the returning character's)
            Teams.MoveLands(-away.uid, -back.uid);
        }

        // the lands it claimed earn for, and are billed to, the one now played; its unpaid bills follow it
        foreach (var land in EClass.pc.faction.GetChildren().Select(b => b.owner).Where(z => z?.GetInt(BaseIncome.ClaimerKey) == away.uid)) {
            land.SetInt(BaseIncome.ClaimerKey, back.uid);
        }

        if (TeamTaxBills.TryGetValue(-away.uid, out var owed)) {
            TeamTaxBills.Remove(-away.uid);
            TeamTaxBills.TryGetValue(-back.uid, out var already);
            TeamTaxBills[-back.uid] = already + owed;
        }

        AdminLog($"return character {away.uid} -> {back.uid} ({back.Name}) of {user} from {away.GetStr(CharaExport.AwaySaveKey)}: " +
                 $"left with {away.GetStr(CharaExport.AwayFromKey)}, back with {CharaExport.Summary(back)}");
        EmpLog.Information("Chara {Away} back from a solo save as {Back} ({Name})", away.uid, back.uid, back.Name);

        PlayerStandings.Remove(away.uid);
        // (a plain Destroy left it at the base, among the players: see DestroyPlayerChara)
        DestroyPlayerChara(away);

        // the same on the way back: a crash must not reload the old away character beside the one now played
        EmpAutoHost.SaveFromMessage("a character came back from a solo save");
    }
}
