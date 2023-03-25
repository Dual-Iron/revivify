using Menu.Remix.MixedUI;
using UnityEngine;

namespace Revivify;

sealed class Options : OptionInterface
{
    public static Configurable<float> ReviveSpeed;
    public static Configurable<int> DeathsUntilExhaustion;
    public static Configurable<int> DeathsUntilComa;
    public static Configurable<int> DeathsUntilExpire;
    public static Configurable<float> CorpseExpiryTime;

    public Options()
    {
        ReviveSpeed = config.Bind("cfgReviveSpeed", 1f, new ConfigAcceptableRange<float>(0.1f, 5f));
        DeathsUntilExhaustion = config.Bind("cfgDeathsUntilExhaustion", 1, new ConfigAcceptableRange<int>(1, 10));
        DeathsUntilComa = config.Bind("cfgDeathsUntilComa", 2, new ConfigAcceptableRange<int>(1, 10));
        DeathsUntilExpire = config.Bind("cfgDeathsUntilExpire", 3, new ConfigAcceptableRange<int>(1, 10));
        CorpseExpiryTime = config.Bind("cfgCorpseExpiryTime", 1.5f, new ConfigAcceptableRange<float>(0.05f, 10f));
    }

    public override void Initialize()
    {
        base.Initialize();

        Tabs = new OpTab[] { new OpTab(this) };

        float sliderX = 270;
        float y = 390;

        var author = new OpLabel(20, 600 - 40, "by Dual", true);
        var github = new OpLabel(20, 600 - 40 - 40, "github.com/Dual-Iron/revivify");

        var d1 = new OpLabel(new(220, y), Vector2.zero, "Revive speed multiplier", FLabelAlignment.Right);
        var s1 = new OpFloatSlider(ReviveSpeed, new Vector2(sliderX, y - 6), 300, decimalNum: 1);

        var d2 = new OpLabel(new(220, y -= 60), Vector2.zero, "Deaths until exhaustion", FLabelAlignment.Right);
        var s2 = new OpSlider(DeathsUntilExhaustion, new Vector2(sliderX, y - 6), 300);

        var d3 = new OpLabel(new(220, y -= 60), Vector2.zero, "Deaths until slugpups become comatose", FLabelAlignment.Right);
        var s3 = new OpSlider(DeathsUntilComa, new Vector2(sliderX, y - 6), 300);

        var d4 = new OpLabel(new(220, y -= 60), Vector2.zero, "Deaths until slugpups permanently expire", FLabelAlignment.Right);
        var s4 = new OpSlider(DeathsUntilExpire, new Vector2(sliderX, y - 6), 300);

        var d5 = new OpLabel(new(220, y -= 60), Vector2.zero, "Time until bodies expire, in minutes", FLabelAlignment.Right);
        var s5 = new OpFloatSlider(CorpseExpiryTime, new Vector2(sliderX, y - 6), 300, decimalNum: 1);

        Tabs[0].AddItems(author, github, d1, s1, d2, s2, d3, s3, d4, s4, d5, s5);
    }
}
