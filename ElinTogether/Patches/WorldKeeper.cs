using System.Collections.Generic;
using System.Reflection;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     What a passing hour, day or month does to the WORLD (not to one map, not to one player) is done once, by
///     one game: the world keeper. Every game that simulates a map makes the date advance in its own copy of the
///     world (the host, and each player alone on a map it holds, see WorldDateAdvanceEvent), and each of them
///     ran these: a quest expired twice and cost fame twice, the month's tax cost karma twice, each copy drew
///     its own weather, salaries and parcels were made in copies that are thrown away. <br />
///     The keeper is the host for now. The role is meant to move, see dev/PLAN_serveur_depot.md
/// </summary>
[HarmonyPatch(typeof(Weather), nameof(Weather.OnChangeHour))]
internal static class WorldKeeper
{
    /// <summary>
    ///     This game does what time does to the world: no session, or the host of one. Not a player who joined
    ///     one, wherever it is
    /// </summary>
    internal static bool IsKeeper => NetSession.Instance.Transport is not ElinNetClient;

    /// <summary>
    ///     This game leaves the world's hooks to the keeper
    /// </summary>
    internal static bool Skip => !IsKeeper && NetSession.Instance.Rules.UseWorldKeeper;

    /// <summary>
    ///     The keeper's weather is the world's: nobody else draws one any more
    /// </summary>
    [HarmonyPostfix]
    internal static void OnWeatherHour(Weather __instance)
    {
        if (NetSession.Instance.Transport is not ElinNetHost host || !NetSession.Instance.Rules.UseWorldKeeper) {
            return;
        }

        host.Delta.AddRemote(WeatherDelta.Create(__instance));
    }
}

/// <summary>
///     The keeper's day (luck, seed of the newspaper) is the world's
/// </summary>
[HarmonyPatch(typeof(World), nameof(World.CreateDayData))]
internal static class WorldKeeperDay
{
    [HarmonyPostfix]
    internal static void OnNewDay(World __instance)
    {
        if (NetSession.Instance.Transport is not ElinNetHost host || !NetSession.Instance.Rules.UseWorldKeeper ||
            __instance.dayData is null) {
            return;
        }

        host.Delta.AddRemote(DayDataDelta.Create(__instance.dayData));
    }
}

/// <summary>
///     The hooks of the world that only its keeper runs
/// </summary>
[HarmonyPatch]
internal static class WorldKeeperHooks
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(Weather), nameof(Weather.OnChangeHour));
        // expired quests: fame and karma
        yield return AccessTools.Method(typeof(QuestManager), nameof(QuestManager.OnAdvanceHour));
        yield return AccessTools.Method(typeof(Region), nameof(Region.CheckRandomSites));
        // the sites a night renews (LayerSleep: the Opatos pillow expires them all, waking on the world map draws
        // new ones): the world's sites are drawn by its keeper, a guest's would exist in its game alone
        yield return AccessTools.Method(typeof(Region), nameof(Region.RenewRandomSites));
        yield return AccessTools.Method(typeof(Region), nameof(Region.UpdateRandomSites));
        yield return AccessTools.Method(typeof(World), nameof(World.CreateDayData));
        yield return AccessTools.Method(typeof(Faction), nameof(Faction.OnAdvanceDay));
        // salary, tax bill, karma for unpaid bills
        yield return AccessTools.Method(typeof(Faction), nameof(Faction.OnAdvanceMonth));
        yield return AccessTools.Method(typeof(GameDate), nameof(GameDate.ShipLetter));
        yield return AccessTools.Method(typeof(GameDate), nameof(GameDate.ShipRandomPackages));
        // every parcel the date sends (gifts of the year, the magic chest, salaries): the list of parcels on
        // their way is the world's, a copy of it must neither grow nor be delivered a second time
        yield return AccessTools.Method(typeof(World), nameof(World.SendPackage));
        yield return AccessTools.Method(typeof(FactionBranch), nameof(FactionBranch.ReceivePackages));
    }

    [HarmonyPrefix]
    internal static bool OnWorldHook()
    {
        return !WorldKeeper.Skip;
    }
}

/// <summary>
///     User 2026-10-30 ("lots of tax bills and salaries spawned when a player claimed a deed"): the month's bill and
///     salary, and the world's parcels on their way, are the world's book, kept by the game that hosts it. A guest's
///     game holds a copy of that list (the world it was sent) and, once it has a base of its own on a map it holds
///     alone, would make and deliver its own: copies nobody pays, in its base's mail, sent to the host with the map.
///     Never in a guest's game, whatever the world keeper box says (that box is about the weather and the quests)
/// </summary>
[HarmonyPatch]
internal static class WorldParcels
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(Faction), nameof(Faction.OnAdvanceMonth));
        yield return AccessTools.Method(typeof(World), nameof(World.SendPackage));
        yield return AccessTools.Method(typeof(FactionBranch), nameof(FactionBranch.ReceivePackages));
    }

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    internal static bool OnWorldParcels()
    {
        return NetSession.Instance.Transport is not ElinNetClient;
    }
}
