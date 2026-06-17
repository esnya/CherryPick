using HarmonyLib;
using ResoniteModLoader;
using FrooxEngine;
#if CHERRYPICK_HOTRELOAD
using ResoniteHotReloadLib;
#endif

namespace CherryPick;

public partial class CherryPick : ResoniteMod
{
    private const string HARMONY_ID = "net.Cyro.CherryPick";

    public override string Name => "<color=hero.green>🍃</color><color=hero.red>🍒</color> CherryPick"; // May remove this flair if it gets obnoxious
    public override string Author => "Cyro";
    public override string Version => typeof(CherryPick).Assembly.GetName().Version.ToString();
    public override string Link => "https://github.com/BlueCyro/CherryPick";
    public static ModConfiguration? Config;

    public override void OnEngineInit()
    {
        Harmony harmony = new(HARMONY_ID);
        Config = GetConfiguration();
        Config?.Save(true);
        harmony.PatchAll();

        // Scan for workers only after FrooxEngine is fully initialized
        Engine.Current.RunPostInit(() =>
        {
            Task.Run(() =>
            {
                // CherryPicker.WarmScope(); // Initialize class to warm up those code paths all nice and toasty (so we don't hitch when first spawning a component selector)
                // CherryPicker.WarmScope(ProtoFluxHelper.PROTOFLUX_ROOT);
                CherryPicker.SetReady();
            });
        });

#if CHERRYPICK_HOTRELOAD
        if (Config?.GetValue(RegisterWithHotReloadLibs) == true)
            HotReloader.RegisterForHotReload(this);
#endif
    }

#if CHERRYPICK_HOTRELOAD
    public static void BeforeHotReload()
    {
        new Harmony(HARMONY_ID).UnpatchAll(HARMONY_ID);
    }

    public static void OnHotReload(ResoniteMod modInstance)
    {
        Config = modInstance.GetConfiguration();
        Config?.Save(true);
        new Harmony(HARMONY_ID).PatchAll(typeof(CherryPick).Assembly);
        CherryPicker.SetReady();
    }
#endif
}
