using UnityEngine;
using YKF;

namespace ElinTogether.Components;

internal abstract class TabEmpBase : YKLayout<LayerCreationData>
{
    internal static Vector2 FitCell(int constraint)
    {
        var scaler = EMono.ui.canvasScaler.scaleFactor;
        return new Vector2(Screen.width / 1.7f / constraint, 45f) / scaler;
    }
}