using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     A new day raises the days with its god and refreshes the god's blessing of "the player" only
///     (Player.OnAdvanceDay). Each guest's game does it for its own character (WorldDateAdvanceDelta), the host's
///     copy of that character never grew: its blessing here went stale (RemotePietyPatch reads that copy) and every
///     rejoin, given the host's copy, started the count again. The host counts the day for each player character it
///     holds; an upload keeps the higher count (<see cref="Merge" />)
/// </summary>
[HarmonyPatch(typeof(Player), nameof(Player.OnAdvanceDay))]
internal static class RemoteDaysWithGodPatch
{
    [HarmonyPostfix]
    internal static void OnAdvanceDay()
    {
        if (NetSession.Instance.Connection is not ElinNetHost { IsZoneSession: false } || EClass.game?.cards is not { } cards) {
            return;
        }

        foreach (var uid in ElinNetHost.SavedRemoteCharas.Values) {
            if (uid == EClass.pc.uid || cards.globalCharas.Find(uid) is not { } chara) {
                continue;
            }

            chara.c_daysWithGod++;
            chara.RefreshFaithElement();
        }
    }

    /// <summary>
    ///     The days with its god a player's own game counted, or the host's copy if higher (the same god: a new one
    ///     starts the count at 0, Religion.JoinFaith)
    /// </summary>
    internal static void Merge(Chara uploaded, Chara? old)
    {
        if (old is null || old.idFaith != uploaded.idFaith || old.c_daysWithGod <= uploaded.c_daysWithGod) {
            return;
        }

        uploaded.c_daysWithGod = old.c_daysWithGod;
        uploaded.RefreshFaithElement();
    }
}
