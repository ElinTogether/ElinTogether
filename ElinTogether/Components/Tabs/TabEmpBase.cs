using ElinTogether.Helper;
using UnityEngine;
using UnityEngine.UI;
using YKF;

namespace ElinTogether.Components;

internal abstract class TabEmpBase : YKLayout<LayerCreationData>
{
    protected readonly Rect RefSize = LayerElinTogether.Instance!.Bound;

    internal static Vector2 FitCell(int constraint)
    {
        var scaler = EMono.ui.canvasScaler.scaleFactor;
        return new Vector2(Screen.width / 1.7f / constraint, 45f) / scaler;
    }

    protected YKGrid CharaGrid()
    {
        var grid = Grid()
            .WithConstraintCount(2);
        grid.Fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        grid.Layout.cellSize = new(RefSize.width / 2.2f, RefSize.width * 0.2f);
        return grid;
    }

    protected YKVertical CharaCard(YKLayout parent, Chara chara)
    {
        var card = parent.MakeCard();
        var bannerGroup = card.Horizontal();
        bannerGroup.Layout.spacing = 15f;

        var go = new GameObject("portrait");
        go.transform.SetParent(bannerGroup.transform);
        var portrait = go.AddComponent<Portrait>();
        portrait.tooltip = new() {
            enable = false,
        };
        portrait.portrait = go.AddComponent<Image>();
        portrait.portrait.preserveAspect = true;

        var go1 = new GameObject("overlay");
        go1.transform.SetParent(go.transform);
        portrait.overlay = go1.AddComponent<Image>();
        portrait.overlay.preserveAspect = true;

        portrait.SetChara(chara);

        portrait.portrait.LayoutElement().preferredWidth = RefSize.width * 0.12f;
        portrait.portrait.rectTransform.sizeDelta = new(RefSize.width, RefSize.width * 0.16f);
        portrait.overlay.rectTransform.anchorMin =
            portrait.overlay.rectTransform.anchorMax =
                portrait.overlay.rectTransform.sizeDelta = Vector2.zero;

        var infoGroup = bannerGroup.Vertical();
        infoGroup.LayoutElement().preferredWidth = 1f;
        return infoGroup;
    }
}