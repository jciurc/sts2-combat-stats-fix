// Dev runs of the game in a sandbox, apart from real play, so the real game can run through Steam at the same time.
//
//   dotnet run tools/dev.cs test [--window]   build, reset the sandbox, run SelfTest.cs with Combat Statistics and quit
//                                             (headless unless --window); prints its results and checks the log;
//                                             exit 1 on any FAIL
//   dotnet run tools/dev.cs launch            build, open the game in the sandbox with Combat Statistics, to try by
//                                             hand (until you close it); --keep-saves keeps the last run's saves
//   dotnet run tools/dev.cs smoke             the release build (dist/, from `build.cs --dist`) with Combat
//                                             Statistics, headless, until the main menu: it loads and patches, runs no dev code and logs
//                                             no warnings
//   smoke --real                              the same with your own enabled mods (local and Workshop, copied in as
//                                             local mods, code and packs hard-linked): with your mod list.
//                                             Not while the game runs: other mods write to the shared mod_configs\,
//                                             which is backed up and put back after the run
//   --stable                                 with any of them: the stable branch's game, in its own dev copy
//   --no-wait                                 with any of them: refuse at once while another dev run (any repo's)
//                                             has the game, instead of waiting up to 15 minutes for it
//
// - Combat Statistics (the mod this fixes), from your Workshop download into out/fixtures/CombatStatistics, never
//   installed: subscribe to it first. It runs with every mode, as our manifest requires it.
// - <drive>:\sts2-dev-combat-stats-fix, on the game's drive: hard links to the installed game (no extra space), with its
//   own mods\ folder holding only our build and the fixtures. --stable uses sts2-dev-stable-combat-stats-fix, linked from
//   the installed game while Steam has the stable branch, else from another stable dev copy (STS2_STABLE_DIR, or
//   sts2-dev-stable on the game's drive, as Spire Mod Manager keeps).
// - --force-steam off: data in %APPDATA%\SlayTheSpire2\default\1\, never the real saves. Its save folders (progress,
//   runs, history) are emptied before every run, `launch --keep-saves` aside: a run with other mods would leave their
//   ids in the progress file and a half-played run behind for every later run, in every repo, to trip over.
// - Results in out/selftest; the game's log in out/dev.log.
// - The game's Steam library: STS2_STEAMAPPS, else the one Steam's libraryfolders.vdf lists that has it.
// - One dev game at a time across every repo and session: the shared lock Local\sts2-dev-run (below).
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

const string ModId = "CombatStatisticsFix";
const string Base = "CombatStatistics";
const string BaseWorkshopId = "3781790213";
const int TestSeconds = 180, SmokeSeconds = 120;
var steamApps = SteamApps();
var gameDir = Path.Combine(steamApps, "common", "Slay the Spire 2");
if (!Directory.Exists(gameDir))
    return Fail($"Slay the Spire 2 isn't in {steamApps}. Set STS2_STEAMAPPS to the steamapps folder of the library that has it.");
var stable = args.Contains("--stable");
var drive = Path.GetPathRoot(gameDir) ?? @"C:\";
// not another mod's dev copy: each replaces its mods folder
var devDir = Path.Combine(drive, stable ? "sts2-dev-stable-combat-stats-fix" : "sts2-dev-combat-stats-fix");
var otherStable = Environment.GetEnvironmentVariable("STS2_STABLE_DIR") is { Length: > 0 } set ? set : Path.Combine(drive, "sts2-dev-stable");
var noWait = args.Contains("--no-wait");
var mode = args.FirstOrDefault() ?? "test";
var smoke = mode == "smoke";
var real = args.Contains("--real");

var root = Path.GetDirectoryName(ScriptDir()) ?? ".";
var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SlayTheSpire2");
var sandbox = Path.Combine(userData, "default", "1");
var outDir = Path.Combine(root, "out", "selftest");
var logPath = Path.Combine(root, "out", "dev.log");
var distDir = Path.Combine(root, "dist", ModId);
var fixturesOut = Path.Combine(root, "out", "fixtures");
// the fixture mods in load order, each with the files it ships
(string Id, string[] Files)[] fixtures = [(Base, [".dll", ".json"])];
string[] ours = [".json", ".dll"];
string[] loaded = [ModId, .. fixtures.Select(f => f.Id)];

if (mode is not ("test" or "launch" or "smoke"))
    return Fail("usage: dotnet run tools/dev.cs [test [--window]|launch [--keep-saves]|smoke [--real]] [--stable] [--no-wait]");
if (real && (!smoke || stable))
    return Fail("--real is for smoke on the beta: your mod list is made for it");
// putting mod_configs back would undo what the real game writes there meanwhile
var configs = Path.Combine(userData, "mod_configs");
var configsBackup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CombatStatisticsFix-dev", "mod_configs.bak");
if (real && RealGameRunning())
    return Fail("--real puts mod_configs back after the run, so not while the game runs: close it first");
if (real && Directory.Exists(configsBackup))
    return Fail($"A copy of mod_configs from {Directory.GetLastWriteTime(configsBackup):yyyy-MM-dd HH:mm} is still in {configsBackup}: a --real run "
        + $"stopped before putting it back. Compare it with {configs}, keep what you want, delete the copy, then run again.");
var keepSaves = args.Contains("--keep-saves");
if (keepSaves && mode != "launch")
    return Fail("--keep-saves is for launch: test and smoke always start from empty saves");
if (smoke && ours.FirstOrDefault(ext => !File.Exists(Path.Combine(distDir, ModId + ext))) is { } absent)
    return Fail($"no {ModId}{absent} in dist/{ModId}: run `dotnet run build.cs --dist` first");
// One dev game at a time across every repo and session (the guidelines' sts2-mods add-on, "One dev game at a time"):
// the shared lock, taken before anything touches the sandbox and held to the end of this script, after the game has
// exited and its cleanup is done (launch: until you close the game; the process scan below covers one started by hand).
// --no-wait refuses at once instead of waiting for it.
using var devRun = TakeDevRunLock(string.Join(' ', args.DefaultIfEmpty("test")), noWait, out var held);
if (held != null)
    return Fail(held);
// Every dev copy (ours, other mods') shares the no-Steam sandbox data, so wait for others to finish: before touching
// our copy (one may still be open from `launch`), and again right before writing the sandbox and starting ours,
// since another can start while we build and link. --no-wait refuses at once here too.
if (!WaitForOtherDevCopies())
    return Fail("another dev copy of the game is still running");

if (!smoke)
{
    var build = Process.Start("dotnet", ["run", Path.Combine(root, "build.cs"), "--no-install"]);
    build.WaitForExit();
    if (build.ExitCode != 0)
        return Fail("build failed");
}

if (LinkDevCopy() is { } noCopy)
    return Fail(noCopy);
// the fixtures, against the copy that runs (its branch's game): a member that branch lacks fails here
if (BuildFixtures() is { } unbuilt)
    return Fail(unbuilt);
SyncMods();
if (!WaitForOtherDevCopies())
    return Fail("another dev copy of the game is still running");
PrepareSandbox();
// Godot's file logger rotates whatever the project's log path names (user://logs/godot.log, the real game's logs
// folder, shared with the sandbox) even with --log-file, and keeps only a few old logs. The dev copy's own
// override.cfg (not a link) points it at out/ instead.
File.WriteAllText(Path.Combine(devDir, "override.cfg"),
    $"[debug]\n\nfile_logging/enable_file_logging=true\nfile_logging/log_path=\"{logPath.Replace('\\', '/')}\"\nfile_logging/max_log_files=2\n");

var start = new ProcessStartInfo(Path.Combine(devDir, "SlayTheSpire2.exe"), ["--force-steam", "off", "--log-file", logPath])
    { WorkingDirectory = devDir };
var windowed = mode == "launch" || (!smoke && args.Contains("--window"));
if (!windowed)
    start.ArgumentList.Add("--headless");

if (mode == "launch")
{
    Console.WriteLine($"launched the dev copy in the sandbox (log: {logPath}); other dev runs wait until you close it");
    Process.Start(start)?.WaitForExit();
    return 0;
}

Directory.CreateDirectory(outDir);
foreach (var file in Directory.GetFiles(outDir)) // emptied, not deleted: a shell may be sitting in it
    File.Delete(file);
if (File.Exists(logPath))
    File.Delete(logPath);
// smoke sets it too: a release build must ignore it
start.Environment["COMBAT_STATS_FIX_SELFTEST"] = outDir;
start.RedirectStandardOutput = start.RedirectStandardError = true;
var previousForeground = GetForegroundWindow();
// --real: mod_configs copied aside under a temporary name, so one cut short never looks whole; empty when there was
// none, so putting it back removes the one the run made
if (real)
{
    if (RealGameRunning())
        return Fail("the game started while this waited: --real puts mod_configs back after the run, so close it first");
    var partial = configsBackup + ".partial";
    if (Directory.Exists(partial))
        Directory.Delete(partial, recursive: true);
    CopyDir(configs, partial);
    Directory.CreateDirectory(partial);
    Directory.Move(partial, configsBackup);
}
var game = Process.Start(start) ?? throw new InvalidOperationException("could not start the game");
game.OutputDataReceived += (_, _) => { };
game.ErrorDataReceived += (_, _) => { };
game.BeginOutputReadLine();
game.BeginErrorReadLine();
if (smoke)
{
    var smoked = Smoke(game);
    if (real)
    {
        if (RealGameRunning())
            Console.WriteLine($"the game started during the run, so mod_configs is left as the run left it; the copy from before is in {configsBackup}");
        else
        {
            if (Directory.Exists(configs))
                Directory.Delete(configs, recursive: true);
            CopyDir(configsBackup, configs);
            Directory.Delete(configsBackup, recursive: true);
        }
    }
    return smoked;
}

// A failed startup leaves the game on its error dialog forever, and without our mod the test never starts, so once the
// log says either, give it a few seconds to get the cause down and stop it. Both streams: errors (the startup error
// among them) go to stderr.
var deadline = DateTime.Now.AddSeconds(TestSeconds);
var startupFailed = false;
var ids = string.Join('|', loaded.Select(Regex.Escape));
void Watch(object _, DataReceivedEventArgs e)
{
    if (startupFailed || e.Data is not { } line)
        return;
    if (line.Contains("Encountered error on game startup"))
        Console.WriteLine("the game reported a startup error; stopping it shortly (see out/dev.log)");
    else if (Regex.IsMatch(line, $@"Tried to load mod (with id )?({ids})[ ,]")) // the game too old, or a mod twice
        Console.WriteLine("the game couldn't load our mod or a fixture (see out/dev.log for why); stopping it shortly");
    else if (Regex.IsMatch(line, $@"Skipping loading mod ({ids}),"))
        Console.WriteLine("the sandbox settings have our mod or a fixture switched off (another dev copy wrote them?); stopping it shortly");
    else
        return;
    startupFailed = true;
    deadline = DateTime.Now.AddSeconds(15);
}
game.OutputDataReceived += Watch;
game.ErrorDataReceived += Watch;
// The self-test quits the game once it has written its results; a game that doesn't is stopped a little later.
var finished = false;
while (!game.WaitForExit(100) && DateTime.Now < deadline)
{
    if (!finished && ResultLines() is [.., "done"])
    {
        finished = true;
        deadline = DateTime.Now.AddSeconds(10);
    }
    if (windowed)
        KeepBehind(game);
}
if (!game.HasExited)
{
    Console.WriteLine(startupFailed ? "killing it after the startup error" : finished ? "the game didn't quit after the self-test, killing it"
        : $"game still running after {TestSeconds}s, killing it");
    game.Kill();
    game.WaitForExit();
}

var lines = LogLines();
var errors = OurErrors(lines);
string[] logChecks =
[
    Result("the hotkey fix logged what it redirected", lines.Any(l => l.Contains($"[{ModId}] Combat Statistics' hotkeys use the game's new signatures")), "no \"hotkeys use the game's new signatures\" line"),
    Result("nothing in the log from our mod is an error or a warning", errors.Count == 0, string.Join(" | ", errors.Take(3))),
];
var resultLines = ResultLines();
var complete = resultLines is [.., "done"];
foreach (var line in resultLines)
    Console.WriteLine(line);
if (!complete)
    Console.WriteLine("FAIL the self-test finished: " + (resultLines.Count == 0 ? "no out/selftest/results.txt" : "results.txt doesn't end with done"));
foreach (var check in logChecks)
    Console.WriteLine(check);
Console.WriteLine("--- log ---");
foreach (var line in lines.Where(l => l.Contains($"[{ModId}]") || errors.Contains(l)).Concat(complete ? [] : lines.TakeLast(15)))
    Console.WriteLine(line.Length > 400 ? line[..400] : line);
return !complete || resultLines.Concat(logChecks).Any(l => l.StartsWith("FAIL")) ? 1 : 0;

// Hard links to every installed file except mods\ (and mods_STEAMTEST\, which the game loads as Workshop items),
// rebuilt when the game's release info changes. Links the dev copy again when its source changed: the installed game
// when Steam has the copy's branch, else (stable only) another stable dev copy. Returns why there's no copy to run, or
// null.
string? LinkDevCopy()
{
    var wanted = stable ? "public" : "public-beta";
    // a branch that can't be read counts as the beta: never link an unknown install into the stable copy
    var installed = InstalledBranch();
    var source = installed == wanted || (installed == null && !stable) ? gameDir
        : stable && File.Exists(Path.Combine(otherStable, "release_info.json")) ? otherStable : null;
    var devInfo = Path.Combine(devDir, "release_info.json");
    if (source == null)
        return File.Exists(devInfo) ? null
            : (installed == null ? $"can't tell which branch Steam has installed (no app manifest in {steamApps})" : "Steam has the other branch installed")
                + $" and there's no {wanted} copy to link from: switch the game to {wanted} in Steam and run again"
                + (stable ? ", or set STS2_STABLE_DIR to a stable copy of the game" : "");
    var info = Path.Combine(source, "release_info.json");
    if (File.Exists(devInfo) && File.ReadAllText(devInfo) == File.ReadAllText(info))
        return null;
    Console.WriteLine($"linking the dev copy in {devDir} from {source}");
    if (Directory.Exists(devDir))
        Directory.Delete(devDir, recursive: true); // deleting links leaves the installed files alone
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(source, file);
        if (relative.StartsWith("mods" + Path.DirectorySeparatorChar) || relative.StartsWith("mods_STEAMTEST" + Path.DirectorySeparatorChar)
            || relative == "override.cfg")
            continue;
        Link(file, Path.Combine(devDir, relative));
    }
    return null;
}

// The branch Steam has installed ("public" is stable, "public-beta" the beta), from the game's app manifest; null when
// it can't be read.
string? InstalledBranch()
{
    var manifest = Path.Combine(steamApps, "appmanifest_2868840.acf");
    if (!File.Exists(manifest))
        return null;
    // the installed branch is MountedConfig's; UserConfig is the one picked, which may still be downloading
    var mounted = Regex.Match(File.ReadAllText(manifest), @"""MountedConfig""\s*\{([^}]*)\}");
    if (!mounted.Success)
        return null;
    var key = Regex.Match(mounted.Groups[1].Value, @"""BetaKey""\s+""([^""]*)""");
    return key.Success && key.Groups[1].Value.Length > 0 ? key.Groups[1].Value : "public";
}

// The mod this fixes, Combat Statistics, into out/fixtures/CombatStatistics from your Workshop download (subscribe to it
// first), after build.cs (which empties out/). Returns why it isn't there, or null.
string? BuildFixtures()
{
    if (Directory.Exists(fixturesOut))
        Directory.Delete(fixturesOut, recursive: true);
    var source = Path.Combine(steamApps, "workshop", "content", "2868840", BaseWorkshopId);
    var target = Path.Combine(fixturesOut, Base);
    Directory.CreateDirectory(target);
    foreach (var ext in new[] { ".dll", ".json" })
    {
        var file = Directory.Exists(source) ? Directory.GetFiles(source, Base + ext, SearchOption.AllDirectories).FirstOrDefault() : null;
        if (file == null)
            return $"no {Base}{ext} in {source}: subscribe to Combat Statistics on the Workshop";
        File.Copy(file, Path.Combine(target, Base + ext));
    }
    return null;
}

// The dev copy's mods: our build (smoke: the release in dist/), and for test and launch the fixtures; nothing from the
// real mods folder. Copied, not linked: the repo and the dev copy can be on different drives.
void SyncMods()
{
    var mods = Path.Combine(devDir, "mods");
    if (Directory.Exists(mods))
        Directory.Delete(mods, recursive: true);
    Copy(smoke ? distDir : Path.Combine(root, "out"), ModId, ours);
    // --real brings your own copy of Combat Statistics, if it's on
    if (!real)
        foreach (var (id, files) in fixtures)
            Copy(Path.Combine(fixturesOut, id), id, files);
    // --real: your enabled mods, ours aside. Every file copied, so one a mod rewrites (its config, a cache) changes only
    // the copy, but for code and packs, hard links: the running game holds those open, and packs are most of the size.
    // They have no entry in the sandbox's mod list, so they're on.
    if (real)
    {
        var off = DisabledIds();
        var workshop = Path.Combine(steamApps, "workshop", "content", "2868840");
        var sources = SubFolders(Path.Combine(gameDir, "mods")).Select(d => (d, "local-")).Concat(SubFolders(workshop).Select(d => (d, "workshop-")));
        var copied = 0;
        foreach (var (dir, prefix) in sources)
        {
            if (ManifestId(dir) is not { } id || id == ModId || off.Contains(id))
                continue;
            var to = Path.Combine(mods, prefix + Path.GetFileName(dir));
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(to, Path.GetRelativePath(dir, file));
                if (Path.GetExtension(file).ToLowerInvariant() is ".dll" or ".pck")
                    Link(file, target);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target) ?? to);
                    File.Copy(file, target);
                }
            }
            copied++;
        }
        Console.WriteLine($"copied in {copied} of your enabled mods");
    }

    static IEnumerable<string> SubFolders(string dir) => Directory.Exists(dir) ? Directory.GetDirectories(dir) : [];

    void Copy(string from, string id, string[] extensions)
    {
        var to = Path.Combine(mods, id);
        Directory.CreateDirectory(to);
        foreach (var ext in extensions)
            File.Copy(Path.Combine(from, id + ext), Path.Combine(to, id + ext));
    }
}

// Mods on, with ours and the fixtures listed in load order and on; quiet, windowed and in English (the sandbox is shared,
// and other repos' runs switch languages); its saves emptied.
void PrepareSandbox()
{
    Directory.CreateDirectory(sandbox);
    // Saves start empty (the guidelines' sts2-mods add-on): unmodded and modded profiles both.
    if (!keepSaves)
    {
        var modded = Path.Combine(sandbox, "modded");
        var saves = Directory.GetDirectories(sandbox, "profile*").Concat(Directory.Exists(modded) ? Directory.GetDirectories(modded, "profile*") : [])
            .Select(profile => Path.Combine(profile, "saves")).Where(Directory.Exists).ToList();
        var emptied = 0;
        foreach (var folder in saves)
            foreach (var entry in Directory.GetFileSystemEntries(folder))
            {
                if (Directory.Exists(entry))
                    Directory.Delete(entry, recursive: true);
                else
                    File.Delete(entry);
                emptied++;
            }
        if (emptied > 0)
            Console.WriteLine($"emptied the sandbox's saves ({emptied} item(s) in {saves.Count} folder(s))");
    }
    var settingsPath = Path.Combine(sandbox, "settings.save");
    var settings = File.Exists(settingsPath) ? JsonNode.Parse(File.ReadAllText(settingsPath))?.AsObject() ?? [] : [];
    var mods = settings["mod_settings"] as JsonObject ?? [];
    mods["mods_enabled"] = true;
    mods["mod_list"] = new JsonArray(loaded.Select(id => (JsonNode)new JsonObject
        { ["id"] = id, ["is_enabled"] = true, ["source"] = "mods_directory" }).ToArray());
    settings["mod_settings"] = mods;
    settings["seen_ea_disclaimer"] = true;
    settings["volume_master"] = 0.0;
    settings["fullscreen"] = false;
    settings["window_size"] = new JsonObject { ["X"] = 1920, ["Y"] = 1080 };
    settings["language"] = "eng";
    File.WriteAllText(settingsPath, settings.ToJsonString(new() { WriteIndented = true }));
}

// The release build alone until the main menu has loaded, plus a few seconds, then closed.
int Smoke(Process game)
{
    DateTime? menuAt = null;
    // your mod list takes minutes to load
    var giveUp = DateTime.Now.AddSeconds(real ? 900 : SmokeSeconds);
    while (!game.WaitForExit(500) && DateTime.Now < giveUp && (menuAt == null || DateTime.Now < menuAt.Value.AddSeconds(5)))
        if (menuAt == null && LogLines().Any(line => line.Contains("main menu loaded (complete)")))
            menuAt = DateTime.Now;
    var exitedEarly = game.HasExited ? $"the game exited on its own, code {game.ExitCode}" : "";
    if (!game.HasExited)
    {
        game.Kill();
        game.WaitForExit();
    }
    var lines = LogLines();
    var errors = OurErrors(lines);
    var dll = Path.Combine(distDir, ModId + ".dll");
    // build.cs writes the manifest on every run; the DLL keeps its compile time when nothing in the code changed
    var manifest = Path.Combine(distDir, ModId + ".json");
    var version = (string?)JsonNode.Parse(File.ReadAllText(manifest))?["version"] ?? "?";
    var committed = CommitTime();
    string[] checks =
    [
        Result($"dist/ was built after the checked-out commit (built {File.GetLastWriteTime(manifest):yyyy-MM-dd HH:mm})",
            committed == null || File.GetLastWriteTimeUtc(manifest) >= committed, "rebuild it with `dotnet run build.cs --dist`"),
        Result($"the release build ({version}) loaded and finished initializing", lines.Any(l => l.Contains("Finished mod initialization for") && l.EndsWith($"({ModId})."))),
        Result("its initializer logged that it started, with every patch applied",
            lines.Any(l => l.Contains($"[{ModId}] Initialized") && !l.Contains("failed")), "no \"Initialized\" line, or one naming failed patches"),
        Result("every fix patched Combat Statistics", lines.Any(l => l.Contains($"[{ModId}] Patched Combat Statistics' OverlayTheme.GameFont")) && lines.Any(l => l.Contains($"[{ModId}] Combat Statistics' hotkeys use the game's new signatures")) && lines.Any(l => l.Contains($"[{ModId}] Combat Statistics' damage sources and Osty's absorption are tracked"))),
        // the type's name is in a dev build's metadata; and a self-test that started writes its results at once
        Result("the self-test is compiled out (it ignored COMBAT_STATS_FIX_SELFTEST)",
            File.ReadAllBytes(dll).AsSpan().IndexOf("SelfTest"u8) < 0 && !Directory.EnumerateFiles(outDir).Any(),
            "dist/ has a dev build, or the self-test wrote to out/selftest"),
        Result("the game reached its main menu", menuAt != null, exitedEarly),
        Result("the game kept running until the run closed it", exitedEarly.Length == 0, exitedEarly),
        Result("nothing in the log from our mod is an error or a warning", errors.Count == 0, string.Join(" | ", errors.Take(3))),
    ];
    File.WriteAllLines(Path.Combine(outDir, "results.txt"), checks);
    foreach (var check in checks)
        Console.WriteLine(check);
    Console.WriteLine("--- log ---");
    foreach (var line in lines.Where(l => l.Contains($"[{ModId}]") || errors.Contains(l)).Concat(menuAt == null ? lines.TakeLast(15) : []))
        Console.WriteLine(line.Length > 400 ? line[..400] : line);
    return checks.Any(c => c.StartsWith("FAIL")) ? 1 : 0;
}

static string Result(string name, bool ok, string detail = "") => $"{(ok ? "PASS" : "FAIL")} {name}{(ok || detail.Length == 0 ? "" : $": {detail}")}";

// Ours: a tagged warning or error, an [ERROR] naming us, or a Godot "ERROR:" naming our code on its own line or in the
// stack lines under it (a release build has no debug info, so the first line may not).
static List<string> OurErrors(List<string> lines)
{
    var errors = new List<string>();
    for (var i = 0; i < lines.Count; i++)
    {
        var line = lines[i];
        if (line.StartsWith("ERROR:") && lines.Skip(i).TakeWhile((l, n) => n == 0 || l.StartsWith(' ')).Any(l => l.Contains(ModId)))
            errors.Add(line);
        else if ((line.Contains($"[{ModId}]") && (line.Contains("[WARN]") || line.Contains("[ERROR]"))) || (line.Contains("[ERROR]") && line.Contains(ModId)))
            errors.Add(line);
    }
    return errors;
}

// the self-test's results so far, read while the game may still be writing them
List<string> ResultLines()
{
    var results = Path.Combine(outDir, "results.txt");
    try
    {
        return File.Exists(results) ? [.. File.ReadAllLines(results).Where(l => l.Length > 0)] : [];
    }
    catch (IOException)
    {
        return [];
    }
}

// the dev log so far, read while the game may still be writing it
List<string> LogLines()
{
    if (!File.Exists(logPath))
        return [];
    using var log = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(log);
    var lines = new List<string>();
    while (reader.ReadLine() is { } line)
        lines.Add(line);
    return lines;
}

// when the checked-out commit was made (UTC), or null without git or a commit
DateTime? CommitTime()
{
    try
    {
        var git = Process.Start(new ProcessStartInfo("git", ["-C", root, "log", "-1", "--format=%ct"]) { RedirectStandardOutput = true, RedirectStandardError = true });
        var seconds = git?.StandardOutput.ReadToEnd().Trim();
        git?.WaitForExit();
        return long.TryParse(seconds, out var s) ? DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime : null;
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return null;
    }
}

// With a window: whenever it comes in front, put it at the bottom of the window stack without activating it, and hand
// focus back to the window that had it, so a test can run while you work.
void KeepBehind(Process game)
{
    game.Refresh();
    var window = game.MainWindowHandle;
    if (window == IntPtr.Zero || GetForegroundWindow() != window)
        return;
    SetWindowPos(window, new IntPtr(1) /* HWND_BOTTOM */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /* NOSIZE|NOMOVE|NOACTIVATE */);
    if (previousForeground != IntPtr.Zero)
        SetForegroundWindow(previousForeground);
}

static void Link(string target, string link)
{
    Directory.CreateDirectory(Path.GetDirectoryName(link) ?? ".");
    if (!CreateHardLink(link, target, IntPtr.Zero))
        throw new IOException($"could not link {link} -> {target} (error {Marshal.GetLastWin32Error()})");
}

bool WaitForOtherDevCopies()
{
    for (var waited = 0; OtherDevCopyRunning(); waited++)
    {
        if (noWait || waited > 900) // 15 minutes, as for the lock
            return false;
        if (waited == 0)
            Console.WriteLine("waiting for another dev copy of the game to exit...");
        Thread.Sleep(1000);
    }
    return true;
}

// any running copy of the game but the installed one, which plays through Steam and never uses the sandbox
bool OtherDevCopyRunning() => Process.GetProcessesByName("SlayTheSpire2")
    .Any(p => ExePath(p) is { } path && !path.StartsWith(gameDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

static string? ExePath(Process p)
{
    try { return p.MainModule?.FileName; }
    catch { return null; } // another user's or an elevated process
}

// The shared dev-run lock: the named mutex Local\sts2-dev-run, with %TEMP%\sts2-dev-run.json naming who holds it. Waits up
// to 15 minutes, naming the holder once (none with noWait); a holder that died left the mutex abandoned, which counts as
// free. The lock to dispose (on this thread: a mutex is released by the thread that took it), or null and why not.
static IDisposable? TakeDevRunLock(string what, bool noWait, out string? refused)
{
    refused = null;
    var holderFile = Path.Combine(Path.GetTempPath(), "sts2-dev-run.json");
    string Holder()
    {
        try
        {
            // shared every way, so a holder can still rewrite or delete it while a waiting run reads it
            using var stream = new FileStream(holderFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonNode.Parse(stream) is JsonObject held
                ? $"{held["repo"]} ({held["mode"]}, process {held["pid"]}, since {held["started"]})" : "another run";
        }
        catch
        {
            return "another run (no holder file)";
        }
    }
    var mutex = new Mutex(false, @"Local\sts2-dev-run");
    try
    {
        if (!mutex.WaitOne(0))
        {
            if (noWait)
            {
                mutex.Dispose();
                refused = $"another dev run holds the game: {Holder()} (--no-wait)";
                return null;
            }
            Console.WriteLine($"waiting for another dev run to finish (one game at a time across every repo): {Holder()}");
            if (!mutex.WaitOne(TimeSpan.FromMinutes(15)))
            {
                refused = $"another dev run still holds the game after 15 minutes: {Holder()}";
                mutex.Dispose();
                return null;
            }
        }
    }
    catch (AbandonedMutexException)
    {
        // the last holder died without letting go: ours now, and its file is stale
    }
    try
    {
        File.WriteAllText(holderFile, new JsonObject
        {
            ["repo"] = "sts2-combat-stats-fix", ["mode"] = what, ["pid"] = Environment.ProcessId, ["started"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        }.ToJsonString());
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        // waiting runs then can't name us; the lock is ours all the same
    }
    return new DevRunLock(mutex, holderFile);
}

// --real's helpers, as Mod Doctor's dev.cs has them.
// A folder's files into another (none when it doesn't exist).
static void CopyDir(string from, string to)
{
    if (!Directory.Exists(from))
        return;
    foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
    {
        var target = Path.Combine(to, Path.GetRelativePath(from, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? to);
        File.Copy(file, target, overwrite: true);
    }
}

// The id in a mod's manifest (a JSON file with "has_dll"), the shallowest one: some Workshop items keep it a level down.
static string? ManifestId(string dir)
{
    foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories).OrderBy(f => f.Count(c => c == Path.DirectorySeparatorChar)))
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(file)) is JsonObject json && json.ContainsKey("has_dll"))
                return (string?)json["id"];
        }
        catch (Exception) { }
    }
    return null;
}

// From your real mod list, where a mod with no entry is on.
HashSet<string> DisabledIds()
{
    var settings = Directory.GetFiles(Path.Combine(userData, "steam"), "settings.save", SearchOption.AllDirectories).First();
    var list = JsonNode.Parse(File.ReadAllText(settings))?["mod_settings"]?["mod_list"]?.AsArray() ?? [];
    return list.Where(e => e?["is_enabled"]?.GetValue<bool>() == false).Select(e => (string?)e?["id"] ?? "").ToHashSet();
}

// the installed game, which plays through Steam
bool RealGameRunning() => Process.GetProcessesByName("SlayTheSpire2")
    .Any(p => ExePath(p) is { } path && path.StartsWith(gameDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

static string ScriptDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path) ?? ".";

static string SteamApps()
{
    if (Environment.GetEnvironmentVariable("STS2_STEAMAPPS") is { Length: > 0 } set)
        return set;
    var steam = (OperatingSystem.IsWindows() ? Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) : null) is string { Length: > 0 } path
        ? path : @"C:\Program Files (x86)\Steam";
    var list = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
    var libraries = File.Exists(list)
        ? Regex.Matches(File.ReadAllText(list), "\"path\"\\s+\"([^\"]+)\"").Select(m => Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), "steamapps"))
        : [];
    var found = libraries.Prepend(Path.Combine(steam, "steamapps"))
        .FirstOrDefault(library => Directory.Exists(Path.Combine(library, "common", "Slay the Spire 2")));
    return Path.GetFullPath(found ?? Path.Combine(steam, "steamapps"));
}


[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

[DllImport("user32.dll")]
static extern IntPtr GetForegroundWindow();

[DllImport("user32.dll")]
static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

[DllImport("user32.dll")]
static extern bool SetForegroundWindow(IntPtr window);

// Lets the shared dev-run lock go: its holder file, then the mutex.
sealed class DevRunLock(Mutex mutex, string holderFile) : IDisposable
{
    public void Dispose()
    {
        try
        {
            File.Delete(holderFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // left for the next holder to overwrite: the mutex must go regardless
        }
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
