using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using STask = System.Threading.Tasks.Task;
using HarmonyLib;
using LZ4;

namespace ElinTogether.Helper;

/// <summary>
///     Host setting FastJoinsAndSaves (stage 2 of PLAN_100_players): the periodic save of a big world froze every
///     player for its whole length. The game still makes its save in its own frame (the JSON of the world and of the
///     maps, read from the game's objects); only the files it hands to IO.Compress and IO.WriteLZ4 are compressed and
///     written beside the game. Whatever reads or copies the save meanwhile (a load, a backup, a map sent to a player)
///     waits for those writes first
/// </summary>
[HarmonyPatch]
[AlwaysPatched]
internal static class SaveWriter
{
    // the writes of the save being made, on the game's thread only: another thread's IO.Compress is never put off
    [ThreadStatic]
    private static List<Action>? _deferred;

    private static STask _pending = STask.CompletedTask;

    // a file of the last save beside the game could not be written: the next save is made in the game's frame again,
    // where a failure is told as the game tells it ("error_save")
    private static volatile bool _failed;

    /// <summary>
    ///     Game.Save(true, true), its files written beside the game when the host setting allows it. Not for a Steam
    ///     Cloud save (the game copies it to the cloud folder right after) nor while the save depot holds the world
    ///     (it copies the save right after it is made)
    /// </summary>
    internal static bool Save(Game game)
    {
        if (_failed) {
            Wait();
            _failed = false;
            return game.Save(true, true);
        }

        if (!EmpConfig.Server.FastJoinsAndSaves.Value || game.isCloud || SaveDepot.Holding || _deferred is not null) {
            return game.Save(true, true);
        }

        var watch = Stopwatch.StartNew();
        var writes = _deferred = [];
        bool saved;
        try {
            saved = game.Save(true, true);
        } finally {
            _deferred = null;
        }

        // (a save that failed halfway still writes what the game gave, as it would have in its frame)
        if (writes.Count > 0) {
            var before = _pending;
            _pending = STask.Run(() => {
                // in the order the game wrote them, behind the save before
                WaitFor(before);
                var worker = Stopwatch.StartNew();
                foreach (var write in writes) {
                    try {
                        write();
                    } catch (Exception ex) {
                        _failed = true;
                        EmpLog.Error(ex, "Save: a file could not be written beside the game");
                    }
                }

                EmpLog.Information("Save: {Files} file(s) written in {Worker} ms worker", writes.Count, worker.ElapsedMilliseconds);
            });
        }

        EmpLog.Information("Save: made in {Main} ms main thread, its files written beside the game", watch.ElapsedMilliseconds);
        return saved;
    }

    /// <summary>
    ///     The files of the last save are all on the disk (nothing waits to be written)
    /// </summary>
    internal static bool Idle => _pending.IsCompleted;

    /// <summary>
    ///     Inside a save whose files are written beside the game: runs the action on the writer's thread once all of
    ///     them are on the disk, and not at all if one of them failed. False outside such a save (the files are
    ///     written already, the caller runs it itself)
    /// </summary>
    internal static bool AfterWrites(Action action)
    {
        if (_deferred is not { } writes) {
            return false;
        }

        writes.Add(() => {
            if (_failed) {
                EmpLog.Warning("Save: a file was not written, what waited for the save is left for the next one");
                return;
            }

            try {
                action();
            } catch (Exception ex) {
                EmpLog.Error(ex, "Save: what waited for the save failed");
            }
        });
        return true;
    }

    /// <summary>
    ///     The files of the last save are on the disk once this returns
    /// </summary>
    internal static void Wait()
    {
        if (_pending.IsCompleted) {
            return;
        }

        var watch = Stopwatch.StartNew();
        WaitFor(_pending);
        EmpLog.Debug("Save: waited {Ms} ms for its files", watch.ElapsedMilliseconds);
    }

    private static void WaitFor(STask task)
    {
        try {
            task.Wait();
        } catch (Exception) {
            // (told by the task itself)
        }
    }

    // IO.Compress as the game does it, into a file beside then put in its place: a game stopped halfway keeps the
    // last whole save, not a cut one
    private static void WriteText(string path, string text)
    {
        var temp = path + ".emptmp";
        using (var file = new FileStream(temp, FileMode.Create))
        using (var lz4 = new LZ4Stream(file, LZ4StreamMode.Compress))
        using (var writer = new StreamWriter(lz4)) {
            writer.Write(text);
        }

        if (!File.Exists(path)) {
            File.Move(temp, path);
            return;
        }

        try {
            File.Replace(temp, path, null);
        } catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException) {
            File.Copy(temp, path, true);
            File.Delete(temp);
        }
    }

    // IO.WriteLZ4 as the game does it
    private static void WriteBytes(string path, byte[] bytes, IO.Compression compression)
    {
        var data = compression == IO.Compression.LZ4 ? LZ4Codec.Wrap(bytes, 0, bytes.Length) : bytes;
        for (var i = 0; i < 5; i++) {
            try {
                File.WriteAllBytes(path + (i == 0 ? "" : ".b" + i), data);
                return;
            } catch (Exception ex) when (i < 4) {
                EmpLog.Debug(ex, "Save: {Path} not written, next name", path);
            }
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(IO), nameof(IO.Compress), typeof(string), typeof(string))]
    private static bool OnCompress(string path, string text)
    {
        if (_deferred is { } writes) {
            writes.Add(() => WriteText(path, text));
            return false;
        }

        Wait();
        return true;
    }

    // the map deletes the files of its other cell widths right after writing the new one (Map.WriteCellIds): put off
    // behind that write, or a game stopped meanwhile has neither
    [HarmonyPrefix]
    [HarmonyPatch(typeof(IO), nameof(IO.DeleteFile), typeof(string))]
    private static bool OnDeleteFile(string path)
    {
        if (_deferred is { } writes) {
            writes.Add(() => {
                if (File.Exists(path)) {
                    File.Delete(path);
                }
            });
            return false;
        }

        Wait();
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(IO), nameof(IO.WriteLZ4), typeof(string), typeof(byte[]), typeof(IO.Compression))]
    private static bool OnWriteLZ4(string _path, byte[] _bytes, IO.Compression compression)
    {
        if (_deferred is { } writes) {
            // (the game makes a new array for each file it saves: nothing changes it meanwhile)
            writes.Add(() => WriteBytes(_path, _bytes, compression));
            return false;
        }

        Wait();
        return true;
    }

    /// <summary>
    ///     What reads or copies the save waits for its files. Inside the save itself (the game's own backup timer
    ///     copies the folder right after writing it) the files put off so far are written at once, in the game's frame
    /// </summary>
    [HarmonyPatch]
    private static class Readers
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            MethodBase?[] methods = [
                AccessTools.Method(typeof(IO), nameof(IO.IsCompressed), [typeof(string)]),
                AccessTools.Method(typeof(IO), nameof(IO.Decompress), [typeof(string)]),
                AccessTools.Method(typeof(IO), nameof(IO.ReadLZ4), [typeof(string), typeof(int), typeof(IO.Compression)]),
                AccessTools.Method(typeof(IO), nameof(IO.CopyDir), [typeof(string), typeof(string), typeof(Func<string, bool>)]),
                AccessTools.Method(typeof(GameIO), nameof(GameIO.MakeBackup), [typeof(GameIndex), typeof(string)]),
                AccessTools.Method(typeof(GameIO), nameof(GameIO.LoadGame), [typeof(string), typeof(string), typeof(bool)]),
                AccessTools.Method(typeof(Core), nameof(Core.OnApplicationQuit), []),
            ];

            // (a method gone in a game update is a wait less, never a mod that does not load)
            return methods.Where(m => m is not null)!;
        }

        [HarmonyPrefix]
        private static void BeforeRead()
        {
            if (_deferred is { Count: > 0 } writes) {
                Wait();
                foreach (var write in writes) {
                    write();
                }

                writes.Clear();
                return;
            }

            Wait();
        }
    }
}
