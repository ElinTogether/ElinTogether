using System;
using ElinTogether.Models;
using System.Collections;
using System.IO;
using STask = System.Threading.Tasks.Task;

namespace ElinTogether.Net;

/// <summary>
///     Council 9, step 4: a guest opens, as its host, the world it was playing in, from the copy it keeps of it
///     (WorldCopyStore). The copy is read again in full, laid in a save folder of its own (never over a save of
///     the player, nothing is deleted), and loaded as any save: what follows is what a world taken from the
///     depot does (ElinNetHost.TakeOverPc: the player plays its own character, the former host's waits for its
///     player; EmpAutoHost: the session opens by itself) <br />
///     Only started by hand for now (emp.take_over, debug builds): what starts it when a host is lost is another
///     piece of work
/// </summary>
internal static class WorldTakeover
{
    private const string SavePrefix = "world_";

    // the save folder being loaded from a copy, and the handover number of that copy
    private static (string Id, int Handover)? _opening;
    private static bool _busy;

    /// <summary>
    ///     The world is being opened here: nothing tries to join the host that is gone
    /// </summary>
    internal static bool Busy => _busy;

    /// <summary>
    ///     What the last takeover did, for the log and the bench
    /// </summary>
    internal static string State { get; private set; } = "idle";

    /// <returns>empty when started, else why not</returns>
    /// <param name="afterCopy">
    ///     The host just said it leaves: the save it sent last may still be on its way to the disk, the takeover
    ///     waits for it (a few seconds at most)
    /// </param>
    internal static string Begin(bool afterCopy = false)
    {
        var session = NetSession.Instance;
        if (_busy) {
            return "a takeover is already under way";
        }

        if (session.Transport is ElinNetHost) {
            return "this game hosts already";
        }

        // a world with admins (admin tools, council 2026-10-27) is taken over by one of them only: whoever takes it
        // over gets the host's powers, bans included
        ulong me = session.Self?.User ?? HeathenEngineering.SteamworksIntegration.UserData.Me;
        // a peer-hosted world: whoever keeps it next has no power for it (it is a guest of its hidden server), so
        // anyone the signed lists do not ban may (lead dev 2026-11-01: everyone, right away; the store's lease and
        // eligibility come with P5), and the admin list the former keeper told is not asked
        if (Helper.PeerHosting.Active) {
            if (Helper.PeerHosting.IsBanned(me)) {
                return "banned from this world by its signed list";
            }
        } else if (ModerationDelta.AdminIds.Count > 0 && !ModerationDelta.AdminIds.Contains(me)) {
            return "only an admin of this world takes it over";
        }

        // a game of the player's own is never left for this
        if (EClass.core.IsGameStarted && session.Transport is null) {
            return "another game is being played";
        }

        if (!afterCopy && WorldHandover.Mine() is null) {
            return "no whole copy of the world";
        }

        _busy = true;
        State = "reading the copy";
        EmpMod.Instance.StartCoroutine(Run(afterCopy, CorePath.RootSave));
        return "";
    }

    private const float CopyWaitSeconds = 20f;

    private static IEnumerator Run(bool afterCopy, string saves)
    {
        var id = "";
        try {
            // every part the host sent came in before its word that it leaves: only the check of the files is left
            var until = UnityEngine.Time.realtimeSinceStartup + CopyWaitSeconds;
            while (afterCopy && WorldCopyReceiver.IsClosing && UnityEngine.Time.realtimeSinceStartup < until) {
                yield return null;
            }

            if (WorldHandover.Mine() is not { } copy) {
                EmpLog.Warning("World not taken over, nothing changed: {Why}", "no whole copy of the world");
                State = "failed: no whole copy of the world";
                yield break;
            }

            // the game goes on (or waits at the title) while the files are read and written
            id = GameIO.GetNewId(saves, SavePrefix);
            var laying = STask.Run(() => Lay(copy, Path.Combine(saves, id)));
            while (!laying.IsCompleted) {
                yield return null;
            }

            if (laying.IsFaulted || laying.Result is { Length: > 0 }) {
                var why = laying.IsFaulted ? laying.Exception!.GetBaseException().Message : laying.Result;
                EmpLog.Warning("World not taken over, nothing changed: {Why}", why);
                State = "failed: " + why;
                yield break;
            }

            EmpLog.Information("Taking the world over from the copy {Copy}, as the save {Id}", copy.ToString(), id);

            // out of the game of the host that is gone, and no more attempts to join it again
            var session = NetSession.Instance;
            NetReconnect.Stop();
            if (session.Transport is not null) {
                session.ResetSession();
            }

            // the title screen is up on the next frame
            yield return null;
            yield return null;

            if (EClass.core.IsGameStarted || session.Transport is not null) {
                // the player started something else meanwhile: the save stays, to be loaded by hand
                State = "failed: another game was started, the world is the save " + id;
                yield break;
            }

            State = "loading " + id;
            _opening = (id, copy.Handover);
            EClass.ui.RemoveLayers();
            LayerTitle.KillActor();
            Game.Load(id, false);
        } finally {
            _busy = false;
        }
    }

    /// <summary>
    ///     Off the game's thread: every file of the copy is read again, then written beside the saves and read once
    ///     more there; the folder gets its name in one rename, so a save folder is whole or is not there
    /// </summary>
    /// <returns>empty when laid, else why not</returns>
    internal static string Lay(WorldHandover.Copy copy, string target)
    {
        var (dir, manifest) = WorldHandover.Read(copy);
        if (manifest is null || manifest.Incomplete || !WorldCopyStore.Verify(dir, manifest)) {
            return "the copy is not whole";
        }

        if (Directory.Exists(target)) {
            return "the save folder exists";
        }

        var laying = target + ".emp";
        try {
            if (Directory.Exists(laying)) {
                Directory.Delete(laying, true);
            }

            foreach (var file in manifest.Files) {
                var path = WorldCopyStore.ToLocal(laying, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Copy(WorldCopyStore.ToLocal(dir, file.Path), path);
            }

            if (!WorldCopyStore.Verify(laying, manifest)) {
                throw new InvalidDataException("the save written is not the copy");
            }

            // (its name is the game's own "world_N": the marker says it is another game's world, see PeerMapPatch)
            File.WriteAllText(Path.Combine(laying, Patches.PeerMapPatch.PeerDataMarker), "a world taken over from another game\n");
            Directory.Move(laying, target);
            return "";
        } catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) {
            try {
                if (Directory.Exists(laying)) {
                    Directory.Delete(laying, true);
                }
            } catch (Exception again) when (again is IOException or UnauthorizedAccessException) {
                // noexcept
            }

            return ex.Message;
        }
    }

    /// <summary>
    ///     A world was just loaded and the tables of its save are read. True when it is the one being taken over:
    ///     the caller then raises its handover number, once (see ElinNetHost.RemoveLeftOverCharas)
    /// </summary>
    internal static bool Opened(out int handover)
    {
        handover = 0;
        if (_opening is not { } opening) {
            return false;
        }

        _opening = null;
        if (opening.Id != Game.id) {
            State = "failed: " + Game.id + " was loaded instead of " + opening.Id;
            return false;
        }

        handover = opening.Handover;
        State = "done " + opening.Id;
        return true;
    }
}
