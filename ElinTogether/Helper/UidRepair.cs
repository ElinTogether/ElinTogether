using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ElinTogether.Net;

namespace ElinTogether.Helper;

/// <summary>
///     A world whose card counter went near the end of the numbers (GitHub issues 78-81: 2.147 billion, 250 000 from
///     int.MaxValue; one more trip alone would have overflowed it): before it is hosted, the counter is moved down into
///     the largest run of numbers no card of the world uses, and its end becomes the world's ceiling. The cards keep
///     their numbers; nothing refers to a number that is not a card's. The run is found from every card of the save on
///     disk (each zone's map and game.txt: a card is written with its uid second in "_ints") and from the cards in
///     memory. The game's backup of the save is made just before (ElinNetHost.BackupBeforeNewVersion)
/// </summary>
internal static class UidRepair
{
    // above this the counter is moved down
    internal const int Limit = 2_000_000_000;

    // the run must hold at least this many numbers; the counter starts in its middle, at least this far past its start
    private const int MinRun = 100_000_000;
    private const int Margin = 2_100_000;

    // a counter this close to the ceiling moves again at the next opening of the world
    private const int CeilingRoom = 10_000_000;

    private static readonly Regex CardUid = new("\"_ints\"\\s*:\\s*\\[\\s*-?\\d+\\s*,\\s*(\\d+)", RegexOptions.Compiled);

    /// <summary>
    ///     The world's ceiling, kept in its save: 0 for none (int.MaxValue)
    /// </summary>
    [ElinGameIOProperty("uid_ceiling")]
    private static int[] SavedCeiling
    {
        get => field is { Length: 1 } ? field : field = [0];
        set;
    }

    internal static int Ceiling => SavedCeiling[0] > 0 ? SavedCeiling[0] : int.MaxValue;

    /// <summary>
    ///     Called when the host opens its world, its save backed up first
    /// </summary>
    internal static void Run()
    {
        var cards = EClass.game.cards;
        if (cards.uidNext < Limit && cards.uidNext < Ceiling - CeilingRoom) {
            return;
        }

        var used = new List<int>();
        try {
            used.AddRange(SavedUids(GameIO.pathCurrentSave));
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Card counter {UidNext}: the save could not be read, the counter is left as it is", cards.uidNext);
            return;
        }

        used.AddRange(LoadedUids());
        if (Plan(used, cards.uidNext) is not var (start, end)) {
            EmpLog.Error("Card counter {UidNext}: no run of {Run} free numbers found among {Count} cards, left as it is",
                cards.uidNext, MinRun, used.Count);
            return;
        }

        EmpLog.Warning("Card counter {UidNext} near the end of the numbers: moved down to {Start}, ceiling {End} ({Count} cards read)",
            cards.uidNext, start, end, used.Count);
        cards.uidNext = start;
        SavedCeiling[0] = end;
        ElinNetHost.ResetCardRangeFloor(start);
    }

    /// <summary>
    ///     The largest run of numbers no card uses, below the counter: its start plus the margin, and its end. Null when
    ///     it is shorter than the minimum
    /// </summary>
    internal static (int Start, int End)? Plan(IEnumerable<int> used, int uidNext)
    {
        var sorted = used.Where(u => u > 0 && u < uidNext).Append(0).Append(uidNext).Distinct().OrderBy(u => u).ToArray();
        long best = 0;
        var at = 0;
        for (var i = 1; i < sorted.Length; i++) {
            long run = (long)sorted[i] - sorted[i - 1] - 1;
            if (run > best) {
                best = run;
                at = i;
            }
        }

        if (best < MinRun) {
            return null;
        }

        // in the middle of the run: its first numbers may be those of a range a player's game still keeps and the
        // host's save does not hold (a guest that left mid-trip and never came back)
        return ((int)Math.Max(sorted[at - 1] + Margin, sorted[at - 1] + best / 2), sorted[at]);
    }

    private static IEnumerable<int> SavedUids(string root)
    {
        var files = Directory.GetFiles(root, "map", SearchOption.AllDirectories)
            .Append(Path.Combine(root, "game.txt"))
            .Where(File.Exists);
        foreach (var file in files) {
            var text = IO.IsCompressed(file) ? IO.Decompress(file) : File.ReadAllText(file);
            foreach (Match m in CardUid.Matches(text)) {
                if (int.TryParse(m.Groups[1].Value, out var uid)) {
                    yield return uid;
                }
            }
        }
    }

    private static IEnumerable<int> LoadedUids()
    {
        var roots = new List<Card>();
        roots.AddRange(EClass.game.cards.globalCharas.Values);
        foreach (var zone in EClass.game.spatials.map.Values.OfType<Zone>()) {
            if (zone.map is { } map) {
                roots.AddRange(map.things);
                roots.AddRange(map.charas);
            }
        }

        var seen = new HashSet<Card>();
        var stack = new Stack<Card>(roots);
        while (stack.Count > 0) {
            var card = stack.Pop();
            if (card is null || !seen.Add(card)) {
                continue;
            }

            yield return card.uid;
            foreach (var thing in card.things) {
                stack.Push(thing);
            }
        }
    }
}
