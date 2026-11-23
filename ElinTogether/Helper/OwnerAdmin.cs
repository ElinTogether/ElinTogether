using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.Net;
using HeathenEngineering.SteamworksIntegration;
using ReflexCLI.Attributes;
using UnityEngine;

namespace ElinTogether.Helper;

/// <summary>
///     The lead dev asked for it on 2026-11-23: in the public world (peer-hosted, <see cref="PeerHosting.Active" />)
///     his own Steam account playing a character named exactly "ADMIN" is invincible, keeps every stat at
///     <see cref="MaxStat" />, may use the game's cheats and <c>emp.admin_go</c> (NoCheatsPatch lets him), and makes
///     no shared time pass (shared speed, combat turn budget, world clock reports and travel steps leave him out).
///     The game that keeps the world decides it from the peer's authenticated Steam id (NetPeerState.User), never
///     from what a client says; his own game checks its own Steam id for the local effects
/// </summary>
[ConsoleCommandClassCustomizer("emp")]
internal static class OwnerAdmin
{
    internal const ulong SteamId = 76561198160900031UL;
    internal const string CharaName = "ADMIN";

    /// <summary>
    ///     Main attributes and skills are kept at this (SPD left alone: it sets everyone's pace on a shared map)
    /// </summary>
    internal const int MaxStat = 1000;

#if DEBUG
    /// <summary>
    ///     Bench only: counts as the public world without the peer-hosted setup
    /// </summary>
    internal static bool BenchForce { get; set; }

    /// <summary>
    ///     Bench only: the bench's own Steam account stands for the owner's
    /// </summary>
    internal static ulong BenchAccount { get; set; }

    private static bool PublicWorld => BenchForce || PeerHosting.Active;

    private static ulong Account => BenchAccount != 0 ? BenchAccount : SteamId;
#else
    private static bool PublicWorld => PeerHosting.Active;

    private static ulong Account => SteamId;
#endif

    /// <summary>
    ///     This game's own character is the owner's admin character
    /// </summary>
    internal static bool IsMe => EClass.core.IsGameStarted && EClass.pc is { } pc && Is(pc);

    internal static bool Grants(ulong user, Chara? chara)
    {
        return user == Account && chara?.c_altName == CharaName && PublicWorld;
    }

    /// <summary>
    ///     The game that keeps the map (the world's host or a player's zone session): that peer is the admin
    /// </summary>
    internal static bool IsPeer(ElinNetHost host, int peerId)
    {
        // (its character may be between maps a moment: the name is checked once it is there, the account always)
        return host.States.TryGetValue(peerId, out var state) && state.User == Account && PublicWorld &&
               (host.PlayerChara(peerId) is not { } chara || chara.c_altName == CharaName);
    }

    internal static bool Is(Chara? chara)
    {
        if (chara is null || chara.c_altName != CharaName || !PublicWorld) {
            return false;
        }

        // its own game: its own Steam login
        if (chara.IsPC) {
            return (ulong)UserData.Me == Account;
        }

        var session = NetSession.Instance;
        if (session.Connection is ElinNetHost host) {
            foreach (var state in host.States.Values) {
                if (state.Index != 0 && state.CharaUid == chara.uid) {
                    return Grants(state.User, chara);
                }
            }

            return false;
        }

        // another player's game: what the host said (NetPeerState.OwnerAdmin)
        foreach (var state in session.CurrentPlayers) {
            if (state.CharaUid == chara.uid) {
                return state.OwnerAdmin;
            }
        }

        return false;
    }

    private static readonly Dictionary<int, float> _keptAt = [];
    private static int[]? _statIds;

    /// <summary>
    ///     Full HP, MP and stamina; attributes and skills back at <see cref="MaxStat" /> every few seconds
    /// </summary>
    internal static void Keep(Chara chara)
    {
        if (chara.hp < chara.MaxHP) {
            chara.hp = chara.MaxHP;
        }

        var mana = chara.mana;
        if (mana.value < mana.max) {
            mana.value = mana.max;
        }

        var stamina = chara.stamina;
        if (stamina.value < stamina.max) {
            stamina.value = stamina.max;
        }

        var now = Time.realtimeSinceStartup;
        if (_keptAt.TryGetValue(chara.uid, out var at) && now - at < 5f && now >= at) {
            return;
        }

        _keptAt[chara.uid] = now;
        _statIds ??= [
            ..Enumerable.Range(SKILL.STR, SKILL.LUC - SKILL.STR + 1),
            ..EClass.sources.elements.rows.Where(r => r.category == "skill").Select(r => r.id),
        ];
        foreach (var id in _statIds) {
            var element = chara.elements.GetElement(id);
            if (element is null || element.vBase != MaxStat) {
                chara.elements.SetBase(id, MaxStat, element?.vPotential ?? 100);
            }
        }
    }

    /// <summary>
    ///     Zones of the world map the admin can go to, "uid id name" per line
    /// </summary>
    [ConsoleCommand("admin_zones")]
    internal static string ListZones()
    {
        if (!IsMe) {
            return "Only for the owner's ADMIN character in the public world.";
        }

        return string.Join("\n", Destinations().Select(z => $"{z.uid} {z.id} {z.Name}"));
    }

    /// <summary>
    ///     Goes at once to a zone of the world map (its uid, id or part of its name), as a scroll of return lands
    /// </summary>
    [ConsoleCommand("admin_go")]
    internal static string Go(string where)
    {
        if (!IsMe) {
            return "Only for the owner's ADMIN character in the public world.";
        }

        var zones = Destinations().ToList();
        var zone = zones.FirstOrDefault(z => z.uid.ToString() == where) ??
                   zones.FirstOrDefault(z => string.Equals(z.id, where, StringComparison.OrdinalIgnoreCase)) ??
                   zones.FirstOrDefault(z => z.Name.IndexOf(where, StringComparison.OrdinalIgnoreCase) >= 0);
        if (zone is null) {
            return $"No zone \"{where}\" (emp.admin_zones lists them).";
        }

        EClass.player.uidLastTravelZone = 0;
        EClass.pc.MoveZone(zone, ZoneTransition.EnterState.Return);
        EClass.player.lastZonePos = null;
        return $"Going to {zone.Name}.";
    }

    private static IEnumerable<Zone> Destinations()
    {
        return EClass.game.spatials.map.Values.OfType<Zone>()
            .Where(z => z != EClass._zone && !z.IsInstance && !z.destryoed && z.parent == EClass.world.region)
            .OrderBy(z => z.uid);
    }
}
