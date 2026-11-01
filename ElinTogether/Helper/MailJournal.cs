using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ElinTogether.Net;
using Newtonsoft.Json;

namespace ElinTogether.Helper;

/// <summary>
///     The Mailbox's journal (mailbox safety, 2026-11-01): a letter's items live in two places that the game writes
///     one after the other, the world (the sender's or recipient's bag) and the mail book (a GameIO chunk written after
///     the world, maybe by another thread, <see cref="SaveWriter" />). A game stopped between the two kept a world
///     without the items and a book without the letter. <br />
///     Each send, take or post therefore leaves, in the same frame and before any save can run: a mark on the
///     character whose bag changed (saved with that bag, so with it or not at all) and an entry in a small file beside
///     the save, flushed at once, holding the letter's row before and after. On load, for every entry still there, the
///     mark says which side of the change the saved world is on, and the book is set to the matching row when its items
///     or gold differ. Entries go once a save that holds them is wholly on the disk. A letter of text only is not put
///     back (nothing in it can be lost)
/// </summary>
internal static class MailJournal
{
    private const string FileName = "emp_mail_journal.json";
    private const string MarkKey = "emp_mail_marks";

    internal sealed class Entry
    {
        public long Seq;
        public int Id;
        public int Chara;
        public string[]? Before;
        public string[]? After;
    }

    private static List<Entry> _entries = [];
    private static Game? _game;
    private static long _seq;
    private static bool _dirty;
    // the entries a finished save holds: dropped once its files are on the disk
    private static HashSet<long>? _saved;

    private static string FilePath => Path.Combine(GameIO.pathCurrentSave, FileName);

    internal static int Count => _entries.Count;

    /// <summary>
    ///     The entries belong to the world loaded: another one (a new world, a load) starts from its own file
    /// </summary>
    private static void Ensure()
    {
        if (ReferenceEquals(_game, EClass.game)) {
            return;
        }

        _game = EClass.game;
        _entries = Read();
        _saved = null;
        _dirty = false;
    }

    private static List<Entry> Read()
    {
        try {
            var path = FilePath;
            return File.Exists(path) ? JsonConvert.DeserializeObject<List<Entry>>(File.ReadAllText(path, Encoding.UTF8)) ?? [] : [];
        } catch (Exception ex) {
            EmpLog.Error(ex, "Mail journal: not read, kept on the disk as it is");
            return [];
        }
    }

    private static bool Write()
    {
        try {
            var path = FilePath;
            if (_entries.Count == 0) {
                if (File.Exists(path)) {
                    File.Delete(path);
                }

                _dirty = false;
                return true;
            }

            var temp = path + ".tmp";
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(_entries));
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) {
                file.Write(bytes, 0, bytes.Length);
                file.Flush(true);
            }

            if (!File.Exists(path)) {
                File.Move(temp, path);
            } else {
                try {
                    File.Replace(temp, path, null);
                } catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException) {
                    File.Copy(temp, path, true);
                    File.Delete(temp);
                }
            }

            _dirty = false;
            return true;
        } catch (Exception ex) {
            // (tried again right before the next save writes the world)
            _dirty = true;
            EmpLog.Error(ex, "Mail journal: not written, {Count} entries kept in memory", _entries.Count);
            return false;
        }
    }

    private static List<long> Marks(Card card)
    {
        return (card.GetStr(MarkKey) ?? "").Split([','], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => long.TryParse(s, out var v) ? v : 0L).Where(v => v > 0).ToList();
    }

    private static void SetMarks(Card card, List<long> marks)
    {
        card.SetStr(MarkKey, marks.Count == 0 ? null : string.Join(",", marks));
    }

    /// <summary>
    ///     A change to letter <paramref name="id" /> that moved things in <paramref name="chara" />'s bag, just made
    ///     (same frame): before and after are the letter's rows, null for no letter
    /// </summary>
    internal static void Record(int id, Chara chara, string[]? before, string[]? after)
    {
        Ensure();
        var entry = new Entry {
            Seq = _seq = Math.Max(_seq + 1, DateTime.UtcNow.Ticks),
            Id = id,
            Chara = chara.uid,
            Before = (string[]?)before?.Clone(),
            After = (string[]?)after?.Clone(),
        };
        _entries.Add(entry);
        var marks = Marks(chara);
        marks.Add(entry.Seq);
        SetMarks(chara, marks);
        Write();
    }

    /// <summary>
    ///     A letter's items and gold, what the journal compares (no letter = an empty one)
    /// </summary>
    private static string Content(string[]? row)
    {
        return row is null ? "0|" : Mailbox.ContentOf(row);
    }

    /// <summary>
    ///     The book as the saved world needs it: for each letter of the journal, the row after the last change whose
    ///     mark the world holds, or the row before the first one when it holds none. Returns how many were set
    /// </summary>
    internal static int Reconcile()
    {
        _game = EClass.game;
        _entries = Read();
        _saved = null;
        _dirty = false;
        if (_entries.Count == 0) {
            return 0;
        }

        var book = ElinNetHost.MailLetters;
        var set = 0;
        foreach (var group in _entries.GroupBy(e => e.Id)) {
            var ordered = group.OrderBy(e => e.Seq).ToList();
            Entry? last = null;
            var unknown = false;
            foreach (var entry in ordered) {
                if (EClass.game.cards.globalCharas.Find(entry.Chara) is not { } chara) {
                    unknown = true;
                    break;
                }

                if (Marks(chara).Contains(entry.Seq)) {
                    last = entry;
                }
            }

            if (unknown) {
                EmpLog.Warning("Mail journal: letter {Id} left as the book has it, a character of its journal is gone", group.Key);
                continue;
            }

            var expected = last is not null ? last.After : ordered[0].Before;
            book.TryGetValue(group.Key, out var actual);
            if (Content(actual) == Content(expected)) {
                continue;
            }

            if (expected is null) {
                book.Remove(group.Key);
            } else {
                book[group.Key] = (string[])expected.Clone();
                ElinNetHost.MailLastId = Math.Max(ElinNetHost.MailLastId, group.Key);
            }

            set++;
            EmpLog.Warning("Mail journal: letter {Id} set back to the world as saved ({Side}): {Before} -> {After}", group.Key,
                last is null ? "before its first change" : "after change " + last.Seq, Content(actual), Content(expected));
        }

        EmpLog.Information("Mail journal: {Count} entries read, {Set} letters set back", _entries.Count, set);
        return set;
    }

    [ElinPostLoad]
    private static void OnLoaded(GameIOContext? context)
    {
        // (the book of the save is read after this hook: one frame later, as RemoveLeftOverCharas)
        var loaded = EClass.core.game;
        EClass.core.actionsNextFrame.Add(() => {
            if (EClass.core.game != loaded || loaded is null) {
                return;
            }

            try {
                Reconcile();
            } catch (Exception ex) {
                EmpLog.Error(ex, "Mail journal: not applied");
            }
        });
    }

    [ElinPreSave]
    private static void OnSaving(GameIOContext? context)
    {
        // what could not be written goes on the disk before the world does
        if (_dirty && ReferenceEquals(_game, EClass.game)) {
            Write();
        }
    }

    [ElinPostSave]
    private static void OnSaved(GameIOContext? context)
    {
        if (ReferenceEquals(_game, EClass.game) && _entries.Count > 0) {
            _saved = [.._entries.Select(e => e.Seq)];
        }
    }

    /// <summary>
    ///     Every frame: the entries a save holds go once its files are all on the disk, and their marks with them
    /// </summary>
    internal static void Tick()
    {
        if (_saved is not { } saved || !SaveWriter.Idle || !ReferenceEquals(_game, EClass.game)) {
            return;
        }

        _saved = null;
        var gone = _entries.Where(e => saved.Contains(e.Seq)).ToList();
        if (gone.Count == 0) {
            return;
        }

        foreach (var group in gone.GroupBy(e => e.Chara)) {
            if (EClass.game.cards.globalCharas.Find(group.Key) is { } chara) {
                var seqs = group.Select(e => e.Seq).ToHashSet();
                SetMarks(chara, Marks(chara).Where(m => !seqs.Contains(m)).ToList());
            }
        }

        _entries.RemoveAll(e => saved.Contains(e.Seq));
        Write();
    }
}
