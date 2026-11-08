using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CharaSleepDelta : ElinDelta
{
    private static bool _waking;
    private static bool? _dream;
    private static int _power;

    [Key(0)]
    public required int Power { get; init; }

    [Key(1)]
    public required int Days { get; init; }

    /// <summary>
    ///     Guest -> host, once awake: uid, charges left and read (1) or not (0), for each book of its grimoire that
    ///     its own wake-up went through (the host keeps the books, as for any reading)
    /// </summary>
    [Key(2)]
    public int[]? Books { get; init; }

    /// <summary>
    ///     Guest -> host, once awake: the recipe of its own night, for everyone as any recipe
    /// </summary>
    [Key(3)]
    public string? Recipe { get; init; }

    /// <summary>
    ///     Guest -> host, once awake: it slept a night of its own (NetSessionRules.UseOwnSleep), nobody slept it
    ///     for its character there. Power is then the power of the bed it slept in
    /// </summary>
    [Key(4)]
    public bool Own { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (net is ElinNetHost host) {
            OnGuestAwake(host);
            return;
        }

        if (pc.isDead) {
            return;
        }

        // the host of the world to a player away from its map: that night ends in its own game
        if (NetSession.Instance.IsAway && !net.IsZoneSession) {
            SleepSynchronizationContext.EndAwayNight();
            return;
        }

        EmpLog.Debug("Applying host sleep {SleepPower}", Power);

        var bed = SleepSynchronizationContext.TakeBed();
        try {
            if (pc.conSleep is { } sleep) {
                WakeUp(net, sleep, bed, false);
            } else if (!NetSession.Instance.Rules.UseOwnSleep) {
                using var _ = Simulate();
                pc.OnSleep(Power, Days, pc.pos.IsSunLit);
                OwnSettings.DropUnknownDomains(player);
                player.DreamSpell();
            }
        } finally {
            SleepSynchronizationContext.CloseSleepLayerIfOpen();
        }
    }

    /// <summary>
    ///     The night of its own of a guest is over (its night screen ran to its end): it wakes by itself and
    ///     tells the game that keeps the map
    /// </summary>
    internal static void WakeOwn(ElinNetBase net)
    {
        if (pc is { isDead: false, conSleep: { } sleep }) {
            new CharaSleepDelta {
                Power = 0,
                Days = 1,
            }.WakeUp(net, sleep, SleepSynchronizationContext.TakeBed(), true);
        }
    }

    /// <summary>
    ///     Chara.OnSleep of the local player: the power of its rest, for the report of its own night
    /// </summary>
    internal static void Rested(int power)
    {
        if (_waking) {
            _power = power;
        }
    }

    /// <summary>
    ///     The recipe of the night, in the wake-up this player plays for itself: learnt as its own act it would
    ///     go to the host as any recipe and come back from there, twice for this player. Drawn once the wake-up
    ///     is over, see <see cref="WakeUp" />
    /// </summary>
    internal static bool DeferRecipe(bool ehekatl)
    {
        if (!_waking) {
            return false;
        }

        _dream = ehekatl;
        return true;
    }

    /// <summary>
    ///     This player wakes as one alone would: the power of its own bed (LayerSleep.Advance), then the game's
    ///     own end of the sleep (ConSleep.OnRemoved): its grimoire, a recipe, a dream spell, its pillow, its karma
    /// </summary>
    /// <param name="own">a night of its own: the host is told in any case, the character still sleeps there</param>
    private void WakeUp(ElinNetBase net, ConSleep sleep, Thing? bed, bool own)
    {
        var books = pc.things.Find<TraitGrimoire>()?.things.List(b => b.trait is TraitBaseSpellbook)
            .Select(b => (Book: b, Charges: b.c_charges, Read: b.isOn)).ToList() ?? [];

        string? recipe = null;
        _dream = null;
        _power = 0;
        _waking = true;
        try {
            try {
                using var _ = Simulate();
                OwnSettings.DropUnknownDomains(player);
                pc.OnSleep(bed, Days);
                sleep.slept = true;
                sleep.Kill();
            } finally {
                _waking = false;
            }

            if (_dream is { } ehekatl) {
                var known = new Dictionary<string, int>(player.recipes.knownRecipes);

                // told to the host with the night below, not as a recipe learnt on its own as well: the wake-up
                // is not always played inside a message, and the host counted the recipe twice (2026-10-13)
                AddRecipeDelta.IsLanding = true;
                try {
                    player.recipes.OnSleep(ehekatl);
                } finally {
                    AddRecipeDelta.IsLanding = false;
                }

                Rand.SetSeed();

                // one recipe, and the variants the game learns with it (pillar, bridge): the shortest id brings them
                recipe = player.recipes.knownRecipes
                    .Where(r => !known.TryGetValue(r.Key, out var count) || count != r.Value)
                    .Select(r => r.Key)
                    .OrderBy(id => id.Length)
                    .FirstOrDefault();
            }
        } finally {
            // told even when the wake-up threw: the host keeps the books, and what was read of them is read
            var read = books.Where(b => b.Book.c_charges != b.Charges || b.Book.isOn != b.Read)
                .SelectMany(b => new[] { b.Book.uid, b.Book.c_charges, b.Book.isOn ? 1 : 0 })
                .ToArray();
            if (own || read.Length > 0 || recipe is not null) {
                net.Delta.AddRemote(new CharaSleepDelta {
                    Power = own ? _power : Power,
                    Days = Days,
                    Books = read,
                    Recipe = recipe,
                    Own = own,
                });
            }
        }
    }

    private void OnGuestAwake(ElinNetHost host)
    {
        if (!host.ActiveRemoteCharas.TryGetValue(OriginPeer, out var sender)) {
            return;
        }

        // a night of its own: its character still sleeps here, and what the end of a night does for "the party"
        // was done by nobody: its body and its own companions rest here too (hit points are the host's to keep)
        if (Own && NetSession.Instance.Rules.UseOwnSleep && sender.conSleep is { } sleep) {
            // the power is the guest's word: never more than its bed and pillow give, as the game counts them
            var power = SleepSynchronizationContext.RestPower(OriginPeer, sender, Power);
            using var _ = Simulate();
            sleep.Kill();
            foreach (var chara in CompanionHelper.CompanionsOf(sender).Prepend(sender)) {
                if (chara is { isDead: false, IsInActiveMap: true }) {
                    chara.OnSleep(power);
                }
            }
        }

        // only its own books, only fewer charges
        for (var i = 0; Books is not null && i + 2 < Books.Length; i += 3) {
            var left = Books[i + 1];
            if (CardCache.Find(Books[i]) is not Thing { isDestroyed: false, trait: TraitBaseSpellbook } book ||
                book.GetRootCard() != sender || left < 0 || left > book.c_charges) {
                continue;
            }

            if (Books[i + 2] != 0) {
                book.isOn = true;
            }

            if (left < book.c_charges) {
                book.ModCharge(left - book.c_charges);
                if (left == 0) {
                    book.ModNum(-1);
                }
            }
        }

        if (Recipe is null) {
            return;
        }

        // learnt here, and by the other guests as any recipe: the sender has it already (teams learning on their own:
        // its team only, the host's game when it is the host's team)
        EmpLog.Debug("Recipe {RecipeId} of the night of peer {Peer}", Recipe, OriginPeer);
        TeamKnowledge.Learnt(sender.uid, Recipe);
        if (TeamKnowledge.Shares(sender.uid, pc.uid)) {
            player.recipes.Add(Recipe, !player.recipes.IsKnown(Recipe));
        }

        TeamKnowledge.Tell(host, new AddRecipeDelta {
            RecipeId = Recipe,
            Learner = sender.uid,
        }, sender.uid, OriginPeer);
    }
}
