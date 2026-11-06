using System.Collections.Generic;
using System.Linq;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     What changed in the base (research board, hearth skills, what a research gives), host to clients: nothing of the
///     base comes back to a client after it arrived, its copy only changed with its own clicks. Each part is sent only
///     when it changed, the client sets what it holds to the host's values <br />
///     Sent by the host when a research is completed or a skill is raised, see <see cref="RemoteBasePaidPatch" /> and
///     <see cref="BaseRequestDelta" />, and when a resident is changed, see <see cref="RemoteResidentPatch" />. A recipe
///     a research gives goes by <see cref="AddRecipeDelta" /> as any other
/// </summary>
[MessagePackObject]
public class BaseStateDelta : ElinDelta
{
    /// <summary>
    ///     The base this is about: the zone that owns it
    /// </summary>
    [Key(0)]
    public required int ZoneUid { get; init; }

    /// <summary>
    ///     The whole research manager (plans, finished, new, focused)
    /// </summary>
    [Key(1)]
    public LZ4Bytes? Researches { get; init; }

    [Key(2)]
    public LZ4Bytes? Policies { get; init; }

    [Key(3)]
    public int? Knowledge { get; init; }

    /// <summary>
    ///     Base value of the elements of the base that changed
    /// </summary>
    [Key(4)]
    public Dictionary<int, int>? Elements { get; init; }

    /// <summary>
    ///     A gesture on a resident that the host made (maid, member type, reserve, recall, banish): each game makes it
    ///     on its own copy with the game's own function. The residents are not a state a client can be given whole
    ///     (the reserve and the departed hold characters)
    /// </summary>
    [Key(5)]
    public BaseRequestKind? Resident { get; init; }

    /// <summary>
    ///     The resident's uid; the new maid, 0 for none
    /// </summary>
    [Key(6)]
    public int Target { get; init; }

    /// <summary>
    ///     The new <see cref="FactionMemberType" />; 1 for a banish that makes no message of its own
    /// </summary>
    [Key(7)]
    public int Value { get; init; }

    /// <summary>
    ///     What a research may change, taken before it is completed to tell what did
    /// </summary>
    internal sealed class Before(FactionBranch branch)
    {
        internal readonly Dictionary<int, int> Elements = branch.elements.dict.ToDictionary(p => p.Key, p => p.Value.vBase);
        internal readonly int Policies = branch.policies.list.Count;
    }

    internal static BaseStateDelta ForResearch(FactionBranch branch, Before before)
    {
        return new() {
            ZoneUid = branch.owner.uid,
            Researches = LZ4Bytes.Create(branch.researches),
            Policies = branch.policies.list.Count == before.Policies ? null : LZ4Bytes.Create(branch.policies),
            Knowledge = branch.resources.knowledge.value,
            Elements = Changed(branch, before.Elements),
        };
    }

    internal static BaseStateDelta ForSkill(FactionBranch branch, int id)
    {
        return new() {
            ZoneUid = branch.owner.uid,
            Elements = new() { [id] = branch.elements.dict.TryGetValue(id, out var element) ? element.vBase : 0 },
        };
    }

    internal static BaseStateDelta ForResident(FactionBranch branch, BaseRequestKind kind, int uid, int value = 0)
    {
        return new() {
            ZoneUid = branch.owner.uid,
            Resident = kind,
            Target = uid,
            Value = value,
        };
    }

    private static Dictionary<int, int>? Changed(FactionBranch branch, Dictionary<int, int> before)
    {
        var changed = branch.elements.dict
            .Where(p => !before.TryGetValue(p.Key, out var value) || value != p.Value.vBase)
            .ToDictionary(p => p.Key, p => p.Value.vBase);
        return changed.Count == 0 ? null : changed;
    }

    /// <summary>
    ///     What the game does after the purchase: the open windows show the new state, a new skill may add what the
    ///     hotbar and the menu offer
    /// </summary>
    internal static void Refresh(FactionBranch branch, bool elements)
    {
        branch.resources.SetDirty();

        if (ui.GetLayer<LayerTech>() is { } tech) {
            tech.RefreshTech();
        }

        if (ui.GetLayer<LayerHome>() is { } home && home.listFeat.callbacks != null) {
            home.listFeat.List();
        }

        // the lists of residents and of the reserve
        if (ui.GetLayer<LayerPeople>() is { multi: not null } people) {
            // the tabs of a single window share one list: only the tab shown is listed again (LayerPeople.OnSwitchContent)
            var owners = people.multi.owners;
            for (var i = 0; i < owners.Count; i++) {
                owners[i].RefreshTab();
                if (people.multi.Double || i == people.windows[0].idTab) {
                    owners[i].List();
                }
            }
        }

        if (!elements) {
            return;
        }

        player.hotbars.ResetHotbar(2);
        if (WidgetHotbar.HotBarMainMenu) {
            WidgetHotbar.HotBarMainMenu.RebuildPage();
        }

        if (WidgetMenuPanel.Instance) {
            WidgetMenuPanel.Instance.OnChangeActionMode();
        }
    }

    protected override void OnApply(ElinNetBase net)
    {
        // host to clients only, and for a base this game knows
        if (net is not ElinNetClient { IsZoneSession: false } || game.spatials.Find(ZoneUid)?.branch is not { } branch) {
            return;
        }

        if (Researches is not null) {
            var researches = Researches.Decompress<ResearchManager>();
            branch.researches = researches;
            researches.SetOwner(branch);
        }

        if (Policies is not null) {
            var policies = Policies.Decompress<PolicyManager>();
            branch.policies = policies;
            policies.SetOwner(branch);
        }

        // the field, not Mod: Mod would tell the host it was the client that spent it (HomeResourceModEvent)
        if (Knowledge is { } knowledge) {
            branch.resources.knowledge.value = knowledge;
        }

        foreach (var (id, value) in Elements ?? new Dictionary<int, int>()) {
            var current = branch.elements.dict.TryGetValue(id, out var element) ? element.vBase : 0;
            if (current != value) {
                branch.elements.ModBase(id, value - current);
            }
        }

        if (Resident is { } kind) {
            ApplyResident(branch, kind);
        }

        Refresh(branch, Elements is { Count: > 0 });
    }

    /// <summary>
    ///     What the game does at each gesture, on this game's copy. A character that is not here (this game is elsewhere,
    ///     or it never had it) is left as it is
    /// </summary>
    private void ApplyResident(FactionBranch branch, BaseRequestKind kind)
    {
        if (kind == BaseRequestKind.Maid) {
            branch.uidMaid = Target;
            return;
        }

        // sent away from the reserve: the character is only there, not among the residents
        if (kind == BaseRequestKind.Discard) {
            if (Home.listReserve.Find(h => h.chara?.uid == Target)?.chara is { } discarded) {
                Home.RemoveReserve(discarded);
            }

            return;
        }

        // a recruit arrives on the map as a card of the host (sent before this), the others are residents of the base
        var here = _zone == branch.owner;
        var chara = kind == BaseRequestKind.Recruit
            ? here ? _map.charas.Find(c => c.uid == Target) : null
            : branch.members.Find(m => m.uid == Target);
        if (chara is null) {
            return;
        }

        switch (kind) {
            case BaseRequestKind.MemberType:
                var type = (FactionMemberType)Value;
                if (chara.memberType == FactionMemberType.Livestock && type == FactionMemberType.Default) {
                    chara.SetInt(36, world.date.GetRaw() + 14400);
                }

                branch.ChangeMemberType(chara, type);
                break;
            case BaseRequestKind.Reserve when here:
                Home.AddReserve(chara);
                break;
            case BaseRequestKind.Recruit:
                // the copy of a recruit the host's game made: its achievement went to whoever recruited (the host's
                // account, or the guest it sent it to), never to every game that copies it; the recruit is no player
                using (Helper.PlayerAchievements.For(chara)) {
                    branch.AddMemeber(chara);
                }
                break;
            case BaseRequestKind.Banish:
                if (Value == 0) {
                    Msg.Say("banish", chara, branch.owner.Name);
                }

                branch.RemoveMemeber(chara);
                break;
        }
    }
}
