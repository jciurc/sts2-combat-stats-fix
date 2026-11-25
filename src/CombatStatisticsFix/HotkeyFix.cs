using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

namespace CombatStatisticsFix;

// Game v0.113 changed NHotkeyManager's Push/RemoveHotkeyReleasedBinding (a new optional sequence parameter, and
// Remove now returns a long), and v0.114.0 changed them again (an owner node before the action: a binding fires only
// while its owner is visible). Combat Statistics 0.5.1 was built against the old signatures, so the four small methods
// that bind its Continue (confirm) and View Map hotkeys throw MissingMethodException, and Continue never closes its
// end-of-combat overlay. Harmony can't patch those methods (copying their bodies hits the missing method), so this
// rewrites their callers to call the stand-ins below instead, which do the same through whichever signature the game
// has. A method that still compiles (an older game, or a Combat Statistics that fixed it) is left alone.
static class HotkeyFix
{
    // the broken method's type and name, and its stand-in here
    static readonly (string Type, string Method, string StandIn)[] Leaves =
    [
        ("CombatStatistics.RunSummaryOverlay", "RefreshContinueHotkeyPriority", nameof(RefreshContinue)),
        ("CombatStatistics.RunSummaryOverlay", "UnregisterContinueHotkey", nameof(UnregisterContinue)),
        ("CombatStatistics.Patches.RunHistoryScreenPatch", "RegisterMapHotkey", nameof(RegisterMap)),
        ("CombatStatistics.Patches.RunHistoryScreenPatch", "UnregisterMapHotkey", nameof(UnregisterMap)),
    ];

    internal static readonly Dictionary<MethodBase, MethodInfo> Redirects = [];
    internal static readonly List<string> Callers = [];
    static FieldInfo? _continueManager, _continueAction, _mapManager, _mapAction, _overlayCanvas, _historyScreen;
    static Node? _continueOwner, _mapOwner;
    static MethodInfo? _onMapPressed, _push, _remove;
    static bool _reported;

    public static void Apply(Harmony harmony, Assembly assembly)
    {
        foreach (var (typeName, methodName, standIn) in Leaves)
            if (assembly.GetType(typeName) is { } type && AccessTools.DeclaredMethod(type, methodName) is { } leaf && Broken(leaf))
                Redirects[leaf] = AccessTools.DeclaredMethod(typeof(HotkeyFix), standIn);
        if (Redirects.Count == 0)
            return;
        var overlay = assembly.GetType("CombatStatistics.RunSummaryOverlay");
        var history = assembly.GetType("CombatStatistics.Patches.RunHistoryScreenPatch");
        _continueManager = overlay is null ? null : AccessTools.Field(overlay, "_continueHotkeyManager");
        _continueAction = overlay is null ? null : AccessTools.Field(overlay, "_continueHotkeyAction");
        _mapManager = history is null ? null : AccessTools.Field(history, "_mapHotkeyManager");
        _mapAction = history is null ? null : AccessTools.Field(history, "_mapHotkeyAction");
        _onMapPressed = history is null ? null : AccessTools.DeclaredMethod(history, "OnPressed");
        _overlayCanvas = overlay is null ? null : AccessTools.Field(overlay, "_current");
        _historyScreen = history is null ? null : AccessTools.Field(history, "_screen");
        _push = Binding("PushHotkeyReleasedBinding");
        _remove = Binding("RemoveHotkeyReleasedBinding");
        if (_push is null || _remove is null)
        {
            Log.Warn($"[{ModEntry.ModId}] NHotkeyManager has no Push/RemoveHotkeyReleasedBinding(string, [Node,] Action, ...): Combat Statistics' hotkeys can't be fixed");
            return;
        }

        var tokens = Redirects.Keys.Select(m => m.MetadataToken).ToHashSet();
        Callers.Clear();
        foreach (var method in Methods(assembly))
        {
            if (Redirects.ContainsKey(method) || !Calls(method, tokens))
                continue;
            try
            {
                harmony.Patch(method, transpiler: new HarmonyMethod(typeof(HotkeyFix), nameof(Redirect)));
                Callers.Add($"{method.DeclaringType?.Name}.{method.Name}");
            }
            catch (Exception e)
            {
                Log.Warn($"[{ModEntry.ModId}] Couldn't redirect {method.DeclaringType?.Name}.{method.Name}: {e.Message}");
            }
        }
        Log.Info($"[{ModEntry.ModId}] Combat Statistics' hotkeys use the game's new signatures: {string.Join(", ", Redirects.Keys.Select(m => m.Name))}, from {Callers.Count} caller(s)");
    }

    // Harmony reads a method body by resolving every token in it, which throws on a reference to a missing method
    static bool Broken(MethodBase method)
    {
        try
        {
            PatchProcessor.ReadMethodBody(method).ToList();
            return false;
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException || e.InnerException is MissingMemberException or TypeLoadException)
        {
            return true;
        }
    }

    // A cheap scan of the raw IL for call, callvirt or ldftn on one of the leaves (same module, so the tokens match);
    // a stray match only costs a transpiler pass that changes nothing
    static bool Calls(MethodBase method, HashSet<int> tokens)
    {
        byte[]? il;
        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray();
        }
        catch
        {
            return false;
        }
        if (il is null)
            return false;
        for (var i = 0; i + 4 < il.Length; i++)
        {
            var operand = i + 1;
            if (il[i] is 0x28 or 0x6F) { }
            else if (il[i] == 0xFE && il[i + 1] == 0x06) operand = i + 2;
            else continue;
            if (operand + 4 <= il.Length && tokens.Contains(BitConverter.ToInt32(il, operand)))
                return true;
        }
        return false;
    }

    static IEnumerable<MethodBase> Methods(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            types = e.Types.OfType<Type>().ToArray();
        }
        const BindingFlags all = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var type in types)
        {
            if (type.IsGenericTypeDefinition)
                continue;
            foreach (var method in type.GetMethods(all).Where(m => !m.IsAbstract && !m.ContainsGenericParameters))
                yield return method;
            foreach (var ctor in type.GetConstructors(all))
                yield return ctor;
        }
    }

    static IEnumerable<CodeInstruction> Redirect(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.operand is MethodBase target && Redirects.TryGetValue(target, out var standIn))
                instruction.operand = standIn;
            yield return instruction;
        }
    }

    // (string hotkey, Action action, ...) on v0.113, (string hotkey, Node owner, Action action, ...) since v0.114: the
    // binding fires only while its owner is visible in the tree
    static MethodInfo? Binding(string name) =>
        typeof(NHotkeyManager).GetMethods(BindingFlags.Instance | BindingFlags.Public).FirstOrDefault(m =>
            m.Name == name && m.GetParameters() is { Length: >= 2 } p && p[0].ParameterType == typeof(string)
            && ActionAt(p) is { } at && p.Skip(at + 1).All(x => x.IsOptional));

    static int? ActionAt(ParameterInfo[] p) =>
        p[1].ParameterType == typeof(Action) ? 1
        : p.Length >= 3 && typeof(Node).IsAssignableFrom(p[1].ParameterType) && p[2].ParameterType == typeof(Action) ? 2
        : null;

    static void Call(MethodInfo? binding, object manager, StringName hotkey, Node? owner, Action action)
    {
        if (binding is null || binding.GetParameters() is not { } parameters || ActionAt(parameters) is not { } at)
            return;
        var args = new object?[parameters.Length];
        args[0] = (string)hotkey;
        if (at == 2)
            args[1] = owner;
        args[at] = action;
        for (var i = at + 1; i < args.Length; i++)
            args[i] = parameters[i].DefaultValue;
        binding.Invoke(manager, args);
    }

    // RunSummaryOverlay.RefreshContinueHotkeyPriority: move the Continue binding to the top. Its owner is the overlay's
    // canvas (RunSummaryOverlay._current, set before the hotkey is registered), kept so the remove matches the push.
    static void RefreshContinue() => Guard(() =>
    {
        if (_continueManager?.GetValue(null) is { } manager && _continueAction?.GetValue(null) is Action action)
        {
            Call(_remove, manager, MegaInput.confirm, _continueOwner, action);
            _continueOwner = _overlayCanvas?.GetValue(null) as Node ?? _continueOwner;
            Call(_push, manager, MegaInput.confirm, _continueOwner, action);
        }
    });

    // RunSummaryOverlay.UnregisterContinueHotkey
    static void UnregisterContinue() => Guard(() =>
    {
        if (_continueManager?.GetValue(null) is { } manager && _continueAction?.GetValue(null) is Action action)
            Call(_remove, manager, MegaInput.confirm, _continueOwner, action);
        _continueManager?.SetValue(null, null);
        _continueAction?.SetValue(null, null);
        _continueOwner = null;
    });

    // RunHistoryScreenPatch.RegisterMapHotkey; owned by the Run History screen (RunHistoryScreenPatch._screen)
    internal static void RegisterMap() => Guard(() =>
    {
        UnregisterMap();
        var manager = NHotkeyManager.Instance;
        var action = _onMapPressed is null ? null : (Action)Delegate.CreateDelegate(typeof(Action), _onMapPressed);
        _mapManager?.SetValue(null, manager);
        _mapAction?.SetValue(null, action);
        _mapOwner = _historyScreen?.GetValue(null) as Node;
        if (manager != null && action != null)
            Call(_push, manager, MegaInput.viewMap, _mapOwner, action);
    });

    // RunHistoryScreenPatch.UnregisterMapHotkey
    static void UnregisterMap() => Guard(() =>
    {
        if (_mapManager?.GetValue(null) is { } manager && _mapAction?.GetValue(null) is Action action)
            Call(_remove, manager, MegaInput.viewMap, _mapOwner, action);
        _mapManager?.SetValue(null, null);
        _mapAction?.SetValue(null, null);
        _mapOwner = null;
    });

    // A hotkey failure shouldn't take the overlay down with it: log once, carry on
    static void Guard(Action body)
    {
        try
        {
            body();
        }
        catch (Exception e)
        {
            if (_reported)
                return;
            _reported = true;
            Log.Warn($"[{ModEntry.ModId}] A Combat Statistics hotkey couldn't be bound: {e}");
        }
    }
}
