using System.Collections;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

public enum BaseRequestKind : byte
{
    /// <summary>
    ///     A plan of the research board, paid in the knowledge of the base. Id is the plan's id
    /// </summary>
    Research,

    /// <summary>
    ///     A skill of the hearth, paid in the platinum of the player. Id is the element's id
    /// </summary>
    HomeSkill,

    /// <summary>
    ///     The maid of the base. Id is the resident's uid, 0 for none
    /// </summary>
    Maid,

    /// <summary>
    ///     A resident becomes livestock or a resident again. Id is its uid, Value the <see cref="FactionMemberType" />
    /// </summary>
    MemberType,

    /// <summary>
    ///     A resident goes to the reserve of the faction. Id is its uid
    /// </summary>
    Reserve,

    /// <summary>
    ///     Someone of the reserve is called back to the base. Id is its uid
    /// </summary>
    Recruit,

    /// <summary>
    ///     A resident is sent away: an ordinary one is destroyed with what it carries. Id is its uid
    /// </summary>
    Banish,

    /// <summary>
    ///     Someone of the reserve is sent away for good (the trash button of the reserve window). Id is its uid
    /// </summary>
    Discard,

    /// <summary>
    ///     The next visitor waiting to meet the base (the maid's "meeting"): it comes next to the player. Answered
    ///     with the visitor's uid in Value
    /// </summary>
    Meeting,

    /// <summary>
    ///     The talk with that visitor is over: it leaves. Id is its uid; answered with the visitors still waiting
    /// </summary>
    MeetingEnd,
}

public enum BaseAnswer : byte
{
    /// <summary>
    ///     A request, client to host
    /// </summary>
    None,

    Done,
    Refused,
}

/// <summary>
///     Something the base sells, or a gesture on its residents, asked of the host: the client's game would pay and then
///     make it in its own copy of the base only, which the host never sees. The client sends this instead and changes
///     nothing; the host checks the payment and the resident against its own state, pays and makes it once, and answers
///     the same type with <see cref="Answer" /> (Done or Refused) <br />
///     The new state of the base goes to everyone with <see cref="BaseStateDelta" />, see <see cref="RemoteBasePaidPatch" />
///     and <see cref="RemoteResidentPatch" />. With <see cref="NetSessionRules.HostManagesBase" /> nothing of this is made
/// </summary>
[MessagePackObject]
public class BaseRequestDelta : ElinDelta
{
    // a request lost on the way must not block the next one for ever
    private const float WaitSeconds = 5f;

    private static bool _waiting;
    private static float _sentAt;

    [Key(0)]
    public required BaseRequestKind Kind { get; init; }

    [Key(1)]
    public required string Id { get; init; }

    [Key(2)]
    public BaseAnswer Answer { get; init; }

    [Key(3)]
    public int Value { get; init; }

    // what the answer carries back (a meeting's visitor, the visitors left)
    private int _answer;

    /// <summary>
    ///     Client side: one request at a time, the next waits for the answer of the last
    /// </summary>
    internal static void Send(BaseRequestKind kind, string id, int value = 0)
    {
        if (NetSession.Instance.Connection is not ElinNetClient client) {
            return;
        }

        // the host keeps the base to itself: said here, before anything is asked
        if (NetSession.Instance.Rules.HostManagesBase) {
            RemoteBasePaidPatch.Refuse(true);
            return;
        }

        if (_waiting && UnityEngine.Time.realtimeSinceStartup - _sentAt < WaitSeconds) {
            SE.Beep();
            EmpPop.Pop("emp_base_pending".lang());
            return;
        }

        _waiting = true;
        _sentAt = UnityEngine.Time.realtimeSinceStartup;
        client.Delta.AddRemote(new BaseRequestDelta {
            Kind = kind,
            Id = id,
            Value = value,
        });
    }

    protected override void OnApply(ElinNetBase net)
    {
        if (Answer != BaseAnswer.None) {
            OnAnswer(net);
            return;
        }

        // not in a zone session: the zone's branch belongs to the world the host saves, not to the game holding the zone
        if (net is not ElinNetHost { IsZoneSession: false } host) {
            return;
        }

        // all of it is read again from this game's state, whatever the sender believed: a request that comes twice
        // or late finds the plan, the knowledge or the platinum as they are now, it cannot pay for nothing
        var done = !NetSession.Instance.Rules.HostManagesBase && host.ActiveRemoteCharas.TryGetValue(OriginPeer, out var sender) &&
                   !host.IsAwayPeer(OriginPeer) && Teams.CanTouch(sender, _zone) && TeamRanks.MayManageBase(sender, _zone) && _zone.branch is { } branch && Execute(host, branch, sender);

        host.SendDeltaTo(OriginPeer, new BaseRequestDelta {
            Kind = Kind,
            Id = Id,
            Answer = done ? BaseAnswer.Done : BaseAnswer.Refused,
            Value = _answer,
        });
    }

    private bool Execute(ElinNetHost host, FactionBranch branch, Chara sender)
    {
        // as the host's own gesture: a recipe learnt on the way must reach everyone (AddRecipeEvent skips what is applied)
        using var simulate = Simulate();

        if (Kind is BaseRequestKind.Meeting or BaseRequestKind.MeetingEnd) {
            return ExecuteMeeting(branch, sender);
        }

        if (Kind is not (BaseRequestKind.Research or BaseRequestKind.HomeSkill)) {
            return ExecuteResident(branch, sender);
        }

        if (Kind == BaseRequestKind.Research) {
            // what the click does: the knowledge goes by Mod, the plan by CompletePlan (recipes, policies, feats).
            // The new state goes out from there, see RemoteBasePaidPatch.OnCompletePlan
            if (branch.researches.plans.FirstOrDefault(p => p.id == Id) is not { } plan ||
                !branch.researches.CanCompletePlan(plan)) {
                return false;
            }

            branch.resources.knowledge.Mod(-plan.source.tech);
            branch.researches.CompletePlan(plan);
            BaseStateDelta.Refresh(branch, false);
            return true;
        }

        // the list of the hearth shows these, and its click refuses the rest (LayerHome.RefreshFeat)
        if (!int.TryParse(Id, out var id) || !branch.elements.dict.TryGetValue(id, out var element) ||
            element.source.category is "policy" or "landfeat" || element.HasTag("hidden") ||
            (element.Value <= 0 && element.vBase <= 0) || element.source.cost is not { Length: > 0 } ||
            element.source.cost[0] == 0 || element.ValueWithoutLink == 0) {
            return false;
        }

        var cost = branch.GetTechUpgradeCost(element);
        if (cost == 0 || cost > sender.GetCurrency("money2")) {
            return false;
        }

        // the platinum is in the bag this game keeps of the sender, the way it reaches the sender's own bag is the one
        // of any other change made here to a player's things
        sender.ModCurrency(-cost, "money2");
        branch.elements.ModBase(id, 1);
        BaseStateDelta.Refresh(branch, true);
        host.Delta.AddRemote(BaseStateDelta.ForSkill(branch, id));
        return true;
    }

    /// <summary>
    ///     What the menus and the dialog of a resident do, read again from this game's base: who it is, whether the
    ///     game would offer it. The game's own functions make it (the host tells the others from them, see
    ///     <see cref="RemoteResidentPatch" />), the maid is the one field they do not touch
    /// </summary>
    // the game hands the god artifacts of whoever is sent away to "the player", the host here: they go to the
    // companion's owner when it has one, else to the player who sends it away, before the game looks for them
    private static void HandArtifacts(Chara from, Chara sender)
    {
        // (an untagged resident's artifact goes to the player who sends it away, as before: given to the host it was
        // lost, the game destroys the resident with what it still carries — oct10_suite)
        var to = from.CompanionOwnerUid is var uid and not 0 ? game.cards.globalCharas.Find(uid) ?? sender : sender;
        if (to == pc) {
            return;
        }

        foreach (var artifact in from.things.List(a => a.HasTag(CTAG.godArtifact)).ToList()) {
            to.PickOrDrop(to.pos, artifact);
        }
    }

    /// <summary>
    ///     MeetingManager.Start / CallNext and Meeting.OnEndDrama, for that player: the visitor is made in this game,
    ///     next to the player (the player is not moved: its own game moves it), the talk opens in its game
    /// </summary>
    private bool ExecuteMeeting(FactionBranch branch, Chara sender)
    {
        var meetings = branch.meetings;
        if (Kind == BaseRequestKind.MeetingEnd) {
            if (!int.TryParse(Id, out var uid) || _map.charas.Find(c => c.uid == uid) is not { } visitor ||
                visitor.id is not ("merchant" or "farris") || visitor.IsPCFactionOrMinion) {
                return false;
            }

            if (visitor.IsGlobal) {
                visitor.MoveZone(game.spatials.Find("somewhere"));
            } else {
                visitor.Destroy();
            }

            _answer = meetings.list.Count;
            return true;
        }

        if (meetings.list.Count == 0 || meetings.SetRoom() == null) {
            return false;
        }

        var meeting = meetings.list[0];
        meetings.list.RemoveAt(0);
        meeting.SetChara(sender.pos.GetNearestPoint(allowChara: false));
        _answer = meeting.chara.uid;
        return true;
    }

    private bool ExecuteResident(FactionBranch branch, Chara sender)
    {
        if (!int.TryParse(Id, out var uid)) {
            return false;
        }

        var chara = branch.members.Find(m => m.uid == uid);
        if (Kind == BaseRequestKind.Maid) {
            if (uid != 0 && chara is not { IsPlayer: false }) {
                return false;
            }

            branch.uidMaid = uid;
            RemoteResidentPatch.TellMaid(branch);
            return true;
        }

        if (Kind is BaseRequestKind.Recruit or BaseRequestKind.Discard) {
            if (Home.listReserve.Find(h => h.chara?.uid == uid)?.chara is not { } reserved) {
                return false;
            }

            if (Kind == BaseRequestKind.Recruit) {
                branch.Recruit(reserved);
            } else if (reserved.trait.CanBeBanished) {
                // what the trash button does; OnBanish tells the others, see RemoteResidentPatch
                Home.RemoveReserve(reserved);
                HandArtifacts(reserved, sender);
                reserved.OnBanish();
            } else {
                return false;
            }

            BaseStateDelta.Refresh(branch, false);
            return true;
        }

        if (chara is not { IsPlayer: false }) {
            return false;
        }

        switch (Kind) {
            case BaseRequestKind.MemberType:
                // resident or livestock, not in a party; to livestock only when the delay of the last change is over
                var type = (FactionMemberType)Value;
                if (chara.IsPCParty || type is not (FactionMemberType.Default or FactionMemberType.Livestock) ||
                    chara.memberType is not (FactionMemberType.Default or FactionMemberType.Livestock) || chara.memberType == type ||
                    (type == FactionMemberType.Livestock && !world.date.IsExpired(chara.GetInt(36)))) {
                    return false;
                }

                if (chara.memberType == FactionMemberType.Livestock) {
                    chara.SetInt(36, world.date.GetRaw() + 14400);
                }

                branch.ChangeMemberType(chara, type);
                break;
            case BaseRequestKind.Reserve:
                // not the companion of a player
                if (chara.IsPCParty || Home.listReserve.Count >= Home.GetMaxReserve()) {
                    return false;
                }

                Home.AddReserve(chara);
                break;
            case BaseRequestKind.Banish:
                if (!chara.trait.CanBeBanished || chara.IsPCParty) {
                    return false;
                }

                HandArtifacts(chara, sender);
                branch.BanishMember(chara);
                break;
            default:
                return false;
        }

        BaseStateDelta.Refresh(branch, false);
        return true;
    }

    private void OnAnswer(ElinNetBase net)
    {
        if (net is not ElinNetClient { IsZoneSession: false }) {
            return;
        }

        _waiting = false;
        if (Answer == BaseAnswer.Refused) {
            SE.Beep();
            EmpPop.Pop("emp_base_refused".lang());
            return;
        }

        // the sound and the line the game makes at the end of the click, the state itself comes in BaseStateDelta
        if (Kind == BaseRequestKind.Meeting) {
            EmpMod.Instance.StartCoroutine(Meet(Value));
            return;
        }

        if (Kind == BaseRequestKind.MeetingEnd) {
            // the next one waiting, as CallNext goes on
            if (Value > 0) {
                Send(BaseRequestKind.Meeting, "");
            }

            return;
        }

        if (Kind == BaseRequestKind.Research) {
            if (sources.researches.map.TryGetValue(Id, out var row)) {
                WidgetPopText.Say("completePlan".lang(row.GetName()), FontColor.Great);
            }

            SE.Play("good");
        } else if (Kind == BaseRequestKind.HomeSkill) {
            SE.Pay();
        } else {
            SE.Click();
        }
    }

    /// <summary>
    ///     The maid's "meeting" in a guest's game: its own copy of the base would make the visitor for itself only
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(MeetingManager), nameof(MeetingManager.Start))]
    internal static class AskMeeting
    {
        [HarmonyLib.HarmonyPrefix]
        internal static bool OnStart()
        {
            if (!RemoteBasePaidPatch.IsRequester) {
                return true;
            }

            Send(BaseRequestKind.Meeting, "");
            return false;
        }
    }

    /// <summary>
    ///     Meeting.Start in the player's own game, once the visitor the host made is here
    /// </summary>
    private static IEnumerator Meet(int uid)
    {
        for (var wait = 0f; wait < 5f; wait += UnityEngine.Time.unscaledDeltaTime) {
            if (_map?.charas.Find(c => c.uid == uid) is { } visitor) {
                pc.LookAt(visitor);
                visitor.LookAt(pc);
                if (visitor.id == "farris") {
                    LayerDrama.ActivateMain("farris", "1-1", visitor);
                } else {
                    visitor.ShowDialog();
                }

                LayerDrama.Instance?.SetOnKill(() => Send(BaseRequestKind.MeetingEnd, uid.ToString()));
                yield break;
            }

            yield return null;
        }
    }
}
