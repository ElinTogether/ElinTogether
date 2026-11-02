using System.Collections.Generic;
using UnityEngine;

namespace ElinTogether.Components;

/// <summary>
///     The emblem editor's small pixel icons (lead dev 2026-11-02: the align row's "|&lt; T _ ^v" and the layer row's
///     "o u" read as nothing): drawn once in the ink of the game's list arrows, 16 px, point filtered, shown in the
///     game's own small list button (<see cref="EmpUi.IconButton" />) with a hover text
/// </summary>
internal static class EditorIcons
{
    private const int N = 16;

    private static readonly Color32 Ink = new(92, 76, 58, 255);
    private static readonly Dictionary<string, Sprite> _made = [];

    private static readonly string[] EyeArt = [
        "................",
        "................",
        "................",
        ".....######.....",
        "...##......##...",
        "..#....##....#..",
        ".#....####....#.",
        "#.....####.....#",
        ".#....####....#.",
        "..#....##....#..",
        "...##......##...",
        ".....######.....",
        "................",
        "................",
        "................",
        "................",
    ];

    private static readonly string[] LockArt = [
        "................",
        "......####......",
        ".....#....#.....",
        "....#......#....",
        "....#......#....",
        "....#......#....",
        "...##########...",
        "...##########...",
        "...####..####...",
        "...####..####...",
        "...#####.####...",
        "...##########...",
        "...##########...",
        "...##########...",
        "................",
        "................",
    ];

    private static readonly string[] UnlockArt = [
        "......####......",
        ".....#....#.....",
        "....#......#....",
        "....#...........",
        "....#...........",
        "....#...........",
        "...##########...",
        "...#........#...",
        "...#...##...#...",
        "...#...##...#...",
        "...#....#...#...",
        "...#........#...",
        "...##########...",
        "................",
        "................",
        "................",
    ];

    internal static Sprite Eye => Art("eye", EyeArt);

    /// <summary>The eye crossed out: the layer is hidden</summary>
    internal static Sprite EyeShut => Make("eye_shut", px => {
        Paint(px, EyeArt);
        for (var i = 2; i <= 13; i++) {
            px[i, 15 - i] = Ink;
            px[i, 14 - i] = Ink;
        }
    });

    internal static Sprite Lock => Art("lock", LockArt);

    internal static Sprite Unlock => Art("unlock", UnlockArt);

    /// <summary>
    ///     Align: 0 left, 1 centres, 2 right, 3 top, 4 middles, 5 bottom (a line and two bars set against it); 6 spread
    ///     across, 7 spread down (two edges and two boxes between them)
    /// </summary>
    internal static Sprite Align(int mode) => Make("align" + mode, px => {
        // drawn as the horizontal forms, turned for top, middle, bottom and spread down
        var across = mode is < 3 or 6;
        void Box(int x, int y, int w, int h)
        {
            for (var yy = y; yy < y + h; yy++) {
                for (var xx = x; xx < x + w; xx++) {
                    if (across) {
                        px[xx, yy] = Ink;
                    } else {
                        px[yy, xx] = Ink;
                    }
                }
            }
        }

        switch (mode % 3 == 0 && mode < 6 ? 0 : mode is 1 or 4 ? 1 : mode is 2 or 5 ? 2 : 3) {
            case 0:
                Box(1, 1, 1, 14);
                Box(3, 3, 10, 3);
                Box(3, 10, 6, 3);
                break;
            case 1:
                Box(7, 1, 2, 14);
                Box(2, 3, 12, 3);
                Box(4, 10, 8, 3);
                break;
            case 2:
                Box(14, 1, 1, 14);
                Box(3, 3, 10, 3);
                Box(7, 10, 6, 3);
                break;
            default:
                Box(0, 1, 1, 14);
                Box(15, 1, 1, 14);
                Box(3, 4, 4, 8);
                Box(9, 4, 4, 8);
                break;
        }
    });

    // ---------------------------------------------------------------- the paint tools

    private static readonly string[] MoveArt = [
        "................",
        ".......#........",
        "......###.......",
        ".....#####......",
        ".......#........",
        "..#....#....#...",
        ".##....#....##..",
        "###############.",
        ".##....#....##..",
        "..#....#....#...",
        ".......#........",
        ".....#####......",
        "......###.......",
        ".......#........",
        "................",
        "................",
    ];

    private static readonly string[] PencilArt = [
        "................",
        "...........##...",
        "..........#..#..",
        ".........#..##..",
        "........#..#.#..",
        ".......#..#.#...",
        "......#..#.#....",
        ".....#..#.#.....",
        "....#..#.#......",
        "...#..#.#.......",
        "...#.#.#........",
        "..#.##..........",
        "..###...........",
        "..##............",
        "................",
        "................",
    ];

    private static readonly string[] BrushArt = [
        "................",
        "............##..",
        "...........###..",
        "..........###...",
        ".........###....",
        "........###.....",
        ".......###......",
        "......##........",
        "....###.........",
        "...####.........",
        "..#####.........",
        "..####..........",
        ".###............",
        "##..............",
        "................",
        "................",
    ];

    private static readonly string[] EraserArt = [
        "................",
        "................",
        "........#####...",
        ".......#....##..",
        "......#....#.#..",
        ".....#....#..#..",
        "....#....#..#...",
        "...######..#....",
        "...######.#.....",
        "...######.......",
        "...#####........",
        "................",
        "..############..",
        "................",
        "................",
        "................",
    ];

    private static readonly string[] BucketArt = [
        "................",
        "......#.........",
        ".....#.#........",
        "....#...#.......",
        "...#.....#......",
        "..#########.....",
        "..#########.#...",
        "...#######..##..",
        "....#####...##..",
        ".....###....##..",
        "......#.........",
        "................",
        "................",
        "................",
        "................",
        "................",
    ];

    private static readonly string[] PickerArt = [
        "................",
        "...........###..",
        "..........#####.",
        "..........#####.",
        ".........#####..",
        "........#.###...",
        ".......#.#......",
        "......#.#.......",
        ".....#.#........",
        "....#.#.........",
        "...#.#..........",
        "..#.#...........",
        "..##............",
        ".#..............",
        "................",
        "................",
    ];

    private static readonly string[] TextArt = [
        "................",
        "..############..",
        "..############..",
        "..##...##...##..",
        ".......##.......",
        ".......##.......",
        ".......##.......",
        ".......##.......",
        ".......##.......",
        ".......##.......",
        ".......##.......",
        "......####......",
        "................",
        "................",
        "................",
        "................",
    ];

    private static readonly string[] SwapArt = [
        "................",
        "....#...........",
        "...##...........",
        "..##########....",
        "...##......#....",
        "....#......#....",
        "...........#....",
        "....#......#....",
        "....#......#....",
        "....#...........",
        "....#......#....",
        "....##########..",
        "...........##...",
        "...........#....",
        "................",
        "................",
    ];

    /// <summary>The paint tools' icons, by the tool's number (see the editor's tool list)</summary>
    internal static Sprite Tool(int tool) => tool switch {
        0 => Art("move", MoveArt),
        1 => Art("pencil", PencilArt),
        2 => Art("brush", BrushArt),
        3 => Art("eraser", EraserArt),
        4 => Art("bucket", BucketArt),
        5 => Make("line", px => {
            for (var i = 2; i <= 13; i++) {
                px[i, 15 - i] = Ink;
                px[i, 16 - i] = Ink;
            }
        }),
        6 => Make("rect", px => {
            for (var i = 2; i <= 13; i++) {
                px[i, 3] = px[i, 12] = Ink;
            }

            for (var y = 3; y <= 12; y++) {
                px[2, y] = px[13, y] = Ink;
            }
        }),
        7 => Make("ellipse", px => {
            for (var a = 0; a < 96; a++) {
                var t = a * Mathf.PI * 2f / 96f;
                px[Mathf.RoundToInt(7.5f + 6f * Mathf.Cos(t)), Mathf.RoundToInt(7.5f + 5f * Mathf.Sin(t))] = Ink;
            }
        }),
        8 => Art("picker", PickerArt),
        10 => Make("select", px => {
            // (a dashed rectangle, the marquee)
            for (var i = 2; i <= 13; i++) {
                if (i % 3 != 1) {
                    px[i, 3] = px[i, 12] = Ink;
                    px[2, i - 1 < 3 ? 3 : i > 12 ? 12 : i] = px[13, i - 1 < 3 ? 3 : i > 12 ? 12 : i] = Ink;
                }
            }
        }),
        _ => Art("text", TextArt),
    };

    /// <summary>The pixel grid on or off</summary>
    internal static Sprite Grid => Make("grid", px => {
        for (var i = 1; i <= 14; i++) {
            foreach (var at in new[] { 1, 5, 10, 14 }) {
                px[i, at] = Ink;
                px[at, i] = Ink;
            }
        }
    });

    private static readonly string[] MagnetArt = [
        "................",
        "..####....####..",
        "..####....####..",
        "................",
        "..##........##..",
        "..##........##..",
        "..##........##..",
        "..##........##..",
        "..###......###..",
        "...###....###...",
        "....########....",
        ".....######.....",
        "................",
        "................",
        "................",
        "................",
    ];

    /// <summary>Snap to the grid on or off</summary>
    internal static Sprite Magnet => Art("magnet", MagnetArt);

    /// <summary>The two colours swapped</summary>
    internal static Sprite Swap => Art("swap", SwapArt);

    private static Sprite Art(string key, string[] art) => Make(key, px => Paint(px, art));

    private static void Paint(Color32[,] px, string[] art)
    {
        for (var y = 0; y < N; y++) {
            for (var x = 0; x < N; x++) {
                if (art[y][x] == '#') {
                    px[x, y] = Ink;
                }
            }
        }
    }

    /// <summary>A 16 px sprite painted top-down by <paramref name="draw" /> (px[x, y]), made once</summary>
    private static Sprite Make(string key, System.Action<Color32[,]> draw)
    {
        if (_made.TryGetValue(key, out var known) && known != null) {
            return known;
        }

        var px = new Color32[N, N];
        draw(px);
        var flat = new Color32[N * N];
        for (var y = 0; y < N; y++) {
            for (var x = 0; x < N; x++) {
                flat[(N - 1 - y) * N + x] = px[x, y];
            }
        }

        var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            name = "emp_icon_" + key,
        };
        tex.SetPixels32(flat);
        tex.Apply(false, true);
        return _made[key] = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
    }
}
