using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     The settings of an object of the map are told to the others where they change, not where they are clicked
///     (menus of lambdas), see <see cref="CardSettingDelta" />
/// </summary>
[HarmonyPatch]
internal static class CardSettingEvent
{
    internal static void Tell(Card? card, byte kind)
    {
        if (card is null || ElinDelta.IsApplying || NetSession.Instance.Connection is not { } connection ||
            !CardCache.Contains(card)) {
            return;
        }

        if (card.GetRootCard() is not Chara && kind is not (CardSettingDelta.Door or CardSettingDelta.Well or CardSettingDelta.Hidden)) {
            RemoteBasePaidPatch.WarnHostOnly();
        }

        connection.Delta.AddRemote(CardSettingDelta.Create(card, kind));
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Card), nameof(Card.c_note), MethodType.Setter)]
    internal static void OnNote(Card __instance)
    {
        Tell(__instance, CardSettingDelta.Note);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Card), nameof(Card.c_refText), MethodType.Setter)]
    internal static void OnName(Card __instance)
    {
        Tell(__instance, CardSettingDelta.Name);
    }

    /// <summary>
    ///     A canvas painted (pixel paint, paint brush, camera)
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Card), nameof(Card.c_textureData), MethodType.Setter)]
    internal static void OnPainted(Card __instance, byte[]? value)
    {
        if (value is not null) {
            Tell(__instance, CardSettingDelta.Painting);
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Card), nameof(Card.c_altName), MethodType.Setter)]
    internal static void OnRename(Card __instance, out string? __state)
    {
        __state = __instance.c_altName;
    }

    /// <summary>
    ///     A resident or an ally renamed (the game also names a dish this way: an object, left out)
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Card), nameof(Card.c_altName), MethodType.Setter)]
    internal static void OnRenamed(Card __instance, string? __state)
    {
        if (__instance is Chara && __instance.c_altName != __state) {
            Tell(__instance, CardSettingDelta.AltName);
        }
    }

    /// <summary>
    ///     Freed from a shackle by hand (not the night's own shielding, which writes the flag itself)
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Card), nameof(Card.TryUnrestrain))]
    internal static void OnUnrestrain(Card __instance, out bool __state)
    {
        __state = __instance.isRestrained;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Card), nameof(Card.TryUnrestrain))]
    internal static void OnUnrestrained(Card __instance, bool __state)
    {
        if (__instance is Chara && __state && !__instance.isRestrained) {
            Tell(__instance, CardSettingDelta.Restrain);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(TraitShackle), nameof(TraitShackle.Restrain))]
    internal static void OnRestrained(Card tc)
    {
        if (tc is Chara) {
            Tell(tc, CardSettingDelta.Restrain);
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Card), nameof(Card.c_idPortrait), MethodType.Setter)]
    internal static void OnPortrait(Card __instance, out string? __state)
    {
        __state = __instance.c_idPortrait;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Card), nameof(Card.c_idPortrait), MethodType.Setter)]
    internal static void OnPortraitSet(Card __instance, string? __state)
    {
        if (__instance is Chara && __instance.c_idPortrait != __state) {
            Tell(__instance, CardSettingDelta.Look);
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Card), nameof(Card.c_idSpriteReplacer), MethodType.Setter)]
    internal static void OnSkin(Card __instance, out string? __state)
    {
        __state = __instance.c_idSpriteReplacer;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Card), nameof(Card.c_idSpriteReplacer), MethodType.Setter)]
    internal static void OnSkinSet(Card __instance, string? __state)
    {
        if (__instance is Chara && __instance.c_idSpriteReplacer != __state) {
            Tell(__instance, CardSettingDelta.Look);
        }
    }

    /// <summary>
    ///     The look a dialog gives (the sister's "change"; hunt 2 row 31): only the game of the player talking changed it
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(DramaOutcome), nameof(DramaOutcome.sister_change))]
    internal static void OnSisterChange(DramaOutcome __instance)
    {
        Tell(__instance.cc, CardSettingDelta.CharaSkin);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Card), nameof(Card.c_idRidePCC), MethodType.Setter)]
    internal static void OnRideSkin(Card __instance, out string? __state)
    {
        __state = __instance.c_idRidePCC;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Card), nameof(Card.c_idRidePCC), MethodType.Setter)]
    internal static void OnRideSkinSet(Card __instance, string? __state)
    {
        if (__instance is Chara && __instance.c_idRidePCC != __state) {
            Tell(__instance, CardSettingDelta.RideSkin);
        }
    }

    /// <summary>
    ///     The color of a light (shift menu of the tile, with the illumination license)
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Card), nameof(Card.c_lightColor), MethodType.Setter)]
    internal static void OnLightColor(Card __instance, out int __state)
    {
        __state = __instance.c_lightColor;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Card), nameof(Card.c_lightColor), MethodType.Setter)]
    internal static void OnLightColored(Card __instance, int __state)
    {
        if (__instance is Thing && __instance.c_lightColor != __state) {
            Tell(__instance, CardSettingDelta.LightColor);
        }
    }

    /// <summary>
    ///     The two switches of a crafting station's window: refuel by itself, do not take from the stock
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Card), nameof(Card.autoRefuel), MethodType.Setter)]
    internal static void OnAutoRefuel(Card __instance, out bool __state)
    {
        __state = __instance.autoRefuel;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Card), nameof(Card.autoRefuel), MethodType.Setter)]
    internal static void OnAutoRefuelSet(Card __instance, bool __state)
    {
        if (__instance is Thing && __instance.autoRefuel != __state) {
            Tell(__instance, CardSettingDelta.AutoRefuel);
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Card), nameof(Card.c_isDisableStockUse), MethodType.Setter)]
    internal static void OnStockUse(Card __instance, out bool __state)
    {
        __state = __instance.c_isDisableStockUse;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Card), nameof(Card.c_isDisableStockUse), MethodType.Setter)]
    internal static void OnStockUseSet(Card __instance, bool __state)
    {
        if (__instance is Thing && __instance.c_isDisableStockUse != __state) {
            Tell(__instance, CardSettingDelta.StockUse);
        }
    }

    /// <summary>
    ///     A hidden medal or trap found by a player (search, waiting): the other games reveal it too
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Card), nameof(Card.SetHidden))]
    internal static void OnHidden(Card __instance, out bool __state)
    {
        __state = __instance.isHidden;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Card), nameof(Card.SetHidden))]
    internal static void OnHiddenSet(Card __instance, bool __state)
    {
        if (__instance is Thing && __instance.isHidden != __state) {
            Tell(__instance, CardSettingDelta.Hidden);
        }
    }

    /// <summary>
    ///     The "important" mark of an item (never sold, thrown, used as an ingredient): the host's code on a guest's bag
    ///     honours it
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Card), nameof(Card.c_isImportant), MethodType.Setter)]
    internal static void OnImportant(Card __instance, out bool __state)
    {
        __state = __instance.c_isImportant;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Card), nameof(Card.c_isImportant), MethodType.Setter)]
    internal static void OnImportantSet(Card __instance, bool __state)
    {
        if (__instance is Thing && __instance.c_isImportant != __state) {
            Tell(__instance, CardSettingDelta.Important);
        }
    }

    /// <summary>
    ///     A generator that shorts out or recovers (TraitGenerator.ModHP, from the simulation of the host): its hp goes
    ///     with it, the others do not simulate it
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Card), nameof(Card.isBroken), MethodType.Setter)]
    internal static void OnBroken(Card __instance)
    {
        if (__instance.trait is TraitGenerator && NetSession.Instance.Connection is ElinNetHost) {
            Tell(__instance, CardSettingDelta.Generator);
        }
    }

    /// <summary>
    ///     "Turn on/off automatically" of a light (shift menu of the tile)
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Card), nameof(Card.disableAutoToggle), MethodType.Setter)]
    internal static void OnAutoToggle(Card __instance, out bool __state)
    {
        __state = __instance.disableAutoToggle;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Card), nameof(Card.disableAutoToggle), MethodType.Setter)]
    internal static void OnAutoToggled(Card __instance, bool __state)
    {
        if (__instance.disableAutoToggle != __state) {
            Tell(__instance, CardSettingDelta.AutoToggle);
        }
    }

    /// <summary>
    ///     A lock picked or forced open (the chest stayed locked in the other games)
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Trait), nameof(Trait.OnLockOpen))]
    internal static void OnLockOpen(Trait __instance)
    {
        Tell(__instance.owner, CardSettingDelta.Lock);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Card), nameof(Card.isMasked), MethodType.Setter)]
    internal static void OnMask(Card __instance, out bool __state)
    {
        __state = __instance.isMasked;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Card), nameof(Card.isMasked), MethodType.Setter)]
    internal static void OnMasked(Card __instance, bool __state)
    {
        if (__instance.isMasked != __state) {
            Tell(__instance, CardSettingDelta.Masked);
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Card), nameof(Card.SetSale))]
    internal static void OnSale(Card __instance, out bool __state)
    {
        __state = __instance.isSale;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Card), nameof(Card.SetSale))]
    internal static void OnSaleSet(Card __instance, bool __state)
    {
        if (__instance.isSale != __state) {
            // (a guest's sale is marked after this, when the host applies it; the host's own is the host's, council
            // 2026-11-25)
            __instance.SetInt(Helper.BaseIncome.SellerKey, __instance.isSale && !Models.ElinDelta.IsApplying &&
                NetSession.Instance.Connection is ElinNetHost { IsZoneSession: false } ? EClass.pc.uid : 0);
            Tell(__instance, CardSettingDelta.Sale);
        }
    }

}

/// <summary>
///     The plate of a room: name, access, group, height, atrium, set by the lambdas of the room's menu
///     (BaseArea.ListInteractions), which write the plate's data. Told after each of them: the whole state
/// </summary>
[HarmonyPatch]
internal static class RoomPlateSettingEvent
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        return typeof(BaseArea).GetNestedTypes(AccessTools.all).SelectMany(AccessTools.GetDeclaredMethods)
            .Concat(AccessTools.GetDeclaredMethods(typeof(BaseArea)))
            .Where(m => m.Name.Contains("<ListInteractions>"));
    }

    [HarmonyPrefix]
    internal static void OnRoomMenuStart(object __instance, out string? __state)
    {
        __state = StateOf(AreaOf(__instance, 2)?.plate);
    }

    // (opening a list or the access menu changes nothing: only what changed the plate is told)
    [HarmonyPostfix]
    internal static void OnRoomMenu(object __instance, string? __state)
    {
        var plate = AreaOf(__instance, 2)?.plate;
        if (plate is not null && StateOf(plate) != __state) {
            CardSettingEvent.Tell(plate.owner, CardSettingDelta.RoomPlate);
        }
    }

    private static string? StateOf(TraitRoomPlate? plate)
    {
        return plate is null ? null : string.Join("|", CardSettingDelta.Create(plate.owner, CardSettingDelta.RoomPlate).Numbers!) + "|" + plate.areaData.name;
    }

    // the area, held by the closure or by the closure it was made in
    private static BaseArea? AreaOf(object? closure, int depth)
    {
        if (closure is BaseArea area) {
            return area;
        }

        if (closure is null || depth == 0) {
            return null;
        }

        return AccessTools.GetDeclaredFields(closure.GetType()).Where(f => !f.FieldType.IsValueType)
            .Select(f => AreaOf(f.GetValue(closure), depth - 1)).FirstOrDefault(a => a is not null);
    }
}

/// <summary>
///     The model a figure shows (its slider, a lambda of TraitFigure.TrySetAct)
/// </summary>
[HarmonyPatch]
internal static class FigureSettingEvent
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        return typeof(TraitFigure).GetNestedTypes(AccessTools.all).SelectMany(AccessTools.GetDeclaredMethods)
            .Concat(AccessTools.GetDeclaredMethods(typeof(TraitFigure)))
            .Where(m => m.Name.Contains("<TrySetAct>") && m.ReturnType == typeof(void) && m.GetParameters() is [{ ParameterType: var t }] && t == typeof(float));
    }

    [HarmonyPostfix]
    internal static void OnFigure(object __instance)
    {
        var figure = __instance as TraitFigure ?? AccessTools.GetDeclaredFields(__instance.GetType())
            .Select(f => f.GetValue(__instance)).OfType<TraitFigure>().FirstOrDefault();
        CardSettingEvent.Tell(figure?.owner, CardSettingDelta.Figure);
    }
}

/// <summary>
///     A well drunk from (or filled): its charges are written directly, no ModCharge the others would hear of. Drinking
///     is not managing the base: allowed to everyone, as a door
/// </summary>
[HarmonyPatch(typeof(TraitWell), nameof(TraitWell.ModCharges))]
internal static class WellSettingEvent
{
    [HarmonyPostfix]
    internal static void OnWell(TraitWell __instance)
    {
        CardSettingEvent.Tell(__instance.owner, CardSettingDelta.Well);
    }
}

/// <summary>
///     The house board's roof, walls and music: every change of its menu ends in ApplyData
/// </summary>
[HarmonyPatch(typeof(TraitHouseBoard), nameof(TraitHouseBoard.ApplyData))]
internal static class HouseBoardSettingEvent
{
    [HarmonyPostfix]
    internal static void OnApplyData(TraitHouseBoard __instance)
    {
        CardSettingEvent.Tell(__instance.owner, CardSettingDelta.HouseBoard);
    }
}

[HarmonyPatch(typeof(TeleportManager), nameof(TeleportManager.SetID))]
internal static class TeleporterSettingEvent
{
    [HarmonyPostfix]
    internal static void OnSetId(TraitTeleporter t)
    {
        CardSettingEvent.Tell(t.owner, CardSettingDelta.Teleporter);
    }
}

[HarmonyPatch]
internal static class BedSettingEvent
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        return [
            AccessTools.Method(typeof(TraitBed), nameof(TraitBed.AddHolder)),
            AccessTools.Method(typeof(TraitBed), nameof(TraitBed.RemoveHolder)),
            AccessTools.Method(typeof(TraitBed), nameof(TraitBed.ClearHolders)),
            AccessTools.Method(typeof(TraitBed), nameof(TraitBed.SetBedType)),
        ];
    }

    [HarmonyPostfix]
    internal static void OnBed(TraitBed __instance)
    {
        CardSettingEvent.Tell(__instance.owner, CardSettingDelta.Bed);
    }
}

/// <summary>
///     A door opened or closed by hand (the "open", "close" of its menu). A walker opens it in every game already, and
///     each game shuts its doors after a while by itself: told, a town's doors would never stop talking
/// </summary>
[HarmonyPatch]
internal static class DoorSettingEvent
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        return AccessTools.GetDeclaredMethods(typeof(TraitDoor))
            .Concat(typeof(TraitDoor).GetNestedTypes(AccessTools.all).SelectMany(AccessTools.GetDeclaredMethods))
            .Where(m => m.Name.Contains("<TrySetAct>"));
    }

    [HarmonyPostfix]
    internal static void OnDoorByHand(object __instance)
    {
        var door = __instance as TraitDoor ?? AccessTools.GetDeclaredFields(__instance.GetType())
            .Select(f => f.GetValue(__instance)).OfType<TraitDoor>().FirstOrDefault();
        CardSettingEvent.Tell(door?.owner, CardSettingDelta.Door);
    }
}

