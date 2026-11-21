using ElinTogether.Helper;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CharaMakeAllyRequestDelta : ElinDelta
{
    // recruitItems
    private static readonly string[] _excluded = ["mamani2"];

    [Key(0)]
    public required RemoteCard? Owner { get; init; }

    [Key(1)]
    public required string? LocalCardId { get; init; }

    [Key(2)]
    public bool IsCopy { get; init; }

    [Key(3)]
    public bool ShowMsg { get; init; }

    /// <summary>
    ///     Already of the base, only asked to join the party (see PartyJoinEvent)
    /// </summary>
    [Key(4)]
    public bool JoinOnly { get; init; }

    /// <summary>
    ///     The character itself, when it only exists in the game of the player who recruits it: a slave or an
    ///     animal bought from a trader, a pet given by a dialog. Without it the host has nobody to recruit
    /// </summary>
    [Key(5)]
    public LZ4Bytes? Data { get; init; }

    /// <summary>
    ///     What the invite dialog took for it (DramaCustomSequence "_invite"): in a client's game that ModNum changes
    ///     nothing (CardModNumEvent), the host takes it from the recruiter's bag
    /// </summary>
    [Key(6)]
    public RemoteCard? Paid { get; init; }

    [Key(7)]
    public int PaidNum { get; init; }

    // the client's last payment in a dialog (thing, amount, frame)
    private static (Card Thing, int Num, int Frame)? _payment;

    /// <summary>
    ///     A character the game only hires for a price (the invite dialog: it cannot be invited, and asks for the
    ///     items of its recruitItems) comes with what was paid for it. A free recruit (a story, a spell, a pet, an
    ///     invitable monster) has no price and goes through. Whether the payment itself is right is TakePayment's
    /// </summary>
    internal override string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        if (Owner is null || JoinOnly || Paid is not null) {
            return null;
        }

        if (Owner.Find() is not Chara { isDead: false } chara || chara.IsPCParty || chara.trait.CanInvite ||
            chara.source.recruitItems.IsEmpty()) {
            return null;
        }

        return $"a recruit of {chara.id} that is hired for {chara.source.recruitItems[0]}, with nothing paid";
    }

    protected override void OnApply(ElinNetBase net)
    {
        if (net is not ElinNetHost host) {
            return;
        }

        if (Owner is null) {
            ReplayLocalCopy(host);
            return;
        }

        if (Owner.Find() is not Chara { isDead: false } chara) {
            return;
        }

        if (JoinOnly) {
            JoinParty(host, chara);
            return;
        }

        // already ours: nothing was taken from the client for it, nothing to give back
        if (chara.IsPCParty) {
            return;
        }

        host.ActiveRemoteCharas.TryGetValue(OriginPeer, out var recruiter);
        using var _ = Simulate();
        TakePayment(recruiter, chara.source);
        // the companion follows the player who recruited it
        chara.SetCompanionOwner(recruiter);
        chara.MakeAlly(ShowMsg);
    }

    private void JoinParty(ElinNetHost host, Chara chara)
    {
        if (!host.ActiveRemoteCharas.TryGetValue(OriginPeer, out var recruiter)) {
            EmpLog.Information("Join of {Uid} asked by peer {PeerIndex}, whose character is not here: not taken", chara.uid, OriginPeer);
            return;
        }

        if (pc.party.members.Contains(chara)) {
            EmpLog.Information("Join of {Uid} asked by player {Recruiter}: already in the party", chara.uid, recruiter.uid);
            return;
        }

        // out of the members but still pointing at the party (a companion that died on its owner's trip, see
        // ReplaceCompanions): the game's AddMemeber skips it, every "Join me" said yes and nothing came (issue 73)
        if (chara.party == pc.party) {
            EmpLog.Information("Join of {Uid}: it pointed at the party without being in it, joined again", chara.uid);
            chara.party = null;
        }

        using var _ = Simulate();
        // it follows the player who asked
        chara.SetCompanionOwner(recruiter);

        // called from the list of residents while it is somewhere else: it comes, as in the game
        if (chara.currentZone != _zone) {
            chara.MoveZone(_zone);
            chara.MoveImmediate(recruiter.pos.GetNearestPoint(false, false) ?? recruiter.pos);
        }

        pc.party.AddMemeber(chara, ShowMsg);
    }

    private void ReplayLocalCopy(ElinNetHost host)
    {
        if (LocalCardId is null) {
            return;
        }

        var receiver = host.ActiveRemoteCharas.TryGetValue(OriginPeer, pc);
        if (!_excluded.Contains(LocalCardId)) {
            AdoptLocal(host, receiver);
            return;
        }

        using var _ = Simulate();
        // Mamani's copy is what paying for Mamani gives
        TakePayment(receiver == pc ? null : receiver, sources.charas.map["mamani"]);
        var copy = CharaGen.Create(LocalCardId);
        _zone.AddCard(copy, receiver.pos.GetNearestPoint());
        copy.isCopy = IsCopy;
        copy.SetCompanionOwner(receiver == pc ? null : receiver);
        copy.MakeAlly(ShowMsg);
    }

    /// <summary>
    ///     The character the player bought or was given comes to this world as it is, under uids of this world
    /// </summary>
    private void AdoptLocal(ElinNetHost net, Chara receiver)
    {
        if (Data is null) {
            return;
        }

        Chara chara;
        try {
            chara = Data.Decompress<Chara>();
        } catch (System.Exception ex) {
            EmpLog.Warning(ex, "Could not read the {Id} recruited by player {Peer}", LocalCardId, OriginPeer);
            return;
        }

        using var _ = Simulate();
        chara.currentZone = null;
        game.cards.AssignUIDRecursive(chara);
        chara.isCopy = IsCopy;
        chara.SetCompanionOwner(receiver == pc ? null : receiver);

        // nobody generated it here: the other games learn it whole, as a companion that comes back
        net.Delta.AddRemote(CardGenDelta.Create(chara));
        _zone.AddCard(chara, receiver.pos.GetNearestPoint(false, false) ?? receiver.pos);
        CardCache.Add(chara);
        CardCache.CacheContainer(chara.things);

        chara.MakeAlly(ShowMsg);
    }

    /// <summary>
    ///     The recruit cost the client's dialog paid, taken once from that player's bag. Never a refusal: a recruit
    ///     the game gives for free (a story, a spell, a pet) comes without payment, an odd one is only logged
    /// </summary>
    private void TakePayment(Chara? recruiter, SourceChara.Row source)
    {
        if (Paid is null) {
            return;
        }

        var reqs = source.recruitItems.IsEmpty() ? [] : source.recruitItems[0].Split('/');
        if (recruiter is null || reqs.Length < 2 || Paid.Find() is not Thing { isDestroyed: false } paid ||
            paid.GetRootCard() != recruiter || paid.id != reqs[0] || PaidNum != reqs[1].ToInt() ||
            paid.Num < PaidNum) {
            EmpLog.Warning("Recruit of {Id} by peer {PeerIndex}: payment {Paid} x{Num} not taken",
                source.id, OriginPeer, Paid, PaidNum);
            return;
        }

        paid.ModNum(-PaidNum);
    }

    /// <summary>
    ///     Client side: a ModNum asked for in a dialog that changed nothing here (CardModNumEvent)
    /// </summary>
    internal static void NotePayment(Card thing, int num)
    {
        _payment = (thing, num, UnityEngine.Time.frameCount);
    }

    // the recruit cost paid in this frame's dialog, right before the MakeAlly it pays for
    private static (Card Thing, int Num)? TakeNotedPayment(Chara chara)
    {
        var noted = _payment;
        _payment = null;
        var source = chara.id == "mamani2" ? sources.charas.map["mamani"] : chara.source;
        if (noted is not { } p || p.Frame != UnityEngine.Time.frameCount || source.recruitItems.IsEmpty()) {
            return null;
        }

        var reqs = source.recruitItems[0].Split('/');
        return reqs.Length >= 2 && p.Thing.id == reqs[0] && p.Num == reqs[1].ToInt() ? (p.Thing, p.Num) : null;
    }

    /// <summary>
    ///     A character made in this game only, that the host can only recruit if it receives it whole
    /// </summary>
    internal static bool IsLocalOnly(Chara chara)
    {
        return PendingUid.IsPending(chara.uid) && !_excluded.Contains(chara.id);
    }

    public static CharaMakeAllyRequestDelta Create(Chara chara, bool msg)
    {
        var pending = PendingUid.IsPending(chara.uid);
        var payment = TakeNotedPayment(chara);
        return new() {
            Owner = pending ? null : chara,
            LocalCardId = pending ? chara.id : null,
            IsCopy = chara.isCopy, // keep sync with host
            ShowMsg = msg,
            Data = pending && !_excluded.Contains(chara.id) ? LZ4Bytes.Create(chara) : null,
            Paid = payment?.Thing,
            PaidNum = payment?.Num ?? 0,
        };
    }
}