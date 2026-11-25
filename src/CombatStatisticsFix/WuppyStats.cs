using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

namespace CombatStatisticsFix;

// Wuppy, the pet of an event relic (game v0.113), takes the powered attacks aimed at its owner. Combat Statistics
// records damage to players and enemies (and to Osty, by name), so what Wuppy takes isn't counted anywhere. This counts
// it as two character stats of the owner, which Combat Statistics adds up over the run and saves with the rest, and
// adds a row for each to the owner's box in the overlay (before Kills and Deaths) when it's above zero.
// Wuppy's types are looked up by name, so this loads on a game without Wuppy and does nothing there.
static class WuppyStats
{
    internal const string Absorbed = "WuppyAbsorbed", Fainted = "WuppyFainted";
    const string WuppyMonster = "MegaCrit.Sts2.Core.Models.Monsters.Wuppy", WuppyRelic = "MegaCrit.Sts2.Core.Models.Relics.Wuppy";
    const string SkullIcon = "res://images/ui/emote/skull.png"; // Combat Statistics' own Deaths row icon

    static readonly ConditionalWeakTable<DamageResult, object> Counted = [];
    static MethodInfo? _getOrCreate, _addStat;
    static PropertyInfo? _stats;
    static ConstructorInfo? _row;
    static Type? _statsType;
    static string? _wuppyIcon;
    static bool _reported, _iconFailed;

    // the game's own types (a lookup in its assembly, not a scan of every loaded one)
    static Type? GameType(string name) => typeof(ModelDb).Assembly.GetType(name);

    public static void Apply(Harmony harmony, Assembly assembly)
    {
        if (GameType(WuppyMonster) is null)
            return; // a game from before Wuppy
        var tracker = assembly.GetType("CombatStatistics.CombatTracker");
        var catalog = assembly.GetType("CombatStatistics.CharacterStatCatalog");
        var rowType = assembly.GetType("CombatStatistics.CharStatRow");
        _statsType = assembly.GetType("CombatStatistics.PlayerCombatStats");
        var record = tracker is null ? null : AccessTools.DeclaredMethod(tracker, "RecordDamage");
        var rowsFor = catalog is null ? null : AccessTools.DeclaredMethod(catalog, "RowsFor");
        _getOrCreate = tracker is null ? null : AccessTools.DeclaredMethod(tracker, "GetOrCreate", [typeof(Player)]);
        _addStat = _statsType is null ? null : AccessTools.DeclaredMethod(_statsType, "AddCharacterStat", [typeof(string), typeof(long)]);
        _stats = _statsType is null ? null : AccessTools.DeclaredProperty(_statsType, "CharacterStats");
        _row = rowType is null || _statsType is null ? null
            : AccessTools.DeclaredConstructor(rowType, [typeof(string), typeof(Func<,>).MakeGenericType(_statsType, typeof(long)), typeof(bool), typeof(string)]);
        if (record is null || rowsFor is null || _getOrCreate is null || _addStat is null || _stats is null || _row is null)
        {
            Log.Warn($"[{ModEntry.ModId}] This Combat Statistics records or lists its stats differently, so Wuppy's stats aren't added (this fix needs an update)");
            return;
        }
        harmony.Patch(record, postfix: new HarmonyMethod(typeof(WuppyStats), nameof(RecordDamagePostfix)));
        harmony.Patch(rowsFor, postfix: new HarmonyMethod(typeof(WuppyStats), nameof(RowsForPostfix)));
        Log.Info($"[{ModEntry.ModId}] Combat Statistics counts what Wuppy absorbs");
    }

    // After CombatTracker.RecordDamage, which skips a pet's damage. It sees a result more than once (from the creature
    // and from the dealer's side), so each is counted once.
    static void RecordDamagePostfix(Creature receiver, DamageResult result)
    {
        try
        {
            if (result is null || receiver?.Monster?.GetType().FullName != WuppyMonster || receiver.PetOwner is not { } owner
                || !Counted.TryAdd(result, true))
                return;
            if (_getOrCreate is not { } getOrCreate || _addStat is not { } addStat)
                return;
            var stats = getOrCreate.Invoke(null, [owner]);
            addStat.Invoke(stats, [Absorbed, (long)Math.Max(0, result.UnblockedDamage)]);
            if (result.WasTargetKilled)
                addStat.Invoke(stats, [Fainted, 1L]);
        }
        catch (Exception e)
        {
            Report(e);
        }
    }

    // After CharacterStatCatalog.RowsFor, which builds a new list ending in Kills and Deaths for each player's box
    static void RowsForPostfix(object player, object __result)
    {
        try
        {
            if (__result is not IList rows || _row is not { } row)
                return;
            var at = Math.Max(0, rows.Count - 2);
            // inserted at the same place, so Absorbed ends up first
            if (Value(player, Fainted) > 0 && Reader(Fainted) is { } fainted)
                rows.Insert(at, row.Invoke(["Wuppy fainted", fainted, false, SkullIcon]));
            if (Value(player, Absorbed) > 0 && Reader(Absorbed) is { } absorbed)
                rows.Insert(at, row.Invoke(["Absorbed by Wuppy", absorbed, false, WuppyIcon()]));
        }
        catch (Exception e)
        {
            Report(e);
        }
    }

    // The relic's icon, as the game draws it in the top bar; looked up at the first row, as the game's models don't
    // exist yet when Combat Statistics loads. The skull if it can't be found, from then on.
    static string WuppyIcon()
    {
        if (_wuppyIcon != null || _iconFailed)
            return _wuppyIcon ?? SkullIcon;
        try
        {
            if (typeof(ModelDb).GetMethod(nameof(ModelDb.Relic)) is { } relicOf && GameType(WuppyRelic) is { } wuppy
                && relicOf.MakeGenericMethod(wuppy).Invoke(null, null) is RelicModel { PackedIconPath: { Length: > 0 } icon })
                return _wuppyIcon = icon;
        }
        catch
        {
            // the skull
        }
        _iconFailed = true;
        return SkullIcon;
    }

    internal static long Value(object? player, string key) =>
        player is not null && _stats?.GetValue(player) is IDictionary<string, long> stats && stats.TryGetValue(key, out var value) ? value : 0;

    // a Func<PlayerCombatStats, long> for the row, made for Combat Statistics' type
    static Delegate? Reader(string key) =>
        _statsType is { } type && typeof(WuppyStats).GetMethod(nameof(MakeReader), BindingFlags.NonPublic | BindingFlags.Static) is { } make
            ? make.MakeGenericMethod(type).Invoke(null, [key]) as Delegate : null;

    static Func<T, long> MakeReader<T>(string key) => player => Value(player, key);

    static void Report(Exception e)
    {
        if (_reported)
            return;
        _reported = true;
        Log.Warn($"[{ModEntry.ModId}] Couldn't count Wuppy's stats: {e}");
    }
}
