using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace ElinTogether.Components;

/// <summary>
///     Icons inside a line of text (the Chat's emotes): Unity's Text leaves a blank for each &lt;quad&gt; tag, drawn
///     with no glyph (its four corners share one texture point). This hides that blank and puts an image of the same
///     size over it, in the order the icons were given; it follows the text whenever it is laid out again (wrapped
///     to another width)
/// </summary>
internal sealed class ChatIcons : BaseMeshEffect
{
    private readonly List<Sprite> _sprites = [];
    private readonly List<Rect> _rects = [];
    private readonly List<Image> _images = [];
    private bool _dirty;

    internal void Set(IEnumerable<Sprite> sprites)
    {
        _sprites.Clear();
        _sprites.AddRange(sprites);
        if (graphic != null) {
            graphic.SetVerticesDirty();
        }
    }

    public override void ModifyMesh(VertexHelper vh)
    {
        if (!IsActive()) {
            return;
        }

        _rects.Clear();
        var corners = new UIVertex[4];
        for (var i = 0; i + 3 < vh.currentVertCount; i += 4) {
            for (var k = 0; k < 4; k++) {
                vh.PopulateUIVertex(ref corners[k], i + k);
            }

            if (corners[1].uv0 != corners[0].uv0 || corners[2].uv0 != corners[0].uv0 || corners[3].uv0 != corners[0].uv0) {
                continue;
            }

            var min = Vector2.Min(Vector2.Min(corners[0].position, corners[1].position), Vector2.Min(corners[2].position, corners[3].position));
            var max = Vector2.Max(Vector2.Max(corners[0].position, corners[1].position), Vector2.Max(corners[2].position, corners[3].position));
            if (max.x - min.x < 2f) {
                continue;
            }

            for (var k = 0; k < 4; k++) {
                corners[k].color = new Color32(0, 0, 0, 0);
                vh.SetUIVertex(corners[k], i + k);
            }

            // (a shadow or an outline of the text draws the blank again a pixel away: one icon for them all)
            var rect = new Rect(min, max - min);
            if (!_rects.Exists(r => Mathf.Abs(r.x - rect.x) < 4f && Mathf.Abs(r.y - rect.y) < 4f)) {
                _rects.Add(rect);
            }
        }

        _dirty = true;
    }

    private void LateUpdate()
    {
        if (!_dirty) {
            return;
        }

        _dirty = false;
        var pivot = ((RectTransform)transform).pivot;
        for (var i = 0; i < Mathf.Max(_rects.Count, _images.Count); i++) {
            if (i >= _rects.Count || i >= _sprites.Count) {
                if (i < _images.Count) {
                    _images[i].enabled = false;
                }

                continue;
            }

            if (i >= _images.Count) {
                var go = new GameObject("Icon", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
                go.transform.SetParent(transform, false);
                go.GetComponent<LayoutElement>().ignoreLayout = true;
                var made = go.GetComponent<Image>();
                made.raycastTarget = false;
                made.preserveAspect = true;
                _images.Add(made);
            }

            var image = _images[i];
            image.enabled = true;
            image.sprite = _sprites[i];
            // (the mesh is in the text's own space, around its pivot)
            var rt = image.rectTransform;
            rt.anchorMin = rt.anchorMax = pivot;
            rt.pivot = new(0.5f, 0.5f);
            rt.anchoredPosition = _rects[i].center;
            rt.sizeDelta = _rects[i].size;
        }
    }
}
