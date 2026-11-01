using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace ElinTogether.Helper;

/// <summary>
///     The last seconds of the screen, for the bug report (user, 2026-10-27): three pictures a second, the last 30
///     seconds kept in memory, sent as a video with the logs. Players rarely record when the bug happens; this did it
///     for them. The picture is made small on the graphics card (one blit, one read), off with Client.RecordReplay
/// </summary>
internal static class ReplayBuffer
{
    // three pictures a second for 30 seconds: a video (user, 2026-10-27)
    private const int Fps = 3;
    private const int Frames = 30 * Fps;
    private const float Every = 1f / Fps;
    // (sharp enough to read the game's messages and windows: the reports are read by the developers, and by Claude)
    private const int Width = 960;
    private const int Quality = 60;

    private static readonly Queue<(DateTime At, byte[] Jpg)> _frames = new();

    /// <summary>
    ///     How many open pages hold the recording (the bug report page: user 2026-10-30, "the last 30 seconds only showed
    ///     the player writing the report"); the frames before it are kept as they are
    /// </summary>
    internal static int PausedBy;

    /// <summary>Put on a page: the recording stops while it is shown, and goes on when it is closed</summary>
    internal sealed class Hold : MonoBehaviour
    {
        private void OnEnable() => PausedBy++;

        private void OnDisable() => PausedBy = Math.Max(0, PausedBy - 1);
    }
    private static RenderTexture? _small;
    private static Texture2D? _read;

    internal static IEnumerator Run()
    {
        // a dedicated server has no screen to film and nobody reports a bug from it (memory audit 2026-11-01)
        if (EmpServer.Requested) {
            yield break;
        }

        var endOfFrame = new WaitForEndOfFrame();
        var wait = new WaitForSecondsRealtime(Every);
        while (true) {
            yield return wait;
            // (paused while the bug report page is open: the video shows what happened, not the report being typed)
            if (!EmpConfig.Client.RecordReplay.Value || !EClass.core.IsGameStarted || PausedBy > 0) {
                continue;
            }

            yield return endOfFrame;
            try {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                Capture();
                _spent += clock.Elapsed.TotalMilliseconds;
                _taken++;
            } catch (Exception ex) {
                // (never the game's frame for a picture: one failure is told, and it stops)
                EmpLog.Warning(ex, "The replay for bug reports could not take a picture, it stops");
                yield break;
            }
        }
    }

    // pictures asked of the graphics card and not yet written: past two, the next one waits (a slow card, a game
    // in the background)
    private const int MaxPending = 2;
    private static int _pending;
    private static int _width, _height;

    private static void Capture()
    {
        if (Volatile.Read(ref _pending) >= MaxPending) {
            return;
        }

        var height = Mathf.Max(1, Width * Screen.height / Mathf.Max(1, Screen.width));
        var full = RenderTexture.GetTemporary(Screen.width, Screen.height, 0);
        try {
            ScreenCapture.CaptureScreenshotIntoRenderTexture(full);
            if (_small is null || _small.height != height) {
                if (_small is not null) {
                    _small.Release();
                }

                _small = new(Width, height, 0);
                _read = new(Width, height, TextureFormat.RGB24, false);
            }

            // (the capture comes upside down on Direct3D: flipped in the same blit)
            Graphics.Blit(full, _small, new Vector2(1, -1), new Vector2(0, 1));
            KeepGameScreen(full);
        } finally {
            RenderTexture.ReleaseTemporary(full);
        }

        (_width, _height) = (Width, height);
        var at = DateTime.Now;
        if (!SystemInfo.supportsAsyncGPUReadback) {
            // (an old card: read and made here, at the cost of the frame)
            var before = RenderTexture.active;
            RenderTexture.active = _small;
            _read!.ReadPixels(new(0, 0, Width, height), 0, 0, false);
            RenderTexture.active = before;
            Keep(at, _read.EncodeToJPG(Quality));
            return;
        }

        // the game's frame only asks: the card hands the picture over a few frames later, and the JPEG is made on
        // another thread (36 ms a picture on the frame when read and made here, bench 2026-10-27)
        Interlocked.Increment(ref _pending);
        AsyncGPUReadback.Request(_small, 0, TextureFormat.RGBA32, request => {
            if (request.hasError) {
                Interlocked.Decrement(ref _pending);
                return;
            }

            var (rgba, w, h) = (request.GetData<byte>().ToArray(), (uint)request.width, (uint)request.height);
            ThreadPool.QueueUserWorkItem(_ => {
                try {
                    Keep(at, ImageConversion.EncodeArrayToJPG(rgba, GraphicsFormat.R8G8B8A8_SRGB, w, h, 0, Quality));
                } catch (Exception ex) {
                    EmpLog.Debug(ex, "A replay picture could not be made");
                } finally {
                    Interlocked.Decrement(ref _pending);
                }
            });
        });
    }

    // ---------------------------------------------------------------- the picture of the game screen (user 2026-10-30)

    // the game screen as last seen with the mod's window closed, sharp (up to PictureWidth wide), kept on the card:
    // one blit a picture, read only when a report takes it. The report is opened from that window, so its own
    // last frames show the window, not the game
    private const int PictureWidth = 2560;
    private const int PictureQuality = 85;
    private static RenderTexture? _game;
    private static DateTime _gameAt;

    private static void KeepGameScreen(RenderTexture full)
    {
        if (ElinTogether.Components.LayerElinTogether.Instance is not null) {
            return;
        }

        var width = Mathf.Min(full.width, PictureWidth);
        var height = Mathf.Max(1, width * full.height / Mathf.Max(1, full.width));
        if (_game is null || _game.width != width || _game.height != height) {
            if (_game is not null) {
                _game.Release();
            }

            _game = new(width, height, 0);
        }

        Graphics.Blit(full, _game, new Vector2(1, -1), new Vector2(0, 1));
        _gameAt = DateTime.Now;
    }

    /// <summary>Whether a report has a picture of the game screen to take</summary>
    internal static bool HasGamePicture => _game is not null || HasFrames;

    /// <summary>How many pictures the video holds (the server's memory log)</summary>
    internal static int FrameCount
    {
        get {
            lock (_frames) {
                return _frames.Count;
            }
        }
    }

    /// <summary>Whether the video has a picture</summary>
    internal static bool HasFrames
    {
        get {
            lock (_frames) {
                return _frames.Count > 0;
            }
        }
    }

    /// <summary>
    ///     The game screen just before the mod's window was opened, as a JPEG (on the game's thread); the last frame
    ///     of the video when there is none; null when nothing was recorded (Client.RecordReplay off)
    /// </summary>
    internal static byte[]? GamePicture()
    {
        if (_game is not null && _game.IsCreated()) {
            var before = RenderTexture.active;
            var read = new Texture2D(_game.width, _game.height, TextureFormat.RGB24, false);
            try {
                RenderTexture.active = _game;
                read.ReadPixels(new(0, 0, _game.width, _game.height), 0, 0, false);
                read.Apply(false);
                return read.EncodeToJPG(PictureQuality);
            } catch (Exception ex) {
                EmpLog.Debug(ex, "The game picture of {At} could not be read", _gameAt);
            } finally {
                RenderTexture.active = before;
                UnityEngine.Object.Destroy(read);
            }
        }

        lock (_frames) {
            return _frames.Count == 0 ? null : _frames.Last().Jpg;
        }
    }

    private static void Keep(DateTime at, byte[] jpg)
    {
        lock (_frames) {
            _frames.Enqueue((at, jpg));
            while (_frames.Count > Frames) {
                _frames.Dequeue();
            }
        }
    }

    /// <summary>
    ///     The pictures kept as a video file (AVI, Motion JPEG), null without pictures
    /// </summary>
    internal static byte[]? Video()
    {
        List<byte[]> jpegs;
        lock (_frames) {
            jpegs = _frames.Select(f => f.Jpg).ToList();
        }

        return jpegs.Count == 0 ? null : AviWriter.Write(_width, _height, Fps, jpegs);
    }

    /// <summary>
    ///     How long a picture takes on the game's frame, on average (ms): for the bench
    /// </summary>
    internal static double AverageMs => _taken == 0 ? 0 : _spent / _taken;

    private static double _spent;
    private static int _taken;

    /// <summary>
    ///     The pictures kept, oldest first, named by their time
    /// </summary>
    internal static List<(string Name, byte[] Jpg)> Snapshot()
    {
        var list = new List<(string, byte[])>();
        lock (_frames) {
            foreach (var (at, jpg) in _frames) {
                list.Add(($"replay/{at:HH-mm-ss-fff}.jpg", jpg));
            }
        }

        return list;
    }
}
