using ElinTogether.LangMod;
using UnityEngine;

namespace ElinTogether.Components;

internal class TabClientConfiguration : TabEmpBase
{
    public override void OnLayout()
    {
        var btnGroup = Horizontal();
        btnGroup.Layout.childForceExpandWidth = true;

        var pingKey = new EInput.KeyMap {
            action = EAction.None,
            key = KeyCode.P,
            required = true,
        };
        btnGroup.Button("emp_ui_ping_keymap".Loc(pingKey.key.ToString()), () => {
            var l = global::Layer.Create<Dialog>("DialogKeymap");
            l.textDetail.SetText("dialog_keymap".lang("emp_ui_ping_action".lang()));
            l.keymap = pingKey;
            l.SetOnKill(() => {
                EmpConfig.Client.PingKeybind.Value = pingKey.key;
                LayerElinTogether.Instance?.Reopen();
            });
            EMono.ui.AddLayer(l);
        });
    }
}