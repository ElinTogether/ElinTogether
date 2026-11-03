using System;
using System.Collections.Generic;
using ElinTogether.Helper;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

// Card
[Union(100, typeof(CardGenDelta))]
[Union(101, typeof(CardDamageHpDelta))]
[Union(102, typeof(CardPlacedDelta))]
[Union(103, typeof(CardModNumDelta))]
[Union(104, typeof(CardAddThingDelta))]
[Union(105, typeof(CardRemoveThingDelta))]
[Union(106, typeof(CardOnUseDelta))]
[Union(107, typeof(CardTryStackToDelta))]
[Union(109, typeof(CardSetDirDelta))]
[Union(110, typeof(CardUidRebindDelta))]
[Union(111, typeof(CardToggleDelta))]
[Union(112, typeof(CardChargeDelta))]
[Union(113, typeof(CardIdentifyDelta))]
[Union(114, typeof(CardShrineUsedDelta))]
// Chara
[Union(200, typeof(CharaMoveDelta))]
[Union(201, typeof(CharaTickDelta))]
[Union(202, typeof(CharaMakeAllyDelta))]
[Union(203, typeof(CharaPickThingDelta))]
[Union(204, typeof(CharaDieDelta))]
[Union(205, typeof(CharaActPerformDelta))]
[Union(206, typeof(CharaAddConditionDelta))]
[Union(207, typeof(CharaReviveDelta))]
[Union(208, typeof(CharaTickConditionDelta))]
[Union(209, typeof(CharaTaskDelta))]
[Union(210, typeof(CharaBuildDelta))]
[Union(211, typeof(CharaProgressBeginDelta))]
[Union(212, typeof(CharaProgressCompleteDelta))]
[Union(213, typeof(CharaTaskCancelDelta))]
[Union(214, typeof(CharaHitFishDelta))]
[Union(215, typeof(CharaGiveGiftDelta))]
[Union(216, typeof(CharaSwitchHeldDelta))]
[Union(217, typeof(CharaRemoveFromGameDelta))]
[Union(218, typeof(CharaSleepDelta))]
[Union(219, typeof(CharaStaminaDelta))]
[Union(220, typeof(CharaMakeAllyRequestDelta))]
[Union(221, typeof(CharaEquipDelta))]
[Union(222, typeof(PartyMemberDelta))]
[Union(223, typeof(CharaFaithDelta))]
[Union(224, typeof(CharaFeatPointDelta))]
[Union(225, typeof(CharaLevelDelta))]
// Thing
[Union(300, typeof(ThingDelta))]
[Union(301, typeof(ThingRequest))]
[Union(302, typeof(CardModCurrencyDelta))]
// Zone
[Union(400, typeof(SpatialGenDelta))]
[Union(401, typeof(ZoneAddCardDelta))]
// World
[Union(500, typeof(GameDelta))]
[Union(501, typeof(WorldDateAdvanceDelta))]
[Union(504, typeof(WorldTimeReportDelta))]
[Union(505, typeof(WeatherDelta))]
[Union(506, typeof(DayDataDelta))]
[Union(502, typeof(ShippingResultDelta))]
[Union(503, typeof(BranchResourceModDelta))]
// Misc
[Union(600, typeof(OnBarterDelta))]
[Union(601, typeof(CardRendererTalkDelta))]
[Union(602, typeof(MsgSayDelta))]
[Union(603, typeof(EnemyVisibilityDelta))]
[Union(604, typeof(PingPointDelta))]
[Union(605, typeof(CombatPhaseDelta))]
[Union(606, typeof(AddRecipeDelta))]
[Union(607, typeof(CombatReadyDelta))]
[Union(608, typeof(SleepRequestDelta))]
[Union(609, typeof(SleepReadyDelta))]
[Union(610, typeof(SleepStartDelta))]
[Union(611, typeof(SleepCancelDelta))]
[Union(620, typeof(PlayerTurnDelta))]
// Inv
[Union(700, typeof(InvOwnerOnProcessDelta))]
[Union(701, typeof(InvRerollDelta))]
[Union(702, typeof(InvSaveDataDelta))]
[Union(703, typeof(InvPlaceAbilityDelta))]
// Quest
[Union(226, typeof(CharaAffinityDelta))]
[Union(227, typeof(CharaDestroyPathDelta))]
[Union(228, typeof(CharaAppearanceDelta))]
[Union(704, typeof(TradeIntentDelta))]
[Union(705, typeof(TradeStateDelta))]
[Union(800, typeof(QuestCreateDelta))]
[Union(801, typeof(QuestSetClientDelta))]
[Union(802, typeof(QuestStartDelta))]
[Union(803, typeof(QuestAcceptDelta))]
[Union(804, typeof(QuestCompleteDelta))]
[Union(805, typeof(QuestUpdateDelta))]
[Union(806, typeof(QuestChangePhaseDelta))]
[Union(807, typeof(DialogFlagDelta))]
[Union(808, typeof(StoryGiftDelta))]
[Union(809, typeof(StoryOutcomeDelta))]
[Union(810, typeof(QuestFailDelta))]
[Union(811, typeof(PersonalStateDelta))]
[Union(812, typeof(PersonalQuestDelta))]
[Union(813, typeof(QuestTakenDelta))]
[Union(814, typeof(PlayerStandingDelta))]
[Union(816, typeof(CraftFirstTimeDelta))]
[Union(817, typeof(CardActReplayDelta))]
[Union(819, typeof(QuestFollowDelta))]
[Union(821, typeof(CharaEffectRequestDelta))]
[Union(822, typeof(ZoneInvestDelta))]
[Union(823, typeof(PlayerTacticsDelta))]
[Union(824, typeof(BaseRequestDelta))]
[Union(825, typeof(BaseStateDelta))]
[Union(826, typeof(CardSettingDelta))]
[Union(827, typeof(PolicyStateDelta))]
[Union(828, typeof(CardDecayDelta))]
[Union(829, typeof(NameDelta))]
[Union(830, typeof(TileStateDelta))]
[Union(831, typeof(AgentTaskDelta))]
[Union(832, typeof(CharaReviveRequestDelta))]
[Union(833, typeof(TerrainHeightDelta))]
[Union(834, typeof(AreaStateDelta))]
[Union(835, typeof(CopyShopDelta))]
[Union(837, typeof(DuelIntentDelta))]
[Union(838, typeof(DuelStateDelta))]
[Union(840, typeof(SleepStateDelta))]
[Union(839, typeof(BillPayDelta))]
[Union(841, typeof(CharaBagDelta))]
[Union(842, typeof(DesyncReportDelta))]
[Union(843, typeof(CardPoseDelta))]
[Union(844, typeof(CodexDelta))]
[Union(845, typeof(QuestOffersDelta))]
[Union(846, typeof(QuestRerollDelta))]
[Union(847, typeof(MapBoundsDelta))]
[Union(848, typeof(CharaSettingDelta))]
[Union(849, typeof(ExtraTaxDelta))]
[Union(850, typeof(BossWinDelta))]
[Union(851, typeof(PartyExpDelta))]
[Union(852, typeof(QuestJournalDelta))]
[Union(853, typeof(ClaimZoneDelta))]
[Union(854, typeof(CardSplitStackDelta))]
[Union(855, typeof(TentZoneDelta))]
[Union(856, typeof(GateFollowDelta))]
[Union(857, typeof(CellFlagDelta))]
[Union(858, typeof(WorldPrincipalDelta))]
[Union(859, typeof(GeneMachineDelta))]
[Union(860, typeof(AutoCombatDelta))]
[Union(861, typeof(PlaylistDelta))]
[Union(862, typeof(PlayerTitleDelta))]
[Union(863, typeof(TrolleyDelta))]
[Union(871, typeof(CardItemStateDelta))]
// Act
[Union(900, typeof(ActThrowDelta))]
// Counters of the player
[Union(910, typeof(FortuneCountersDelta))]
[Union(920, typeof(ThrowGrowthDelta))]
// World map time
[Union(940, typeof(WorldTravelStepDelta))]
[Union(941, typeof(WorldMapTogetherDelta))]
[Union(960, typeof(PlayerLeadDelta))]
[Union(962, typeof(WhisperDelta))]
[Union(963, typeof(GateItemRequest))]
[Union(964, typeof(GateItemAnswer))]
[Union(965, typeof(GateZoneDelta))]
[Union(970, typeof(MarriageDelta))]
[Union(971, typeof(CharaBanishRequest))]
[Union(972, typeof(ManiGeneRequest))]
[Union(973, typeof(CharaBloomRequest))]
[Union(974, typeof(StrifeKillDelta))]
[Union(975, typeof(CharaEggRequest))]
[Union(976, typeof(RelicSlotRequest))]
[Union(977, typeof(MiscreationRequest))]
// Teams
[Union(978, typeof(TeamBeatenDelta))]
[Union(980, typeof(TeamsDelta))]
[Union(981, typeof(TeamRequestDelta))]
[Union(982, typeof(TeamSpawnDelta))]
// Teams learn on their own
[Union(1010, typeof(TeamLoreDelta))]
// Teams live their own story
[Union(1110, typeof(StoryBundleDelta))]
// World time (council 2026-10-26)
[Union(983, typeof(PlayerIdleDelta))]
// Host/guest equality fixes (2026-10-28)
[Union(1190, typeof(KnownCraftDelta))]
// Admin tools (council 2026-10-27)
[Union(984, typeof(AdminCommandDelta))]
[Union(985, typeof(ModerationDelta))]
// Hunt 5, each player's own (2026-10-27)
[Union(1040, typeof(BackerGiftDelta))]
[Union(1041, typeof(AchievementDelta))]
// Hunt 6, the base moved for everyone (2026-10-28)
[Union(1120, typeof(BaseRelocateDelta))]
// Teams keep their own bank and delivery box (2026-10-28)
[Union(1196, typeof(TeamBoxDelta))]
// Element
[Union(1000, typeof(ElementChangeDelta))]
// Duels, council 7 steps 5 to 7
[Union(1030, typeof(AdventurerBoutDelta))]
// Affinity per player (2026-10-28)
[Union(1170, typeof(PlayerAffinitiesDelta))]
// Container access log (2026-10-28)
[Union(1210, typeof(ContainerLogRequest))]
[Union(1211, typeof(ContainerLogAnswer))]
// Closed lands: a player is moved out (council 2026-10-28)
[Union(1200, typeof(TeamEvictDelta))]
[Union(1225, typeof(SiblingLandDelta))]
// Hub features: character sheets, activity journal, leaderboards (PLAN_hub_features)
[Union(1230, typeof(CharaSheetDelta))]
[Union(1231, typeof(JournalDelta))]
[Union(1232, typeof(LeaderboardDelta))]
// Market: classified ads (user 2026-10-30)
[Union(1245, typeof(MarketAskDelta))]
[Union(1246, typeof(MarketBoardDelta))]
// Mailbox: letters between players, kept by the host
[Union(1240, typeof(MailRequestDelta))]
[Union(1241, typeof(MailStateDelta))]
// Teammates share quests (user 2026-10-30)
[Union(1250, typeof(TeamQuestListDelta))]
[Union(1251, typeof(TeamQuestUpdateDelta))]
[Union(1252, typeof(TeamQuestDoneDelta))]
// Bug reports: a guest's report asks for the host's log (user 2026-10-30)
[Union(1255, typeof(HostLogRequest))]
[Union(1256, typeof(HostLogAnswer))]
// Every player of the server, away ones too, for the menus (user 2026-10-30)
[Union(1259, typeof(ServerRosterDelta))]
// A player's own count of its god's gifts, from the host (GitHub issue 51)
[Union(1265, typeof(GodGiftDelta))]
// A refused action says why on the player's game (user 2026-11-01)
[Union(1263, typeof(RefusedDelta))]
// Farris made on Nymelle B2F by the player holding it, for the host's world (GitHub issue 55)
[Union(1272, typeof(NymelleFarrisDelta))]
// A change to the ranks of the sender's team, asked of the host (lead dev 2026-11-01)
[Union(1290, typeof(TeamRankRequestDelta))]
// A chat line of one emote: the game's emote over the speaker, for its map (lead dev 2026-11-02)
[Union(1291, typeof(ChatEmoteDelta))]
// A small picture in the chat, the hook of the drawing editor (lead dev 2026-11-02)
[Union(1292, typeof(ChatImageDelta))]
// The public server asks a joined player for its world service key and vouches for it (PLAN_peer_hosted_server 2.1)
[Union(1310, typeof(EtWorldKeyDelta))]
public abstract class ElinDelta : EClass
{
    private static int _applyDepth;

    internal virtual OverrideOrder Order { get; } = OverrideOrder.Stack;

    // what Order First/Last is counted by besides the type, e.g. the character the delta is about
    internal virtual int OverrideKey => 0;

    internal virtual bool RequiresGameStarted { get; } = true;

    /// <summary>
    ///     A guest's message the host checks and applies inside the sender's own story (host option "Each player or
    ///     team lives its own story", TeamOwnStory.As): its permit, its refusal and its apply read that story
    /// </summary>
    internal virtual bool InSendersStory => false;

    public static bool IsApplying => _applyDepth > 0;

    // remote applying, ThingRequest is local sim which does not cunt
    public static bool IsRemoteStateLanding => IsApplying && !ThingRequest.IsReplayingIntent;

    internal int OriginPeer { get; set; }

    internal int DeferCount { get; set; }

    // came in from a guest (OriginPeer alone cannot say it: 0 is also a peer id)
    internal bool FromPeer { get; set; }

    protected virtual void OnApply(ElinNetBase net)
    {
    }

    protected virtual bool OnRefresh()
    {
        return true;
    }

    /// <summary>
    ///     On the host, for a delta a guest sent: why that guest may not do this (the character it is about is
    ///     neither its own nor one of its companions), null when it may. Council 2 on network trust (audit
    ///     2026-10-23): a reason is only logged ("would have refused") and the delta applied as before, unless the
    ///     host turned EnforceOwnership on or the game is public (TrustEnforced): then it is dropped and
    ///     OnHostRefused sends the sender how things really are. Never a kick: a lagging game says stale things
    ///     too. The default: the sender plays a character here
    /// </summary>
    internal virtual string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        return null;
    }

    /// <summary>
    ///     For the deltas about a character: the sender must have one. Not for all: a guest that just left keeps
    ///     sending its last batch (bills, quests, the codex), which the intake lets through for an away player
    ///     (review 2026-10-23)
    /// </summary>
    protected static string? NoCharacter(Chara? sender)
    {
        return sender is null ? "the sender has no character" : null;
    }

    /// <summary>
    ///     The reason to give when <paramref name="card" /> is a character that is neither the sender nor one of
    ///     its companions or summons. A card not found is allowed: the delta does nothing with it anyway
    /// </summary>
    protected static string? NotOwned(Chara? sender, RemoteCard? card, string what)
    {
        if (sender is null || card?.Find() is not Chara target || OwnsOrCompanion(sender, target)) {
            return null;
        }

        _refusedOwner = PlayerKarma.PlayerBehind(target);
        return $"{what} {target.uid} is not the sender's";
    }

    // the player a refused thing or character belongs to, set by the checks above for the toast (RefusedDelta)
    private static Chara? _refusedOwner;

    /// <summary>
    ///     A thing the sender may move, take, wear or put away (security audit 2026-10-27: other bags, merchants and
    ///     chests were open from anywhere): in its own bag or a companion's, or on this map within a few steps of it,
    ///     on the floor or in a container there. A thing not found, or kept outside every map (the world's boxes), is
    ///     left to the delta
    /// </summary>
    protected static string? NotReachable(Chara? sender, RemoteCard? card)
    {
        if (sender is null || card?.Find() is not Thing thing) {
            return null;
        }

        var root = thing.GetRootCard();
        if (root is Chara holder) {
            if (OwnsOrCompanion(sender, holder)) {
                return null;
            }

            _refusedOwner = PlayerKarma.PlayerBehind(holder);
            return $"thing {thing.uid} is held by {holder.uid}";
        }

        if (root?.parent is not Zone || root.pos is not { IsValid: true } at) {
            return null;
        }

        return sender.currentZone == root.parent && sender.pos.Distance(at) <= ReachTiles
            ? null
            : $"thing {thing.uid} is out of the sender's reach";
    }

    // what a player reaches on a map: next to it, plus the steps a lagging game may not have shown yet
    private const int ReachTiles = 6;

    /// <summary>
    ///     The target is the sender itself, its companion, or the summon of one of them
    /// </summary>
    internal static bool OwnsOrCompanion(Chara sender, Chara target)
    {
        // (an untagged companion reads as the party leader's, the host's: the host's own pets are never tagged, and
        // letting untagged ones through let a guest act on them; bench permit_suite 2026-10-23)
        return target.uid == sender.uid || PlayerKarma.PlayerBehind(target)?.uid == sender.uid;
    }

    // delta type and peer -> when it was last logged, so a stream of the same delta is one line per window
    private static readonly Dictionary<(Type, int), float> _permitLogged = [];
    private const float PermitLogWindow = 10f;

    /// <summary>
    ///     The checks refuse for real: the host's box, or always in a game anybody can join (council 2, audit
    ///     2026-10-23: on for a public game). Off, a refusal is only logged
    /// </summary>
    // (a dedicated server too: its session opens quietly, never "open to all", yet strangers reach it; security audit
    // 2026-10-27)
    internal static bool TrustEnforced => EmpConfig.Server.EnforceOwnership.Value || NetSession.Instance.Lobby.OpenToAll ||
                                          EmpServer.Requested;

    /// <summary>
    ///     On the host, after this guest's delta was refused: the sender's game is told how things really are, where
    ///     there is something to tell (the card it moved, used or spent back where the host has it)
    /// </summary>
    internal virtual void OnHostRefused(ElinNetHost host)
    {
    }

    /// <summary>
    ///     That card as the host has it, to everyone: back in its container (CardAddThingDelta.Rebind) or on its tile
    /// </summary>
    internal static void SendBack(ElinNetBase net, RemoteCard? remote)
    {
        if (remote?.Find() is not Thing { isDestroyed: false } thing) {
            return;
        }

        if (thing.parent is Card) {
            CardAddThingDelta.Rebind(net, remote, thing);
        } else if (thing.parent == _zone && thing.pos is { IsValid: true } pos) {
            net.Delta.AddRemote(new ZoneAddCardDelta {
                Card = remote,
                Pos = pos,
                ZoneUid = _zone.uid,
            });
        }
    }

    /// <returns>false when the delta is refused (only when TrustEnforced)</returns>
    private bool HostPermits(ElinNetHost host)
    {
        string? reason;
        var senderChara = host.PlayerChara(OriginPeer);
        _refusedOwner = null;
        try {
            reason = OnHostPermit(host, senderChara);
        } catch (Exception ex) {
            // a check that fails is a refusal like any other: logged, and the delta dropped where strangers come in
            // (security audit 2026-10-27, item 14: a forged delta that made its check throw went through unchecked)
            EmpLog.Information(ex, "Permit check of {DeltaType} threw", GetType().Name);
            reason = "its check failed";
        }

        if (reason is null) {
            return true;
        }

        var enforce = TrustEnforced;
        var key = (GetType(), OriginPeer);
        var now = UnityEngine.Time.unscaledTime;
        if (!_permitLogged.TryGetValue(key, out var last) || now - last >= PermitLogWindow) {
            _permitLogged[key] = now;
            EmpLog.Warning(enforce ? "Refused {DeltaType} from peer {Peer}: {Reason}" : "Would have refused {DeltaType} from peer {Peer}: {Reason}",
                GetType().Name, OriginPeer, reason);
        }

        if (enforce && senderChara is not null && reason != "not a request") {
            TellRefused(host, _refusedOwner);
        }

        return !enforce;
    }

    // peer -> when it was last told a refusal
    private static readonly Dictionary<int, float> _refusedTold = [];

    /// <summary>
    ///     The sender's game shows why its action snapped back (RefusedDelta), once every few seconds at most: a
    ///     lagging game's stream of stale deltas is one toast, not one per delta
    /// </summary>
    private void TellRefused(ElinNetHost host, Chara? owner)
    {
        var now = UnityEngine.Time.unscaledTime;
        if (_refusedTold.TryGetValue(OriginPeer, out var last) && now - last < RefusedDelta.Interval && now >= last) {
            return;
        }

        _refusedTold[OriginPeer] = now;
        host.SendDeltaTo(OriginPeer, new RefusedDelta {
            Owner = owner is null ? null : Teams.PlayerName(owner.uid),
        });
    }

    public void Apply(ElinNetBase net)
    {
        if (FromPeer && InSendersStory && net is ElinNetHost storyHost) {
            if (TeamOwnStory.KeeperDrops(storyHost, this)) {
                return;
            }

            using (TeamOwnStory.AsSender(storyHost, OriginPeer)) {
                ApplyHere(net);
            }

            return;
        }

        ApplyHere(net);
    }

    private void ApplyHere(ElinNetBase net)
    {
        if (FromPeer && net is ElinNetHost permitHost && !HostPermits(permitHost)) {
            try {
                OnHostRefused(permitHost);
            } catch (Exception ex) {
                EmpLog.Information(ex, "Sending back after a refused {DeltaType} threw", GetType().Name);
            }

            return;
        }

        _applyDepth++;
        // what the host does here, it does for the player who sent it: an ally made on the way (a monster
        // ball, a mount, a tamed animal) follows that player, not the host
        var actor = CharaMakeAllyEvent.Actor;
        if (net is ElinNetHost host && host.ActiveRemoteCharas.TryGetValue(OriginPeer, out var sender)) {
            CharaMakeAllyEvent.Actor = sender;
        }

        try {
            OnApply(net);
        } finally {
            CharaMakeAllyEvent.Actor = actor;
            _applyDepth--;
        }
    }

    /// <summary>
    ///     What is done in this scope is told to no one, as if it came from another game (the copies the trade window
    ///     shows, never part of the world)
    /// </summary>
    internal static ScopeExit Quiet()
    {
        _applyDepth++;
        _quietDepth++;
        return new() {
            OnExit = () => {
                _applyDepth--;
                _quietDepth--;
            },
        };
    }

    private static int _quietDepth;

    /// <summary>
    ///     Cards made now are this game's alone (see <see cref="Quiet" />): not announced, not taken back
    /// </summary>
    internal static bool IsQuiet => _quietDepth > 0;

    internal static ScopeExit Simulate(bool active = true)
    {
        var depth = _applyDepth;
        if (active) {
            _applyDepth = 0;
        }

        return new() {
            OnExit = () => {
                if (active) {
                    _applyDepth = depth;
                }
            },
        };
    }

    public bool Refresh()
    {
        return OnRefresh();
    }

    // for harmony patches
    internal readonly struct PatchScope
    {
        private enum Kind : byte
        {
            None,
            Simulate,
            Pending,
        }

        private readonly Kind _kind;
        private readonly int _restore;

        private PatchScope(Kind kind, int restore)
        {
            _kind = kind;
            _restore = restore;
        }

        internal bool IsActive => _kind != Kind.None;

        internal static PatchScope Simulate(bool active = true)
        {
            if (!active) {
                return default;
            }

            var previous = _applyDepth;
            _applyDepth = 0;
            return new(Kind.Simulate, previous);
        }

        internal static PatchScope Pending(bool active = true)
        {
            if (!active) {
                return default;
            }

            PendingContext.Enter();
            return new(Kind.Pending, 0);
        }

        internal void Exit()
        {
            switch (_kind) {
                case Kind.Simulate:
                    _applyDepth = _restore;
                    break;
                case Kind.Pending:
                    PendingContext.Exit();
                    break;
            }
        }
    }

    internal enum OverrideOrder
    {
        Stack,
        Last,
        First,
    }
}