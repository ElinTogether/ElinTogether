using System.Linq;
using ElinTogether.Components;
using HarmonyLib;
using UnityEngine;

namespace ElinTogether.Patches;

/// <summary>
///     The "Multiplayer" button in the main-menu row of the HUD (hotbar 2: backpack, abilities, character, journal).
///     The item is a vanilla HotItemLayer (the only kind a save knows), so without the mod the next zone activation
///     rebuilds the bar and the save stays vanilla; the click is taken over in a prefix because vanilla would look
///     for a layer prefab of that name.
/// </summary>
[HarmonyPatch]
[AlwaysPatched]
internal static class MultiplayerHubPatch
{
    internal const string LayerId = "LayerElinTogether";

    private static Sprite? _icon;
    private static Sprite? _logo;
    private static bool _logoLooked;

    /// <summary>
    ///     The mod's logo, two putits in pixel art (package/Texture/emp_logo.png, next to the DLL as LangMod is),
    ///     loaded once with point filtering; null when the file is missing
    /// </summary>
    internal static Sprite? Logo {
        get {
            if (_logoLooked) {
                return _logo;
            }

            _logoLooked = true;
            try {
                var dir = System.IO.Path.GetDirectoryName(EmpMod.Assembly.Location) ?? "";
                var path = System.IO.Path.Combine(dir, "Texture", "emp_logo.png");
                if (!System.IO.File.Exists(path)) {
                    EmpLog.Debug("No logo at {Path}", path);
                    return null;
                }

                var tex = IO.LoadPNG(path, FilterMode.Point);
                tex.wrapMode = TextureWrapMode.Clamp;
                _logo = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f, 0,
                    SpriteMeshType.FullRect);
                _logo.name = "emp_logo";
            }
            catch (System.Exception e) {
                EmpLog.Warning(e, "Could not load the logo");
            }

            return _logo;
        }
    }

    /// <summary>A putit, from the game's own putit sprite (null if unavailable).</summary>
    internal static Sprite? Icon {
        get {
            if (_icon != null) {
                return _icon;
            }

            try {
                _icon = BuildIcon();
                if (_icon != null) {
                    SpriteSheet.Add(_icon);
                }
            }
            catch (System.Exception e) {
                EmpLog.Warning(e, "Could not build the multiplayer icon");
            }

            return _icon;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(HotbarManager), nameof(HotbarManager.ResetHotbar))]
    private static void AddButton(HotbarManager __instance, int id)
    {
        if (id != Hotbar.IDMainMenu || __instance.bars[id] is not { } bar) {
            return;
        }

        _ = Icon;
        bar.SetItem(new HotItemLayer { id = LayerId });
        // (vanilla already rebuilt the row once, before this item existed)
        if (bar.actor != null) {
            bar.actor.Rebuild();
        }
    }

    // a save made before this mod (or before this button) keeps the row it was saved with: ResetHotbar only runs for a
    // new game, so the button is put in the row's first free slot when the game loads
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.OnLoad))]
    private static void AddButtonOnLoad(Player __instance)
    {
        if (__instance.hotbars?.bars is not { } bars || bars.Length <= Hotbar.IDMainMenu || bars[Hotbar.IDMainMenu] is not { } bar) {
            return;
        }

        foreach (var page in bar.pages) {
            if (page.items.Any(i => i is HotItemLayer { id: LayerId })) {
                return;
            }
        }

        _ = Icon;
        bar.SetItem(new HotItemLayer { id = LayerId });
        if (!bar.pages.Any(p => p.items.Any(i => i is HotItemLayer { id: LayerId }))) {
            EmpLog.Warning("No free slot in the main menu bar for the Multiplayer button");
        }

        if (bar.actor != null) {
            bar.actor.Rebuild();
        }
    }

    // a widget with a fixed slot count (its own setting, kept in the player's widget layout and not in the save)
    // drew only the first 4 slots on another PC, so the button sat in the bar but out of sight (user 2026-10-29):
    // the row grows to show it, as the player would with the widget's "numSlot" slider
    [HarmonyPrefix]
    [HarmonyPatch(typeof(WidgetHotbar), nameof(WidgetHotbar.Rebuild))]
    private static void ShowButton(WidgetHotbar __instance)
    {
        if (__instance.hotbar is not { id: Hotbar.IDMainMenu } bar || __instance.extra is not { autoSize: false } extra) {
            return;
        }

        var index = bar.CurrentPage.items.FindIndex(i => i is HotItemLayer { id: LayerId });
        if (index >= extra.rows) {
            extra.rows = index + 1;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(HotItemLayer), nameof(HotItemLayer.OnClick), typeof(ButtonHotItem), typeof(Hotbar))]
    private static bool OnClick(HotItemLayer __instance)
    {
        if (__instance.id != LayerId) {
            return true;
        }

        Toggle();
        return false;
    }

    internal static void Toggle()
    {
        if (EClass.ui.GetLayer<LayerElinTogether>() is { } open) {
            EClass.ui.RemoveLayer(open);
        }
        else {
            LayerElinTogether.OpenPanelSesame();
        }
    }

    private static Sprite? BuildIcon()
    {
        // the mod's logo, two putits (user, 2026-10-28); the game's single putit when the file is missing
        var logo = Logo;
        var putit = logo ?? EClass.sources.charas.map.TryGetValue("putty")?.GetSprite();
        if (putit == null) {
            return null;
        }

        // same pixel size and PPU as the journal button, so SetNativeSize gives the same footprint
        var model = SpriteSheet.Get("icon_LayerJournal");
        var size = model != null ? Mathf.RoundToInt(Mathf.Max(model.rect.width, model.rect.height)) : 48;
        var ppu = model != null ? model.pixelsPerUnit : 100f;

        var src = Read(putit);
        var box = AlphaBox(src.Pixels, src.W, src.H);
        // centred, about the footprint of the game's own icons (one putit at 0.85 looked too big next to them)
        var small = Mathf.RoundToInt(size * (logo != null ? 0.75f : 0.6f));
        var scale = (float)small / Mathf.Max(box.width, box.height);
        var w = Mathf.Max(1, Mathf.RoundToInt(box.width * scale));
        var h = Mathf.Max(1, Mathf.RoundToInt(box.height * scale));

        var outPx = new Color32[size * size];
        Draw(outPx, size, src, box, (size - w) / 2, (size - h) / 2, w, h);

        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            name = "icon_" + LayerId,
        };
        tex.SetPixels32(outPx);
        tex.Apply(false, false);
        var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), ppu, 0,
            SpriteMeshType.FullRect);
        sprite.name = "icon_" + LayerId;
        return sprite;
    }

    internal static (Color32[] Pixels, int W, int H) Read(Sprite sprite)
    {
        // the sprite's atlas is not necessarily readable: copy the rectangle through a render texture
        var r = sprite.textureRect;
        var tex = sprite.texture;
        var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
        var previous = RenderTexture.active;
        try {
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            var copy = new Texture2D((int)r.width, (int)r.height, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(r.x, r.y, r.width, r.height), 0, 0);
            copy.Apply();
            var pixels = copy.GetPixels32();
            Object.Destroy(copy);
            return (pixels, (int)r.width, (int)r.height);
        }
        finally {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
        }
    }

    internal static RectInt AlphaBox(Color32[] px, int w, int h)
    {
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (var y = 0; y < h; y++) {
            for (var x = 0; x < w; x++) {
                if (px[y * w + x].a < 40) {
                    continue;
                }

                x0 = Mathf.Min(x0, x);
                x1 = Mathf.Max(x1, x);
                y0 = Mathf.Min(y0, y);
                y1 = Mathf.Max(y1, y);
            }
        }

        return x1 < 0 ? new RectInt(0, 0, w, h) : new RectInt(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    /// <summary>Nearest-neighbour copy of the box into the target at (ox, oy), opaque pixels only.</summary>
    private static void Draw(Color32[] dst, int size, (Color32[] Pixels, int W, int H) src, RectInt box, int ox, int oy,
        int w, int h)
    {
        for (var y = 0; y < h; y++) {
            for (var x = 0; x < w; x++) {
                var sx = box.x + Mathf.Min(box.width - 1, x * box.width / w);
                var sy = box.y + Mathf.Min(box.height - 1, y * box.height / h);
                var c = src.Pixels[sy * src.W + sx];
                if (c.a < 40) {
                    continue;
                }

                c.a = 255;
                dst[(oy + y) * size + ox + x] = c;
            }
        }
    }
}
