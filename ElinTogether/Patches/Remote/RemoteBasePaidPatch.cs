using System;
using System.Linq;
using ElinTogether.LangMod;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     Four things of the base are paid for and then made in this game's own copy of the base, which the host never
///     sees: the research board (knowledge), the hearth skills (platinum), the plans sold by a merchant (the base's
///     money) and the hearth upgrade (the player's gold) <br />
///     Research and hearth skills are requests to the host, see <see cref="BaseRequestDelta" />: the client keeps the
///     game's own steps up to the choice ("buy", the yes of the box) and sends the request in place of the payment, it
///     pays and changes nothing. The two dialog steps (merchant plans, hearth upgrade) say "only the host can do this
///     for now" and nothing is paid, nothing is made <br />
///     "Client" is <c>Connection is ElinNetClient</c>: a player on the host's map, and the host itself visiting a zone
///     simulated by a guest (its zone session is a client one, the guest's game is the one that keeps the zone). The
///     second one cannot ask: the game that would answer holds a copy of the zone, not the world's base, so it is
///     refused as before. Null while alone in an away zone (the game runs as single player there) and a host session
///     otherwise: the game runs as it always did <br />
///     The residents are in <see cref="RemoteResidentPatch" />
/// </summary>
[HarmonyPatch]
internal static class RemoteBasePaidPatch
{
    // ItemResearch.SetPlan asks CanCompletePlan to colour the price: the refusal is for the click only
    private static bool _listing;

    // the element whose hearth click is running: its yes/no box is the one to hook
    private static Element? _skill;

    private static bool IsClient => NetSession.Instance.Connection is ElinNetClient && !ElinDelta.IsApplying;

    // a player of the host's map, whose requests the host answers
    internal static bool IsRequester =>
        NetSession.Instance.Connection is ElinNetClient { IsZoneSession: false } && !ElinDelta.IsApplying;

    // the host in the zone of a guest
    internal static bool IsVisiting =>
        NetSession.Instance.Connection is ElinNetClient { IsZoneSession: true } && !ElinDelta.IsApplying;

    internal static void Refuse(bool beep)
    {
        if (beep) {
            SE.Beep();
        }

        EmpPop.Pop("emp_base_host_only".lang());
    }

    /// <summary>
    ///     A change that is told after it was made (policies, names, settings of an object, see
    ///     <see cref="PolicyStateDelta" />): with "only the host manages the base" it is said refused here, and the host
    ///     answers with the state it keeps, which puts this copy back
    /// </summary>
    internal static void WarnHostOnly()
    {
        if (IsRequester && NetSession.Instance.Rules.HostManagesBase) {
            Refuse(true);
        }
    }

    // this game is not the host of the world: a player on its map, the host in a guest's zone, and also a guest alone on
    // a map it keeps (no connection then, or a host session of the zone): its own copy is all it has there
    private static bool IsNotWorldHost =>
        (NetSession.Instance.Connection is ElinNetClient || NetSession.Instance.Transport is ElinNetClient) &&
        !ElinDelta.IsApplying;

    /// <summary>
    ///     Leaving the base for good (the hearth stone) and claiming land (a deed): a player who does not keep the world
    ///     did it on its own copy of the zone, a half-made base or a half-lost one that the host never sees, and a lost
    ///     base does not come back. Refused with the message wherever that player is; the host of the world does both as
    ///     before. The one inequality that stays
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ActPlan), nameof(ActPlan.TrySetAct), typeof(string), typeof(Func<bool>), typeof(Card),
        typeof(CursorInfo), typeof(int), typeof(bool), typeof(bool), typeof(bool))]
    private static void OnSetAct(string lang, ref Func<bool> onPerform)
    {
        if (lang == "actAbandonHome" && IsNotWorldHost) {
            onPerform = () => {
                Refuse(true);
                return false;
            };
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(TraitDeed), nameof(TraitDeed.OnRead))]
    private static bool OnReadDeed(TraitDeed __instance, Chara c, bool __runOriginal)
    {
        // a deed read on a world-map tile that holds a land is SiblingLands' (it asks the host itself)
        if (Helper.SiblingLands.ReadsOnLand(out _, out _)) {
            return true;
        }

        // on the host's map the game asks its question, and the yes is a request to the host (ClaimZonePatch)
        if (!IsNotWorldHost || (IsRequester && !NetSession.Instance.Rules.HostManagesBase)) {
            return true;
        }

        // away on the field this game holds alone: the same question, the yes asks the host (ClaimZoneDelta). (Not
        // when another prefix already answered, the cap of lands)
        if (IsLeaseHolder && !NetSession.Instance.Rules.HostManagesBase && c.IsPC) {
            if (__runOriginal) {
                AskHeldLand(__instance.owner);
            }

            return false;
        }

        Refuse(true);
        return false;
    }

    // a player away, on the map its own game holds (zone lease), alone or with visitors
    private static bool IsLeaseHolder =>
        NetSession.Instance is { IsZoneAuthority: true, Transport: ElinNetClient } session && EClass._zone is { } zone &&
        session.AwayZone == zone && !ElinDelta.IsApplying;

    // the deed read away, spent once the host made the land
    private static int _heldDeed;

    /// <summary>
    ///     The checks and the question of TraitDeed.OnRead; the yes sends a checkpoint (the host sees this game's bag
    ///     and map as they are now) and the request
    /// </summary>
    private static void AskHeldLand(Card deed)
    {
        var zone = EClass._zone;
        if (zone.mainFaction == EClass.pc.faction || !zone.isClaimable || zone.instance is not null) {
            Msg.Say("invalidClaimZone");
            return;
        }

        if (Helper.Teams.LandCapReached(EClass.pc)) {
            Msg.Say("emp_team_land_cap".Loc(EmpConfig.Server.LandsPerTeam.Value));
            return;
        }

        Dialog.YesNo("dialog_claimLand", () => {
            if (!IsLeaseHolder || NetSession.Instance.Transport is not ElinNetClient main) {
                return;
            }

            _heldDeed = deed.uid;
            main.SendTravelCheckpoint();
            main.SendWhileAway(new ClaimZoneDelta { ZoneUid = zone.uid });
        });
    }

    /// <summary>
    ///     The host made the land this game holds: its copy is claimed as TraitDeed.OnRead claims it (the hearth
    ///     stone, the deed spent), and a checkpoint brings the map and the base to the host
    /// </summary>
    internal static void ClaimHeldLand(int zoneUid)
    {
        var zone = EClass._zone;
        var deed = EClass.pc.things.List(t => t.uid == _heldDeed && t.trait is TraitDeed, true).FirstOrDefault() ??
                   EClass.pc.things.List(t => t.trait is TraitDeed, true).FirstOrDefault();
        _heldDeed = 0;
        if (NetSession.Instance is not { IsZoneAuthority: true, Transport: ElinNetClient main } session || zone is null ||
            session.AwayZone != zone || zone.uid != zoneUid || zone.mainFaction == EClass.pc.faction || deed is null) {
            // the host undoes the land it made (a claim is final only when this game claimed it too)
            EmpLog.Warning("The host made land {ZoneUid}, but this game no longer holds it or its deed: undone", zoneUid);
            if (NetSession.Instance.Transport is ElinNetClient client) {
                client.SendWhileAway(new ClaimZoneDelta { ZoneUid = zoneUid, Refused = true });
                if (NetSession.Instance is { IsZoneAuthority: true, AwayZone: not null }) {
                    client.SendTravelCheckpoint();
                }
            }

            return;
        }

        zone.ClaimZone();
        deed.ModNum(-1);
        WidgetMenuPanel.OnChangeMode();
        main.SendTravelCheckpoint();
    }

    /// <summary>
    ///     Research, the host in a guest's zone: the click of a plan asks CanCompletePlan before it opens the "buy"
    ///     menu. Said no here, the menu never opens (the game beeps by itself)
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ItemResearch), nameof(ItemResearch.SetPlan))]
    private static void OnListing()
    {
        _listing = true;
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(ItemResearch), nameof(ItemResearch.SetPlan))]
    private static void OnListed()
    {
        _listing = false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ResearchManager), nameof(ResearchManager.CanCompletePlan))]
    private static bool OnCanCompletePlan(ref bool __result)
    {
        if (_listing || !IsVisiting) {
            return true;
        }

        Refuse(false);
        __result = false;
        return false;
    }

    /// <summary>
    ///     Research, a player of the host's map: the click opens the "buy" menu as the game does (the price is coloured
    ///     by this copy of the knowledge). The menu is made right after <c>SetHighlightTarget(button1)</c> of the plan,
    ///     which tells the plan from the button: "buy" sends the request in place of paying and completing here
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UIContextMenu), nameof(UIContextMenu.AddButton), typeof(string), typeof(Action), typeof(bool))]
    private static void OnAddButton(UIContextMenu __instance, string idLang, ref Action action)
    {
        if (idLang != "buy" || !IsRequester || !__instance.highlightTarget ||
            __instance.highlightTarget.GetComponentInParent<ItemResearch>() is not { plan: { } plan }) {
            return;
        }

        var id = plan.id;
        action = () => BaseRequestDelta.Send(BaseRequestKind.Research, id);
    }

    /// <summary>
    ///     Research, the host: every plan it completes (its own click, or a request it served) tells the clients the new
    ///     state. What changed besides the plans (policies, feats) is told by comparing with what it was before
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ResearchManager), nameof(ResearchManager.CompletePlan))]
    private static void OnCompletePlan(ResearchManager __instance, out BaseStateDelta.Before? __state)
    {
        __state = NetSession.Instance.Connection is ElinNetHost { IsZoneSession: false } && __instance.branch is { } branch &&
                  branch == EClass._zone.branch
            ? new(branch)
            : null;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ResearchManager), nameof(ResearchManager.CompletePlan))]
    private static void OnPlanCompleted(ResearchManager __instance, BaseStateDelta.Before? __state)
    {
        if (__state is not null && NetSession.Instance.Connection is ElinNetHost host) {
            host.Delta.AddRemote(BaseStateDelta.ForResearch(__instance.branch, __state));
        }
    }

    /// <summary>
    ///     Plans of the merchant and hearth upgrade: both are steps of the dialog (the payment is in the step, the
    ///     choice only jumps to it). A client jumping to one is sent back to the step it came from, which is
    ///     shown again as for any other "back"
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(DramaSequence), nameof(DramaSequence.Play), typeof(string))]
    private static void OnPlayStep(DramaSequence __instance, ref string id)
    {
        if (id is not ("_buyPlan" or "_upgradeHearth") || !IsClient) {
            return;
        }

        Refuse(true);
        id = __instance.lastStep ?? "";
    }

    /// <summary>
    ///     Skills of the hearth: the click reads its callback from the list at each click, so it is wrapped there
    ///     (the click does the check of the price, the yes/no box, the payment and the new level). The host in a
    ///     guest's zone is refused; for the others the box is the game's own, see <see cref="OnYesNo" />
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(LayerHome), nameof(LayerHome.RefreshFeat))]
    private static void OnRefreshFeat(LayerHome __instance)
    {
        if (__instance.listFeat.callbacks is not UIList.Callback<Element, ButtonElement> { onClick: { } original } callbacks) {
            return;
        }

        callbacks.onClick = (element, button) => {
            if (IsVisiting) {
                Refuse(true);
                return;
            }

            _skill = element;
            try {
                original(element, button);
            } finally {
                _skill = null;
            }
        };
    }

    /// <summary>
    ///     The box of the click: for a player of the host's map its "yes" sends the request in place of the payment, for
    ///     the host it makes what the game makes and then tells the clients the new level
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Dialog), nameof(Dialog.YesNo))]
    private static void OnYesNo(ref Action actionYes)
    {
        if (_skill is not { } element) {
            return;
        }

        var id = element.id;
        if (IsRequester) {
            actionYes = () => BaseRequestDelta.Send(BaseRequestKind.HomeSkill, id.ToString());
            return;
        }

        var yes = actionYes;
        actionYes = () => {
            yes();
            if (NetSession.Instance.Connection is ElinNetHost { IsZoneSession: false } host && EClass._zone.branch is { } branch) {
                host.Delta.AddRemote(BaseStateDelta.ForSkill(branch, id));
            }
        };
    }
}
