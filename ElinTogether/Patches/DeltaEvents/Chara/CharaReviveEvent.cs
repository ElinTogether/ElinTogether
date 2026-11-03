using System.Collections;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;
using UnityEngine;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal static class CharaReviveEvent
{
    private static string? _pendingLastWords;

    // of the request being watched: the grave is made here if this game ends up keeping the map
    private static string? _askedLastWords;

    // requests sent for this death: a keeper that never answers is not asked for ever
    private static int _asks;
    private const int MaxAsks = 3;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.MakeGrave))]
    internal static bool OnCharaMakeGrave(Chara __instance, string lastword)
    {
        if (NetSession.Instance.Connection is not ElinNetClient || !__instance.IsPC) {
            return true;
        }

        _pendingLastWords = lastword;
        _asks = 0;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.Revive))]
    internal static bool OnCharaRevive(Chara __instance, ref bool __state)
    {
        __state = __instance.isDead;

        if (!__instance.isDead) {
            return true;
        }

        if (NetSession.Instance.Connection is not ElinNetClient client || ElinDelta.IsApplying) {
            return true;
        }

        // drop all other character revives and wait for delta
        if (!__instance.IsPC) {
            return false;
        }

        Position? pos = (__instance.pos.IsValid && EClass._map.charas.Contains(__instance)) ? __instance.pos : null;
        EmpLog.Debug("Requesting revive at {@Pos}", pos);

        client.Delta.AddRemote(new CharaReviveDelta {
            Owner = __instance,
            LastWords = _pendingLastWords,
            Pos = pos,
        });
        _askedLastWords = _pendingLastWords ?? _askedLastWords;
        _pendingLastWords = null;
        if (++_asks < MaxAsks) {
            EmpMod.Instance.StartCoroutine(WatchRevive(__instance, client));
        }

        // scene
        EClass.player.deathDialog = true;

        if (!__instance.pos.IsValid) {
            __instance.pos.Set(EClass._map.GetCenterPos());
        }

        return false;
    }

    // a request with no answer leaves the player dead for good
    private const float ReviveWait = 10f;

    /// <summary>
    ///     The revive was asked to the game that keeps the map. If that game stops keeping it before it answers (it
    ///     left and handed the map over, the link changed), or says nothing for a while, the player would stay dead:
    ///     asked again, to whoever keeps the map now (this game's own revive when it is this one). The keeper drops a
    ///     second request for a player already standing
    /// </summary>
    private static IEnumerator WatchRevive(Chara chara, ElinNetBase keeper)
    {
        var since = Time.realtimeSinceStartup;
        yield return null;

        while (EClass.core.IsGameStarted && EClass.pc == chara && chara.isDead) {
            var moved = !ReferenceEquals(NetSession.Instance.Connection, keeper);
            if ((moved || Time.realtimeSinceStartup - since > ReviveWait) && EClass._zone is { IsActiveZone: true } &&
                EClass._map?.charas is not null) {
                EmpLog.Information("Revive not answered ({Reason}), asking again",
                    moved ? "the map changed hands" : "no answer");
                chara.Revive();
                if (!chara.isDead) {
                    // this game keeps the map now: its own revive was played, the grave asked with the request
                    // was never made (OnCharaMakeGrave)
                    chara.MakeGrave(_askedLastWords);
                    _askedLastWords = null;
                    EClass.player.deathDialog = false;
                }

                yield break;
            }

            yield return null;
        }
    }

    // what dying costs a player is settled by the game that simulates the map (CharaReviveDelta): not again
    // here, when its coming back is played in its own game
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.ApplyDeathPenalty))]
    internal static bool OnApplyDeathPenalty()
    {
        // (a permadeath revive at the bartender: its price was the cost, see PlayerRevive)
        if (PlayerRevive.Reviving) {
            return false;
        }

        // (Transport: while away Connection is null, and the revive the host sent there is applied too, GitHub issue 70)
        return NetSession.Instance.Transport is not ElinNetClient || !ElinDelta.IsApplying;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.Revive))]
    internal static void OnCharaReviveEnd(Chara __instance, bool __state)
    {
        if (!__state || __instance.isDead || ElinDelta.IsApplying) {
            return;
        }

        // a companion too: the guests drop the revive of anything but a player (above), the host's game is the one
        // that stands it up
        // (a player away on a map of its own is no active remote chara here, and its revive at a bartender must reach
        // its game all the same: GitHub issue 70, it stayed dead there)
        if (NetSession.Instance.Connection is not ElinNetHost host ||
            !(__instance.IsPlayer || (PlayerRevive.Reviving && PlayerRevive.IsPlayerChara(__instance)) ||
              (__instance.IsPCFaction && __instance.IsInActiveMap))) {
            return;
        }

        EmpLog.Debug("Revive chara {Uid} at {@Pos}",
            __instance.uid, (Position?)(__instance.IsInActiveMap ? __instance.pos : null));

        host.Delta.AddRemote(new CharaReviveDelta {
            Owner = __instance,
            LastWords = null,
            Pos = __instance.IsInActiveMap ? __instance.pos : null,
        });
    }
}