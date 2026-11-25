using System.Collections;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;

namespace CombatStatisticsFix;

// Combat Statistics 0.5.1 tracks two things around CreatureCmd.Damage(choiceContext, targets, amount, props, dealer,
// cardSource, cardPlay): which power or relic is dealing the damage (DamageOriginTracking, for the damage-by-source
// bars) and which players an attack was aimed at (OstyAbsorptionTracking, for what Osty takes for them). Game v0.113
// removed that overload. On v0.114.0 the one taking creatures has a new last parameter and forwards to a core overload
// taking DamageTargetData, which attacks (AttackCommand) also call directly. So both trackers never attach, and Combat
// Statistics logs that its damage numbers may be incomplete. This patches the core overload and runs Combat
// Statistics' own two prefixes and finalizers around it, as it did around the old overload.
// Everything is by reflection, so this loads on an older game and does nothing there.
static class DamageTracking
{
    static MethodInfo? _originPrefix, _originRestore, _ostyPrefix, _ostyRestore;
    static Func<object, Creature?>? _target;
    static bool? _theirsAttached;
    static bool _reported;
    internal static MethodBase? Core;

    public static void Apply(Harmony harmony, Assembly assembly)
    {
        var damage = typeof(CreatureCmd).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "Damage").ToList();
        if (damage.Any(m => m.GetParameters() is { Length: 7 } p && p[1].ParameterType == typeof(IEnumerable<Creature>) && p[2].ParameterType == typeof(decimal)))
            return; // the overload Combat Statistics patches is still there
        // Damage(PlayerChoiceContext, IEnumerable<DamageTargetData>, decimal, ...)
        Core = damage.FirstOrDefault(m => m.GetParameters() is { Length: >= 3 } p && p[0].ParameterType == typeof(PlayerChoiceContext)
            && p[1].ParameterType is { IsGenericType: true } e && e.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            && e.GetGenericArguments()[0].Name == "DamageTargetData" && p[2].ParameterType == typeof(decimal));
        var data = Core?.GetParameters()[1].ParameterType.GetGenericArguments()[0];
        var origin = assembly.GetType("CombatStatistics.Patches.DamageOriginTracking");
        var osty = assembly.GetType("CombatStatistics.Patches.OstyAbsorptionTracking");
        _originPrefix = origin is null ? null : AccessTools.DeclaredMethod(origin, "DamagePrefix");
        _originRestore = origin is null ? null : AccessTools.DeclaredMethod(origin, "RestoreFinalizer");
        _ostyPrefix = osty is null ? null : AccessTools.DeclaredMethod(osty, "DamagePrefix");
        _ostyRestore = osty is null ? null : AccessTools.DeclaredMethod(osty, "RestoreFinalizer");
        var field = data is null ? null : AccessTools.Field(data, "target");
        if (Core is null || field is null || _originPrefix is null || _originRestore is null || _ostyPrefix is null || _ostyRestore is null)
        {
            Core = null;
            Log.Warn($"[{ModEntry.ModId}] The game's Damage or Combat Statistics' damage tracking changed, so its damage sources and Osty's absorption aren't restored (this fix needs an update)");
            return;
        }
        _target = d => field.GetValue(d) as Creature;
        harmony.Patch(Core, prefix: new HarmonyMethod(typeof(DamageTracking), nameof(Prefix)), finalizer: new HarmonyMethod(typeof(DamageTracking), nameof(Finalizer)));
        Log.Info($"[{ModEntry.ModId}] Combat Statistics' damage sources and Osty's absorption are tracked on the game's new Damage");
    }

    // What a prefix saved, for its finalizer to put back; only the prefixes that ran have one
    internal sealed class Saved
    {
        public bool Origin, Osty;
        public object? OriginValue, OstyValue;
    }

    // Combat Statistics' prefixes, in its order. The arguments are by position (__0 choiceContext, __1 targetData), so
    // a renamed parameter doesn't stop the patch.
    internal static void Prefix(PlayerChoiceContext? __0, object? __1, MethodBase? __originalMethod, out Saved? __state)
    {
        __state = null;
        if (TheirsAttached(__originalMethod) || _originPrefix is not { } originPrefix || _ostyPrefix is not { } ostyPrefix || _target is not { } target)
            return;
        var saved = __state = new Saved();
        try
        {
            object?[] origin = [__0, null];
            originPrefix.Invoke(null, origin);
            (saved.Origin, saved.OriginValue) = (true, origin[1]);
            IEnumerable<Creature>? targets = __1 is IEnumerable all ? all.Cast<object>().Select(target).OfType<Creature>().ToList() : null;
            object?[] osty = [targets, null];
            ostyPrefix.Invoke(null, osty);
            (saved.Osty, saved.OstyValue) = (true, osty[1]);
        }
        catch (Exception e)
        {
            Report(e);
        }
    }

    internal static Exception? Finalizer(Exception? __exception, Saved? __state)
    {
        try
        {
            if (__state is { Osty: true } && _ostyRestore is { } osty)
                osty.Invoke(null, [null, __state.OstyValue]);
            if (__state is { Origin: true } && _originRestore is { } origin)
                origin.Invoke(null, [null, __state.OriginValue]);
        }
        catch (Exception e)
        {
            Report(e);
        }
        return __exception;
    }

    // A Combat Statistics that patches this overload itself doesn't need this; checked once, as it patches at load
    static bool TheirsAttached(MethodBase? original) =>
        _theirsAttached ??= original != null && Harmony.GetPatchInfo(original)?.Prefixes.Any(p => p.owner == "CombatStatistics") == true;

    static void Report(Exception e)
    {
        if (_reported)
            return;
        _reported = true;
        Log.Warn($"[{ModEntry.ModId}] Couldn't run Combat Statistics' damage tracking: {e}");
    }
}
