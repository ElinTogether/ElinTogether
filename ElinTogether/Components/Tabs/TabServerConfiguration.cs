using System.Collections.Generic;
using ElinTogether.Helper;
using ElinTogether.Net;
using UnityEngine.UI;
using YKF;

namespace ElinTogether.Components;

internal class TabServerConfiguration : TabEmpBase
{
    public override void OnLayout()
    {
        BuildValidationSets();

        BuildSyncMode();
    }

    private void BuildValidationSets()
    {
        var sets = this.MakeCard();

        sets.TextFlavor("emp_ui_sv_cfg_validation_sets");

        var list = sets.Grid()
            .WithConstraintCount(2);
        list.Fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        list.Layout.cellSize = FitCell(2);

        var options = new List<string> {
            "none",
            "sources",
            "plugins",
            "all",
        };

        foreach (var option in options) {
            list.Toggle($"emp_validation_{option}", EmpConfig.Server.SourceValidationSet.Value.Contains(option), value => {
                var raw = EmpConfig.Server.SourceValidationSet.Value;
                raw = raw.Replace($"{option},", "").Replace(option, "");
                if (value) {
                    raw = $"{option},{raw}";
                }
                EmpConfig.Server.SourceValidationSet.Value = raw;
                NetSession.Instance.Connection?.CreateValidation();
            });
        }
    }

    private void BuildSyncMode()
    {
        var modes = this.MakeCard();

        modes.Toggle("emp_ui_sv_cfg_shared_speed", EmpConfig.Server.SharedAverageSpeed.Value, value => {
            EmpConfig.Server.SharedAverageSpeed.Value = value;
        });

        modes.Toggle("emp_ui_sv_cfg_turn_combat", EmpConfig.Server.TurnBasedCombat.Value, value => {
            EmpConfig.Server.TurnBasedCombat.Value = value;
        });
    }
}