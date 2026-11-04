using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using EModding.Helper;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     A step on the world map. Whoever steps pays it with its own body, the turns of hunger and conditions of
///     its character and of its companions, as in a solo game. The hours it costs follow the host's choice
///     (council 2026-10-23, <see cref="WorldMapTime" />): Shared Clock, every step of anyone moves the date;
///     Travel Together, only while every player is on the world map; Personal Pace, as the Shared Clock but the
///     hours of a player travelling while the others are elsewhere are its lead (ElinNetHost.HoldLead). A host's step and a guest's are the same:
///     the guest's game, which does not own the date on the host's map, has the host pass the hours
///     (WorldTravelStepDelta); on its own copy of the world map it passes them itself and reports its date
/// </summary>
[HarmonyPatch]
internal static class RemoteTravelRegionPatch
{
    // "your trip does not move the date" was said on this trip
    private static bool _told;

    /// <summary>
    ///     The turns of the step in progress are being played: those of this game's player and companions only
    /// </summary>
    internal static bool IsPayingStep { get; private set; }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(Chara), nameof(Chara._Move))]
    internal static IEnumerable<CodeInstruction> OnRegionTravelIl(IEnumerable<CodeInstruction> instructions)
    {
        var original = instructions.ToList();

        // a game that changed this function must not stop the patches after this one: all or nothing, with a warning
        var matcher = new CodeMatcher(original)
            .MatchEndForward(
                new OperandContains(OpCodes.Call, nameof(Chara.currentZone)),
                new OperandContains(OpCodes.Callvirt, nameof(Spatial.IsRegion)));
        if (matcher.IsInvalid) {
            return Unpatched(original, "Chara._Move currentZone.IsRegion");
        }

        matcher
            .Advance(1)
            .InsertAndAdvance(
                new CodeInstruction(OpCodes.Ldarg_0),
                Transpilers.EmitDelegate((bool isRegion, Chara chara) => {
                    if (!isRegion) {
                        _told = false;
                        return false;
                    }

                    // next to the one who simulates the map: its step is its own to pay too
                    return NetSession.Instance.IsHost || chara.IsPC;
                }))
            .MatchStartForward(
                new OperandContains(OpCodes.Callvirt, nameof(GameDate.AdvanceMin)));
        if (matcher.IsInvalid) {
            return Unpatched(original, "Chara._Move date.AdvanceMin");
        }

        return matcher
            .SetAndAdvance(OpCodes.Call, AccessTools.Method(typeof(RemoteTravelRegionPatch), nameof(StepDate)))
            .InstructionEnumeration();
    }

    internal static IEnumerable<CodeInstruction> Unpatched(List<CodeInstruction> original, string what)
    {
        EmpLog.Warning("Travel on the world map: {What} not found in the game's code, the function is left as the " +
                       "game wrote it (travel moves the date for everyone)", what);
        return original;
    }

    // the step ended, or threw
    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Chara), nameof(Chara._Move))]
    internal static void OnMoveEnd(Chara __instance)
    {
        if (__instance.IsPC) {
            IsPayingStep = false;

            // the turns counted during the step go out, one message per character
            CharaTickConditionDelta.FlushStep(NetSession.Instance.Connection);
        }
    }

    /// <summary>
    ///     In place of the date.AdvanceMin of the step, right before its turns are played
    /// </summary>
    internal static void StepDate(GameDate date, int minutes)
    {
        IsPayingStep = NetSession.Instance.Transport is not null;

        if (DateMoves()) {
            Travel(date, minutes, false);
        } else if (!PlayerFollow.CarriesTrip) {
            Tell();
        }
    }

    /// <summary>
    ///     The hours of this game's step or express hour pass: alone as the game does it; on the host, what the
    ///     walk of its player adds to the others walking with it (WorldTravelClock); on a player's own copy of the
    ///     world map on its own date, reported with its name; next to the host, the host passes them
    /// </summary>
    internal static void Travel(GameDate date, int minutes, bool express)
    {
        var session = NetSession.Instance;
        switch (session.Transport) {
            case null:
                if (express) {
                    date.AdvanceHour();
                } else {
                    date.AdvanceMin(minutes);
                }

                return;
            case ElinNetHost host:
                // Personal Pace: the part the world waits for is the host's lead, as anyone's
                var held = host.HoldLead(EClass.pc.uid, minutes);
                if (held > 0) {
                    WorldMapLead.Held(held, ElinNetHost.LeadOf(EClass.pc.uid));
                }

                Advance(date, WorldTravelClock.Step(WorldTravelClock.Host, minutes - held),
                    (session.Self?.User.Name ?? EClass.pc.NameSimple, EClass.pc.uid));
                return;
            case ElinNetClient main when session.IsZoneAuthority:
                // Personal Pace: the host keeps the leads, it says what part of this step the world waits for
                if (session.Rules.WorldMapTime == WorldMapTime.PersonalPace) {
                    main.SendWhileAway(new WorldTravelStepDelta { Minutes = minutes, Away = true });
                    return;
                }

                Advance(date, minutes, (EClass.pc.NameSimple, 0));
                return;
            case ElinNetClient client when !session.IsAway:
                client.Delta.AddRemote(new WorldTravelStepDelta { Minutes = minutes });
                return;
        }

        // visiting another player's map: its holder's date, not this game's
    }

    /// <summary>
    ///     Personal Pace, on this player's own copy of the world map: the part of its step past its lead, as the
    ///     host said (PlayerLeadDelta), passes here and is reported like any trip of the Shared Clock
    /// </summary>
    internal static void AdvanceAway(int minutes)
    {
        if (NetSession.Instance is { IsZoneAuthority: true, Transport: ElinNetClient } && EClass.core.IsGameStarted) {
            Advance(EClass.world.date, minutes, (EClass.pc.NameSimple, 0));
        }
    }

    private static void Advance(GameDate date, int minutes, (string Name, int Uid) traveller)
    {
        if (minutes <= 0) {
            return;
        }

        WorldDateAdvanceEvent.Traveller = traveller;
        try {
            date.AdvanceMin(minutes);
        } finally {
            WorldDateAdvanceEvent.Traveller = null;
        }
    }

    /// <summary>
    ///     Whether this game's travel (a step, an hour of the express travel) takes the date of the world along
    /// </summary>
    internal static bool DateMoves()
    {
        // following another player on the world map (GitHub issue 75): its steps pay the hours of the trip made
        // together, this player's own steps only its turns
        if (PlayerFollow.CarriesTrip) {
            return false;
        }

        var session = NetSession.Instance;
        if (session.Rules.WorldMapTime != WorldMapTime.TravelTogether) {
            return true;
        }

        // a guest's steps move the date as the host's do, when the host says everyone is on the world map (the
        // "_ => false" that kept a guest's steps from ever moving it is gone, council 2026-10-23)
        return session.Transport switch {
            null => true,
            ElinNetHost host => host.AllOnWorldMap(),
            _ => WorldMapTogetherDelta.AllOnWorldMap,
        };
    }

    /// <summary>
    ///     "Your friends are elsewhere", once per trip: the host knows it, a guest has it from the host
    /// </summary>
    internal static void Tell()
    {
        if (_told || NetSession.Instance.Transport is null) {
            return;
        }

        _told = true;
        Msg.Say("emp_ui_travel_no_date".lang());
    }
}

/// <summary>
///     The turns a step on the world map costs are played for the whole party, which is everyone's here: only
///     the player who steps and its companions live them. The companions of a player next to the host are
///     simulated by the host, which is asked for each turn
/// </summary>
[HarmonyPatch(typeof(Chara), nameof(Chara.TickConditions))]
internal static class TravelStepTurnsPatch
{
    // before CharaTickConditionEvent, which drops what this game does not simulate
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    internal static bool OnTickConditions(Chara __instance)
    {
        if (!RemoteTravelRegionPatch.IsPayingStep || __instance.IsPC) {
            return true;
        }

        if (!__instance.IsCompanionOf(EClass.pc)) {
            return false;
        }

        if (NetSession.Instance.Connection is not ElinNetClient client) {
            return true;
        }

        // counted with the step, sent when it ends. Harmony still runs CharaTickConditionEvent after this prefix
        // returned false, which skips its own message for a skipped original (__runOriginal)
        CharaTickConditionDelta.Emit(client, __instance);
        return false;
    }
}

/// <summary>
///     The express travel (LayerTravel) makes its hours pass by GameDate.AdvanceHour, one per image, from a
///     function the compiler generated: found by what it calls, nothing is patched when the game changes it. <br />
///     Same rule as a step: these hours pass for the world only when everyone travels. The traveller paid its
///     rations, the game asks nothing else of its body
/// </summary>
[HarmonyPatch]
internal static class TravelExpressPatch
{
    private static readonly MethodInfo AdvanceHour = AccessTools.Method(typeof(GameDate), nameof(GameDate.AdvanceHour));

    private static MethodBase? _target;

    private static MethodBase? FindTarget()
    {
        return typeof(LayerTravel)
            .GetNestedTypes(AccessTools.all)
            .SelectMany(type => AccessTools.GetDeclaredMethods(type))
            .FirstOrDefault(m => m.ReturnType == typeof(bool) && m.GetMethodBody() is not null &&
                                 PatchProcessor.ReadMethodBody(m).Any(il => Equals(il.Value, AdvanceHour)));
    }

    [HarmonyPrepare]
    private static bool Prepare()
    {
        _target ??= FindTarget();
        if (_target is null) {
            EmpLog.Warning("Express travel: no call to GameDate.AdvanceHour found in LayerTravel, " +
                           "its hours pass for everyone");
        }

        return _target is not null;
    }

    [HarmonyTargetMethod]
    private static MethodBase TargetMethod()
    {
        return _target ??= FindTarget()!;
    }

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> OnExpressHourIl(IEnumerable<CodeInstruction> instructions)
    {
        var original = instructions.ToList();

        var matcher = new CodeMatcher(original)
            .MatchStartForward(new CodeMatch(il => il.Calls(AdvanceHour)));
        if (matcher.IsInvalid) {
            return RemoteTravelRegionPatch.Unpatched(original, "LayerTravel date.AdvanceHour");
        }

        return matcher
            .SetAndAdvance(OpCodes.Call, AccessTools.Method(typeof(TravelExpressPatch), nameof(ExpressHour)))
            .InstructionEnumeration();
    }

    internal static void ExpressHour(GameDate date)
    {
        if (RemoteTravelRegionPatch.DateMoves()) {
            RemoteTravelRegionPatch.Travel(date, Date.HourToken, true);
        } else if (!PlayerFollow.CarriesTrip) {
            RemoteTravelRegionPatch.Tell();
        }
    }
}
