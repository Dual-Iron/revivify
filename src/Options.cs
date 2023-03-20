using Menu.Remix.MixedUI;
using UnityEngine;

namespace Revivify;

sealed class Options : OptionInterface
{
    public static Configurable<float> ReviveSpeed;
    public static Configurable<int> DeathsUntilExhaustion;

    public Options()
    {
        ReviveSpeed = config.Bind("cfgReviveSpeed", 1f, new ConfigAcceptableRange<float>(0.1f, 5f));
        DeathsUntilExhaustion = config.Bind("cfgDeathsUntilExhaustion", 1, new ConfigAcceptableRange<int>(1, 10));
    }

    public override void Initialize()
    {
        base.Initialize();

        Tabs = new OpTab[] { new OpTab(this) };

        float y = 380;

        var author = new OpLabel(20, 600 - 40, "by Dual", true);
        var github = new OpLabel(20, 600 - 40 - 40, "github.com/Dual-Iron/revivify");

        var d1 = new OpLabel(new(100, y), Vector2.zero, "Revive speed multiplier", FLabelAlignment.Left);
        var s1 = new OpFloatSlider(ReviveSpeed, new Vector2(104, y - 48), 300, decimalNum: 1, vertical: false);

        var d2 = new OpLabel(new(320, y -= 110), Vector2.zero, "Deaths until exhaustion", FLabelAlignment.Right);
        var s2 = new OpSlider(DeathsUntilExhaustion, new Vector2(104, y - 48), 300, vertical: false);

        Tabs[0].AddItems(author, github, d1, s1, d2, s2);
    }
}
