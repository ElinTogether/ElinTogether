using System;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YKF;
using Object = UnityEngine.Object;

namespace ElinTogether.Components;

/// <summary>
///     A picture of the chat (ChatImages) shown larger in its own window (user 2026-11-03): the chat log and the pop
///     text show only the image, a click on it opens this. The window shows the image as large as fits (whole steps,
///     nearest filter), the drawing's title and its creator's credit (lead dev 2026-11-02: a drawing always stays
///     credited to its creator), who sent it, Save (a drawing, into My drawings) and Close
/// </summary>
internal static class ChatPictureView
{
    /// <summary>
    ///     A texture of the picture, point filtered, that the caller destroys; null when it cannot be made (a PNG
    ///     checked again here, a drawing baked as the log does)
    /// </summary>
    internal static Texture2D? Make(byte[]? png, Drawing? drawing)
    {
        if (drawing != null) {
            try {
                return Drawing.Bake(drawing);
            } catch (Exception e) {
                EmpLog.Debug(e, "Chat: a drawing of {W}x{H} could not be baked", drawing.Art.W, drawing.Art.H);
                return null;
            }
        }

        if (!ChatImages.IsValid(png)) {
            return null;
        }

        var texture = new Texture2D(2, 2);
        if (!texture.LoadImage(png)) {
            Object.Destroy(texture);
            return null;
        }

        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;
        return texture;
    }

    /// <summary>
    ///     The scale that draws a picture as large as fits <paramref name="max" /> on its longer side: whole steps
    ///     (each pixel a square of the same size) when it fits at least once, else the share that fits
    /// </summary>
    internal static float Fit(Texture texture, float max, float most = float.MaxValue)
    {
        var scale = max / Mathf.Max(1, Mathf.Max(texture.width, texture.height));
        return scale >= 1f ? Mathf.Min(most, Mathf.Floor(scale)) : scale;
    }

    /// <summary>A click on the image (left button) opens the window; the wheel and a drag still scroll the log under it</summary>
    internal static void Clickable(Graphic image, Action open)
    {
        image.raycastTarget = true;
        image.gameObject.AddComponent<ChatPictureClick>().Open = open;
    }

    /// <summary>Puts the picture in the window (the one shown before is closed)</summary>
    internal static void Open(byte[]? png, Drawing? drawing, string sender) => LayerChatPicture.Open(png, drawing, sender);
}

/// <summary>
///     The left click on a chat picture. It takes no scroll nor drag: those go up to the log's ScrollRect, so the log
///     still scrolls with the wheel or a drag over the image (and a drag is never taken for a click)
/// </summary>
internal sealed class ChatPictureClick : MonoBehaviour, IPointerClickHandler
{
    internal Action? Open;

    public void OnPointerClick(PointerEventData eventData)
    {
        if (eventData.button != PointerEventData.InputButton.Left || eventData.dragging || Open == null) {
            return;
        }

        SE.Click();
        Open();
    }
}

/// <summary>
///     The textures and sprites a pop text line with a picture made: freed with the line (the pop text destroys a
///     line when it fades out). Counts the lines alive, so a flood of pictures never holds more than a few
/// </summary>
internal sealed class PopPictureOwner : MonoBehaviour
{
    internal static int Alive;
    private Object[] _owned = [];

    internal void Own(params Object[] owned)
    {
        _owned = owned;
        Alive++;
    }

    private void OnDestroy()
    {
        Alive--;
        foreach (var o in _owned) {
            if (o != null) {
                Destroy(o);
            }
        }
    }
}

internal sealed class LayerChatPicture : YKLayer<LayerCreationData>
{
    internal const string TabId = "emp_tab_chat_picture";

    // what the next window shows, set by Open and taken by the window as it is built (the texture is then its own)
    private static Texture2D? _pending;
    private static Drawing? _pendingDrawing;
    private static string _pendingSender = "";
    private static Vector2 _pendingSize;
    private static float _pendingScale = 1f;

    internal static LayerChatPicture? Instance { get; private set; }

    internal Texture2D? Picture { get; private set; }
    internal Drawing? Shown { get; private set; }
    internal string Sender { get; private set; } = "";
    internal float Scale { get; private set; } = 1f;

    public override string Title => "emp_ui_chat_view_title".lang();

    public override Rect Bound => new(Vector2.zero, _pendingSize);

    internal static void Open(byte[]? png, Drawing? drawing, string sender)
    {
        if (ChatPictureView.Make(png, drawing) is not { } texture) {
            return;
        }

        // (one window at a time: the one shown before is removed at once, its texture freed. Close can wait for its
        // closing animation, and YK.CreateLayer hands back a layer of that type still in the list without building it)
        if (Instance != null) {
            ui.RemoveLayer(Instance);
        }

        if (_pending != null) {
            Destroy(_pending);
        }

        var scaler = Mathf.Max(0.01f, ui.canvasScaler.scaleFactor);
        var screen = new Vector2(Screen.width, Screen.height) / scaler;
        // (as large as fits: the screen less the window's frame, its texts and buttons)
        var max = Mathf.Max(64f, Mathf.Min(screen.x - 160f, screen.y - (drawing != null ? 300f : 250f), 768f));
        _pendingScale = ChatPictureView.Fit(texture, max);
        var image = new Vector2(texture.width, texture.height) * _pendingScale;
        _pendingSize = new(Mathf.Min(screen.x * 0.96f, Mathf.Max(image.x + 80f, 460f)),
            Mathf.Min(screen.y * 0.94f, image.y + (drawing != null ? 250f : 200f)));
        _pending = texture;
        _pendingDrawing = drawing;
        _pendingSender = sender;
        try {
            YK.CreateLayer<LayerChatPicture, LayerCreationData>(new(""));
        } catch (Exception e) {
            EmpLog.Debug(e, "Chat: the picture's window could not be opened");
        } finally {
            // (taken by the window as it was built; left when it never was)
            if (_pending != null) {
                Destroy(_pending);
            }

            _pending = null;
            _pendingDrawing = null;
        }
    }

    public override void OnLayout()
    {
        Instance = this;
        Picture = _pending;
        Shown = _pendingDrawing;
        Sender = _pendingSender;
        Scale = _pendingScale;
        _pending = null;
        _pendingDrawing = null;
        var tab = CreateTab<TabChatPicture>("emp_ui_chat_view_title", TabId);
        UIHelper.WrapTexts(tab);
        tab.RebuildLayout(true);
    }

    public override void OnAfterAddLayer()
    {
        base.OnAfterAddLayer();
        Window.SwitchContent(TabId);
        // (one page: the strip of tabs is not shown)
        if (Window.rectTab != null) {
            Window.rectTab.gameObject.SetActive(false);
        }
    }

    public override void OnKill()
    {
        if (Picture != null) {
            Destroy(Picture);
        }

        Picture = null;
        if (Instance == this) {
            Instance = null;
        }
    }
}

/// <summary>The window's page: the picture, the drawing's title and credit, who sent it, Save and Close</summary>
internal sealed class TabChatPicture : TabEmpBase
{
    public override void OnLayout()
    {
        if (LayerChatPicture.Instance is not { Picture: { } texture } layer) {
            return;
        }

        if (GetComponent<VerticalLayoutGroup>() is { } column) {
            column.childAlignment = TextAnchor.UpperCenter;
        }

        var size = new Vector2(texture.width, texture.height) * layer.Scale;
        var row = new GameObject("ChatPictureLarge", typeof(RectTransform), typeof(LayoutElement));
        row.transform.SetParent(transform, false);
        ((RectTransform)row.transform).SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, size.y);
        var element = row.GetComponent<LayoutElement>();
        element.minHeight = element.preferredHeight = size.y;
        element.minWidth = size.x;
        var picture = new GameObject("Picture", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
        picture.transform.SetParent(row.transform, false);
        var raw = picture.GetComponent<RawImage>();
        raw.texture = texture;
        raw.raycastTarget = false;
        var rt = (RectTransform)picture.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new(0.5f, 1f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = size;

        if (layer.Shown is { } drawing) {
            if (drawing.Title.Length > 0) {
                Centre(this.Text(ChatMarkup.Escape(drawing.Title))).name = "ChatPictureTitle";
            }

            // (the credit, always: lead dev 2026-11-02, a drawing stays its creator's)
            var credit = "emp_ui_drawing_by".Loc(drawing.CreatorName);
            if (drawing.BasedOnName.Length > 0) {
                credit += "  ·  " + "emp_ui_drawing_based_on".Loc(drawing.BasedOnName);
            }

            Centre(this.Text(ChatMarkup.Escape(credit), FontColor.Gray)).name = "ChatPictureCredit";
        }

        if (layer.Sender.Length > 0) {
            Centre(this.Text(ChatMarkup.Escape("emp_ui_chat_view_sent_by".Loc(layer.Sender)), FontColor.Gray)).name = "ChatPictureSender";
        }

        var buttons = this.ButtonRow();
        buttons.Layout.childAlignment = TextAnchor.MiddleCenter;
        if (layer.Shown is { } kept) {
            var saved = ChatImages.IsSaved(kept);
            UIButton? save = null;
            save = buttons.RowButton((saved ? "emp_ui_chat_drawing_saved" : "emp_ui_chat_drawing_save").lang(), () => {
                if (ChatImages.SaveFromChat(kept) is { } why) {
                    Msg.Say(why);
                    return;
                }

                if (save != null) {
                    save.mainText.text = "emp_ui_chat_drawing_saved".lang();
                    save.interactable = false;
                }
            });
            save.name = "ChatPictureSave";
            save.interactable = !saved;
        }

        buttons.RowButton("emp_ui_chat_view_close".lang(), () => {
            if (LayerChatPicture.Instance != null) {
                LayerChatPicture.Instance.Close();
            }
        }).name = "ChatPictureClose";
    }

    private static UIText Centre(UIText text)
    {
        text.alignment = TextAnchor.MiddleCenter;
        return text;
    }
}
