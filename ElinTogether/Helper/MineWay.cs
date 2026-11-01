using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ElinTogether.Net;
using HarmonyLib;
using UnityEngine;

namespace ElinTogether.Helper;

/// <summary>
///     Vernis as a commons (host box "The start land and Vernis belong to everyone", user 2026-10-30): the way to its
///     mine stays open. Around each stairs down of the town map (the mine entrance, TraitMineEntrance, and any other
///     way down or the locked stairs before it opens), <see cref="Radius" /> cells: nobody builds, places, mines,
///     digs, shapes the terrain, nor takes or moves an installed thing there. Walking through is untouched. Refused in
///     every player's own game (the hit test of the tasks, the click) and on the host for a guest's deltas
///     (AgentTaskDelta, CharaBuildDelta, ZoneAddCardDelta, CardPlacedDelta, CardAddThingDelta, TerrainHeightDelta,
///     CellFlagDelta), so a modified client cannot block it either
/// </summary>
[HarmonyPatch]
internal static class MineWay
{
    internal const int Radius = 5;

    // the entrances of the map last read, read again when the map changes or after a few seconds
    private static Map? _map;
    private static List<Point> _entrances = [];
    private static float _readAt;
    private const float ReadEvery = 5f;

    /// <summary>
    ///     That zone is Vernis's town and a commons (the mine below is a zone of its own)
    /// </summary>
    internal static bool IsVernis(Spatial? zone)
    {
        return zone is Zone_Vernis { lv: 0 } && Commons.Is(zone);
    }

    private static List<Point> Entrances()
    {
        var map = EClass._map;
        if (map is null || !IsVernis(EClass._zone)) {
            return [];
        }

        var now = Time.unscaledTime;
        if (map != _map || now - _readAt > ReadEvery) {
            _map = map;
            _readAt = now;
            _entrances = map.things
                .Where(t => t.trait is TraitNewZone { IsDownstairs: true } or TraitStairsLocked && t.pos is { IsValid: true })
                .Select(t => new Point(t.pos))
                .ToList();
        }

        return _entrances;
    }

    /// <summary>
    ///     That cell (or a thing of that extent standing on it) is on the way to the mine
    /// </summary>
    internal static bool Protects(int x, int z, int extent = 0)
    {
        var entrances = Entrances();
        return entrances.Count > 0 && entrances.Any(e => e.Distance(x, z) <= Radius + extent);
    }

    internal static bool Protects(Point? p, int extent = 0)
    {
        return p is { IsValid: true } && Protects(p.x, p.z, extent);
    }

    /// <summary>
    ///     An installed thing of this map on the way to the mine: nobody takes or moves it
    /// </summary>
    internal static bool ProtectsCard(Card? card)
    {
        return card is Thing { IsInstalled: true } thing && thing.parent == EClass._zone && Protects(thing.pos, Extent(thing));
    }

    /// <summary>
    ///     The town's own furniture of Vernis not claimed (installed, a resident's): nobody takes it, a guest's delta
    ///     included
    /// </summary>
    internal static bool IsTownFurniture(Card? card)
    {
        return card is Thing { IsInstalled: true, isNPCProperty: true } thing && thing.parent is Zone zone && Commons.IsOpenTown(zone);
    }

    internal static int Extent(Card card)
    {
        return Mathf.Max(card.W, card.H) - 1;
    }

    internal static int Extent(Recipe? recipe)
    {
        return recipe is null ? 0 : Mathf.Max(recipe.W, recipe.H) - 1;
    }

    internal static void Say()
    {
        SE.Beep();
        Msg.Say("emp_mine_way".lang());
    }

    /// <summary>
    ///     The click itself says why (the terrain brush counts its radius)
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(BaseTileSelector), nameof(BaseTileSelector.TryProcessTiles))]
    private static bool OnProcessTiles(BaseTileSelector __instance, Point _end)
    {
        if (!Commons.On || EInput.skipFrame > 0) {
            return true;
        }

        var mode = __instance.mode;
        var extent = mode switch {
            AM_Terrain terrain => terrain.brushRadius,
            AM_Build build => Extent(build.recipe),
            AM_MoveInstalled { target: { } moved } => Extent(moved),
            AM_Mine or AM_Dig or AM_FlagCell or AM_MoveInstalled or AM_Deconstruct => 0,
            _ => -1,
        };
        if (extent < 0 || !Protects(_end, extent)) {
            return true;
        }

        // the terrain brush clicks again every frame while the button is held: said once per press
        if (!EInput.leftMouse.pressing || EInput.leftMouse.down) {
            Say();
        }

        return false;
    }

    /// <summary>
    ///     Picking a thing up to move it: not one on the way to the mine. In Vernis not claimed (the town's own), not
    ///     a resident nor the town's furniture either: its shops and people stay where they are
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(AM_MoveInstalled), nameof(AM_MoveInstalled.SetTarget))]
    private static bool OnPickToMove(Card _target)
    {
        if (!Commons.On || _target is null) {
            return true;
        }

        if (ProtectsCard(_target)) {
            Say();
            return false;
        }

        if (IsVernis(EClass._zone) && !EClass._zone.IsPCFaction && (_target.isChara || _target.isNPCProperty)) {
            SE.Beep();
            Msg.Say("invalidAction");
            return false;
        }

        return true;
    }

    /// <summary>
    ///     The deconstruct tool of build mode (Vernis not claimed opens it to everyone): the game puts away any thing
    ///     it can, a resident's included. Here not the town's things (isNPCProperty) while the town is its own, nor an
    ///     installed thing on the way to the mine; the rest as the game does (AM_Deconstruct.Perform)
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(AM_Deconstruct), nameof(AM_Deconstruct.Perform))]
    private static bool OnDeconstruct(AM_Deconstruct __instance, Point point, bool perform, ref int __result)
    {
        if (!Commons.On || !IsVernis(EClass._zone)) {
            return true;
        }

        var openTown = Commons.IsOpenTown(EClass._zone);
        var list = point.ListCards();
        list.Reverse();
        var num = 0;
        foreach (var item in list) {
            if ((!EClass.debug.ignoreBuildRule && (!item.isThing || !item.trait.CanPutAway)) ||
                (__instance.ignoreInstalled && item.IsInstalled) || item.IsPCParty ||
                (openTown && item.isNPCProperty) || ProtectsCard(item)) {
                continue;
            }

            if (perform) {
                item.PlaySound(item.material.GetSoundDead(item.sourceCard));
                if (item.isThing) {
                    EClass._map.PutAway(item.Thing);
                } else {
                    item.Destroy();
                }

                BuildMenu.dirtyCat = true;
            }

            num++;
        }

        __result = num;
        return false;
    }
}

/// <summary>
///     Build mode, held furniture, mining and digging, moving a thing: not on the way to Vernis's mine (the tile shows
///     red), see <see cref="MineWay" />. Cutting stays: it only clears (Vernis's story asks to clear its rubble)
/// </summary>
[HarmonyPatch]
internal static class MineWayHitPatch
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        return [
            AccessTools.Method(typeof(TaskBuild), nameof(TaskBuild.GetHitResult)),
            AccessTools.Method(typeof(TaskMine), nameof(TaskMine.GetHitResult)),
            AccessTools.Method(typeof(TaskDig), nameof(TaskDig.GetHitResult)),
            AccessTools.Method(typeof(TaskMoveInstalled), nameof(TaskMoveInstalled.GetHitResult)),
        ];
    }

    [HarmonyPostfix]
    internal static void OnHitResult(TaskDesignation __instance, ref HitResult __result)
    {
        if (__result == HitResult.Invalid || !Commons.On) {
            return;
        }

        var extent = __instance switch {
            TaskBuild build => MineWay.Extent(build.recipe),
            TaskMoveInstalled { target: { } moved } => MineWay.Extent(moved),
            _ => 0,
        };
        if (MineWay.Protects(__instance.pos, extent) || (__instance is TaskMoveInstalled move && MineWay.ProtectsCard(move.target))) {
            __result = HitResult.Invalid;
        }
    }
}
