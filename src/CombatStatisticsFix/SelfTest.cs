using System.Collections;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Unlocks;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatStatisticsFix;

// The self-test (DEVTOOLS builds only), when COMBAT_STATS_FIX_SELFTEST=<folder> is set: at the first main menu, with
// Combat Statistics loaded, it runs Combat Statistics' own Continue path (register the hotkey, then RemoveCurrent, what
// Continue calls) and its run-history map hotkey, checks the game's hotkey bindings and the overlay's canvas after
// each, then counts a hit on a Wuppy pet and runs the damage trackers around the game's Damage, writes results.txt there ("PASS x" / "FAIL x: why", then "done"), also to the log, then quits.
static class SelfTest
{
    static bool _started;

    internal static void Hook(Harmony harmony) =>
        harmony.Patch(AccessTools.DeclaredMethod(typeof(NMainMenu), nameof(NMainMenu._Ready)), postfix: new HarmonyMethod(typeof(SelfTest), nameof(MaybeStart)));

    static void MaybeStart()
    {
        if (_started || System.Environment.GetEnvironmentVariable("COMBAT_STATS_FIX_SELFTEST") is not { Length: > 0 } dir)
            return;
        _started = true;
        var lines = new List<string>();
        void Check(string name, bool pass, string why = "") => lines.Add(pass ? $"PASS {name}" : $"FAIL {name}: {why}");
        try
        {
            Run(Check);
        }
        catch (Exception e)
        {
            Check("self-test", false, e.ToString().ReplaceLineEndings(" "));
        }
        foreach (var line in lines)
            Log.Info($"[{ModEntry.ModId}] self-test: {line}");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "results.txt"), [.. lines, "done"]);
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.CreateTimer(0.2).Timeout += () => tree.Quit();
    }

    static void Run(Action<string, bool, string> check)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "CombatStatistics");
        check("Combat Statistics is loaded", assembly != null, "not in the AppDomain");
        if (assembly == null)
            return;
        check("four hotkey methods redirected", HotkeyFix.Redirects.Count == 4, $"{HotkeyFix.Redirects.Count}: {string.Join(", ", HotkeyFix.Redirects.Keys.Select(m => m.Name))}");
        string[] callers = ["RunSummaryOverlay.RegisterContinueHotkey", "RunSummaryOverlay.RemoveCurrent", "GameOverContinueButtonPatch.Postfix",
            "RunHistoryScreenPatch.OnDisplayRun", "RunHistoryScreenPatch.OnSubmenuHidden", "RunHistoryScreenPatch.EnsureButton"];
        check("their six callers transpiled", HotkeyFix.Callers.Order().SequenceEqual(callers.Order()), string.Join(", ", HotkeyFix.Callers));
        var manager = NHotkeyManager.Instance;
        check("the game has a hotkey manager", manager != null, "NHotkeyManager.Instance is null");
        if (manager == null)
            return;

        // Continue: Show sets _current to the canvas, then RegisterContinueHotkey; RemoveCurrent (what Continue runs) must
        // unbind it and free the canvas
        var overlay = assembly.GetType("CombatStatistics.RunSummaryOverlay")!;
        var canvas = new Control();
        ((SceneTree)Engine.GetMainLoop()).Root.AddChild(canvas);
        Action onContinue = () => { };
        AccessTools.Field(overlay, "_current").SetValue(null, canvas);
        AccessTools.DeclaredMethod(overlay, "RegisterContinueHotkey").Invoke(null, [canvas, onContinue]);
        check("Continue's hotkey is bound on top", Bound(manager, MegaInput.confirm).LastOrDefault() == onContinue, "another binding is on top of confirm");
        check("Continue's hotkey fires", Firing(manager, MegaInput.confirm) == onContinue, "confirm wouldn't run it");
        AccessTools.DeclaredMethod(overlay, "RemoveCurrent").Invoke(null, null);
        check("Continue unbinds its hotkey", !Bound(manager, MegaInput.confirm).Contains(onContinue), "still bound after RemoveCurrent");
        check("Continue frees the overlay", canvas.IsQueuedForDeletion(), "the canvas isn't queued for deletion");
        check("Continue clears its fields", AccessTools.Field(overlay, "_continueHotkeyManager").GetValue(null) == null
            && AccessTools.Field(overlay, "_continueHotkeyAction").GetValue(null) == null, "a hotkey field is still set");

        // Run History's map hotkey: RegisterMapHotkey binds it; OnSubmenuHidden (redirected) unbinds it
        var history = assembly.GetType("CombatStatistics.Patches.RunHistoryScreenPatch")!;
        HotkeyFix.RegisterMap();
        var map = Bound(manager, MegaInput.viewMap).LastOrDefault();
        check("the map hotkey is bound", map?.Method.Name == "OnPressed" && map.Method.DeclaringType == history, $"on top of viewMap: {map?.Method.DeclaringType?.Name}.{map?.Method.Name}");
        AccessTools.DeclaredMethod(history, "OnSubmenuHidden").Invoke(null, null);
        check("the map hotkey is unbound", !Bound(manager, MegaInput.viewMap).Any(a => a.Method.DeclaringType == history), "still bound after OnSubmenuHidden");

        Wuppy(assembly, check);
        Damage(assembly, check);

        // the control: an unredirected leaf still throws, so the checks above went through the fix
        try
        {
            AccessTools.DeclaredMethod(overlay, "UnregisterContinueHotkey").Invoke(null, null);
            check("the broken method still throws (the control)", false, "it ran: the game has the old signatures, so this test proves nothing");
        }
        catch (TargetInvocationException e) when (e.InnerException is MissingMethodException)
        {
            check("the broken method still throws (the control)", true, "");
        }
    }

    // Wuppy: a hit on a Wuppy pet through Combat Statistics' own RecordDamage (twice, as it's called), then its rows
    static void Wuppy(Assembly assembly, Action<string, bool, string> check)
    {
        var tracker = assembly.GetType("CombatStatistics.CombatTracker")!;
        var player = Player.CreateForNewRun<Ironclad>(UnlockState.none, 990_001);
        var wuppy = new Creature(ModelDb.Monster<MegaCrit.Sts2.Core.Models.Monsters.Wuppy>().ToMutable(), CombatSide.Player, null) { PetOwner = player };
        var hit = new DamageResult(wuppy, ValueProp.Move) { UnblockedDamage = 7, WasTargetKilled = true };
        var record = AccessTools.DeclaredMethod(tracker, "RecordDamage");
        record.Invoke(null, [wuppy, null, hit, null, ValueProp.Move, false]);
        record.Invoke(null, [wuppy, null, hit, null, ValueProp.Move, false]);
        var stats = AccessTools.DeclaredMethod(tracker, "GetOrCreate", [typeof(Player)]).Invoke(null, [player]);
        check("Wuppy's hit counted once", WuppyStats.Value(stats, WuppyStats.Absorbed) == 7, $"absorbed {WuppyStats.Value(stats, WuppyStats.Absorbed)}");
        check("Wuppy fainting counted", WuppyStats.Value(stats, WuppyStats.Fainted) == 1, $"fainted {WuppyStats.Value(stats, WuppyStats.Fainted)}");

        var rowsFor = AccessTools.DeclaredMethod(assembly.GetType("CombatStatistics.CharacterStatCatalog"), "RowsFor");
        var rows = ((IEnumerable)rowsFor.Invoke(null, [stats])!).Cast<object>().Select(r => (
            Label: (string)AccessTools.Property(r.GetType(), "Label").GetValue(r)!,
            Value: (long)((Delegate)AccessTools.Property(r.GetType(), "Value").GetValue(r)!).DynamicInvoke(stats)!,
            Icon: (string)AccessTools.Property(r.GetType(), "IconPath").GetValue(r)!)).ToList();
        var labels = string.Join(", ", rows.Select(r => $"{r.Label} {r.Value}"));
        check("Wuppy's rows come before Kills and Deaths", rows.Count >= 4 && rows[^4].Label == "Absorbed by Wuppy" && rows[^4].Value == 7
            && rows[^3].Label == "Wuppy fainted" && rows[^3].Value == 1 && rows[^2].Label == "Kills" && rows[^1].Label == "Deaths", labels);
        check("Wuppy's icon is the relic's", rows.FirstOrDefault(r => r.Label == "Absorbed by Wuppy").Icon is { } icon
            && icon.Contains("wuppy") && ResourceLoader.Exists(icon), rows.FirstOrDefault(r => r.Label == "Absorbed by Wuppy").Icon ?? "no row");
        var other = Activator.CreateInstance(assembly.GetType("CombatStatistics.PlayerCombatStats")!);
        var plain = ((IEnumerable)rowsFor.Invoke(null, [other])!).Cast<object>().Count();
        check("no Wuppy rows without Wuppy", plain == 2, $"{plain} rows for a player with no stats");
    }

    // Damage tracking: the patch is on the game's core Damage, and around it Combat Statistics' own trackers see the
    // power dealing the damage and the players aimed at (so a hit on their Osty counts for them), then are restored
    static void Damage(Assembly assembly, Action<string, bool, string> check)
    {
        var core = DamageTracking.Core;
        check("damage tracking is on the game's core Damage", core != null
            && Harmony.GetPatchInfo(core)?.Prefixes.Any(p => p.owner == ModEntry.ModId) == true, core?.ToString() ?? "not patched");
        if (core == null)
            return;
        var origin = AccessTools.PropertyGetter(assembly.GetType("CombatStatistics.Patches.DamageOriginTracking"), "Current");
        var redirected = AccessTools.DeclaredMethod(assembly.GetType("CombatStatistics.Patches.OstyAbsorptionTracking"), "TryGetRedirectedOwner");
        bool OstyCounts(Creature osty) => (bool)redirected.Invoke(null, [osty, null])!;

        var player = Player.CreateForNewRun<Necrobinder>(UnlockState.none, 990_002);
        var osty = new Creature(ModelDb.Monster<MegaCrit.Sts2.Core.Models.Monsters.Osty>().ToMutable(), CombatSide.Player, null) { PetOwner = player };
        var power = ModelDb.Power<MegaCrit.Sts2.Core.Models.Powers.StrengthPower>().ToMutable();
        var context = new BlockingPlayerChoiceContext();
        context.PushModel(power);
        var dataType = core.GetParameters()[1].ParameterType.GetGenericArguments()[0];
        var targets = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(dataType))!;
        targets.Add(Activator.CreateInstance(dataType, player.Creature));

        DamageTracking.Prefix(context, targets, core, out var state);
        check("the damage's source is the power dealing it", origin.Invoke(null, null) == power, $"{origin.Invoke(null, null)}");
        check("a hit on Osty counts for the player aimed at", OstyCounts(osty), "Osty absorbing for its owner wasn't seen");
        DamageTracking.Finalizer(null, state);
        check("both trackers restored after the damage", origin.Invoke(null, null) == null && !OstyCounts(osty), "still set");

        // the real call: no targets, so it returns at once, through the patch
        var empty = Activator.CreateInstance(typeof(List<>).MakeGenericType(dataType));
        var args = core.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
        args[0] = context;
        args[1] = empty;
        var task = (Task)core.Invoke(null, args)!;
        check("the patched Damage still runs", task.IsCompletedSuccessfully && origin.Invoke(null, null) == null, $"{task.Status}, {task.Exception?.InnerException?.Message}");
    }

    // The actions bound to a hotkey's release, lowest priority first, read from the manager's list (the same on every
    // game version, whatever its methods take)
    static List<Action> Bound(NHotkeyManager manager, StringName hotkey) =>
        AccessTools.Field(typeof(NHotkeyManager), "_hotkeyReleasedBindings").GetValue(manager) is IDictionary bindings && bindings.Contains(hotkey)
            && bindings[hotkey] is IEnumerable list
            ? list.Cast<object>().Select(b => Traverse.Create(b).Property("Action").GetValue() ?? Traverse.Create(b).Field("Action").GetValue()).OfType<Action>().ToList()
            : [];

    // What a release of the hotkey runs now: GetFiringReleasedBinding (v0.114, which checks the owner), else
    // GetPrioritizedReleasedBinding (v0.113)
    static Action? Firing(NHotkeyManager manager, StringName hotkey) =>
        (AccessTools.DeclaredMethod(typeof(NHotkeyManager), "GetFiringReleasedBinding") ?? AccessTools.DeclaredMethod(typeof(NHotkeyManager), "GetPrioritizedReleasedBinding"))
            ?.Invoke(manager, [(string)hotkey]) as Action;
}
