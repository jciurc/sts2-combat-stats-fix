using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace CombatStatisticsFix;

// Combat Statistics keeps the first font it finds in the scene for its end-of-combat overlay, for the whole session.
// The game disposes a font another mod fetched through its asset cache (one that isn't preloaded) at the next room
// load, so after the first fight the overlay can be built with a disposed font, throws, and never shows. A
// prefix on its GameFont drops the kept font once it's disposed, so it finds the font again. HotkeyFix covers
// the other break: game v0.113's hotkey API change, which stuck its Continue button. WuppyStats adds what
// v0.113's Wuppy pet absorbs, which Combat Statistics doesn't count. DamageTracking puts Combat Statistics' damage-source
// and Osty tracking back on the game's new Damage.
[ModInitializer(nameof(Init))]
public static class ModEntry
{
    public const string ModId = "CombatStatisticsFix";
    const string Target = "CombatStatistics";

    static FieldInfo? _font;
    static bool _patched, _reported;

    public static void Init()
    {
        // Combat Statistics may load before or after this mod: patch it now, or when its assembly loads
        AppDomain.CurrentDomain.AssemblyLoad += (_, e) => TryPatch(e.LoadedAssembly);
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            TryPatch(assembly);
#if DEVTOOLS
        SelfTest.Hook(new Harmony(ModId));
#endif
        Log.Info($"[{ModId}] Initialized; " + (_patched ? "Combat Statistics was already loaded (what was patched is above)" : "waiting for Combat Statistics to load"));
    }

    static void TryPatch(Assembly assembly)
    {
        if (_patched || assembly.IsDynamic || assembly.GetName().Name != Target)
            return;
        _patched = true;
        var harmony = new Harmony(ModId);
        try
        {
            if (assembly.GetType("CombatStatistics.OverlayTheme") is not { } theme
                || AccessTools.DeclaredMethod(theme, "GameFont") is not { } gameFont
                || (_font = AccessTools.Field(theme, "_gameFont")) is null)
                Log.Warn($"[{ModId}] This Combat Statistics has no OverlayTheme.GameFont: it changed, so there's nothing to fix (or this fix needs an update)");
            else
            {
                harmony.Patch(gameFont, prefix: new HarmonyMethod(typeof(ModEntry), nameof(DropDisposed)));
                Log.Info($"[{ModId}] Patched Combat Statistics' OverlayTheme.GameFont");
            }
        }
        catch (Exception e)
        {
            Log.Error($"[{ModId}] Couldn't patch Combat Statistics, so its overlay may still show only for the first fight: {e}");
        }
        try
        {
            HotkeyFix.Apply(harmony, assembly);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModId}] Couldn't fix Combat Statistics' hotkeys, so its Continue button may not close the overlay: {e}");
        }
        try
        {
            DamageTracking.Apply(harmony, assembly);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModId}] Couldn't restore Combat Statistics' damage tracking, so its damage sources may be incomplete: {e}");
        }
        try
        {
            WuppyStats.Apply(harmony, assembly);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModId}] Couldn't add Wuppy's stats to Combat Statistics: {e}");
        }
    }

    static void DropDisposed()
    {
        try
        {
            if (_font?.GetValue(null) is not GodotObject font || GodotObject.IsInstanceValid(font))
                return;
            _font.SetValue(null, null);
            if (_reported)
                return;
            _reported = true;
            Log.Info($"[{ModId}] Combat Statistics' overlay font was disposed by the game; it finds the font again");
        }
        catch
        {
            // left to Combat Statistics
        }
    }
}
