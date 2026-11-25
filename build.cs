// Build CombatStatisticsFix (DLL + manifest) and install it into the game's mods folder.
// Usage: dotnet run build.cs [--no-install]     (needs the .NET 10 SDK; close the game first to install)
//        dotnet run build.cs --dist             the release: ReleaseVersion, no dev code, in dist/CombatStatisticsFix/
//                                               for the Workshop upload; never installs
// The Steam library with the game: STS2_STEAMAPPS, else the one Steam's libraryfolders.vdf lists that has it.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

const string ModId = "CombatStatisticsFix";
// raised by hand in the release PR; dev builds carry their own version below
const string ReleaseVersion = "0.2.0";
var Steam = SteamApps();
if (!Directory.Exists(Path.Combine(Steam, "common", "Slay the Spire 2")))
    return Fail($"Slay the Spire 2 isn't in {Steam}. Set STS2_STEAMAPPS to the steamapps folder of the library that has it, "
        + @"for example D:\SteamLibrary\steamapps.");
var root = ScriptDir();
var installDir = Path.Combine(Steam, "common", "Slay the Spire 2", "mods", ModId);
var project = Path.Combine(root, "src", ModId, ModId + ".csproj");
var outDir = Path.Combine(root, "out");
var dist = args.Contains("--dist");

var manifest = new JsonObject
{
    ["id"] = ModId,
    ["name"] = "Combat Statistics Fix (unofficial)",
    ["author"] = "symphony",
    ["description"] = "Fixes Combat Statistics on game v0.113 and later: Continue not closing its overlay, the overlay showing only for the first fight, and missing damage sources and Osty stats. Adds Wuppy's stats. An unofficial fix; needs Combat Statistics.",
    // N goes up by one with each install; after a release, the next version's -dev.0, so it stays above the Workshop's
    // copy (a -dev build sorts below its own release)
    ["version"] = dist ? ReleaseVersion : "0.2.0-dev.5",
    // no player text, scenes or images: nothing to pack
    ["has_pck"] = false,
    ["has_dll"] = true,
    // only what one player's own overlay shows: players of a multiplayer run don't all need it
    ["affects_gameplay"] = false,
    // required, so Combat Statistics stays credited; the fix still waits for it to load (ModEntry), in either order
    ["dependencies"] = new JsonArray(new JsonObject { ["id"] = "CombatStatistics", ["min_version"] = "0.5.1" }),
    ["min_game_version"] = "0.111.0",
};
string[] shipped = [".json", ".dll"];

// Emptied rather than deleted: Windows won't delete a folder a shell is sitting in, so such a folder stays (empty).
static void Empty(string dir)
{
    foreach (var file in Directory.GetFiles(dir))
        File.Delete(file);
    foreach (var sub in Directory.GetDirectories(dir))
    {
        Empty(sub);
        try { Directory.Delete(sub); }
        catch (IOException) { }
    }
}
Directory.CreateDirectory(outDir);
Empty(outDir);

File.WriteAllText(Path.Combine(outDir, ModId + ".json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

// CombatStatisticsFixWorkshop leaves the dev-only code (SelfTest.cs) and debug info out of the release.
var build = Process.Start("dotnet", ["build", project, "-c", "Release", "-nologo", "-v", "q",
    $"-p:SteamLibrary={Steam}", $"-p:CombatStatisticsFixWorkshop={(dist ? "true" : "false")}", $"-o:{Path.Combine(outDir, "bin")}"]);
build.WaitForExit();
if (build.ExitCode != 0)
    return Fail("dotnet build failed");
File.Copy(Path.Combine(outDir, "bin", ModId + ".dll"), Path.Combine(outDir, ModId + ".dll"));

if (dist)
{
    // only this folder is emptied: zips of earlier releases beside it stay
    var distDir = Path.Combine(root, "dist", ModId);
    Directory.CreateDirectory(distDir);
    Empty(distDir);
    foreach (var ext in shipped)
        File.Copy(Path.Combine(outDir, ModId + ext), Path.Combine(distDir, ModId + ext));
    var found = Directory.GetFileSystemEntries(distDir).Select(Path.GetFileName).Order().ToList();
    var expected = shipped.Select(ext => ModId + ext).Order().ToList();
    if (!found.SequenceEqual(expected))
        return Fail($"dist/{ModId} must hold exactly {string.Join(", ", expected)}, but holds: {string.Join(", ", found)}");
    Console.WriteLine($"release {ReleaseVersion} in {distDir}");
    return 0;
}

if (!args.Contains("--no-install"))
{
    // Not while any copy of the game runs (the real one, or a dev copy); and each file is deleted, then copied, never
    // overwritten in place, so a game holding the old file keeps it whole.
    if (Process.GetProcessesByName("SlayTheSpire2").Length > 0)
        return Fail("not installing: the game is running (or a dev copy of it); close it first");
    Directory.CreateDirectory(installDir);
    try
    {
        foreach (var ext in shipped)
        {
            var target = Path.Combine(installDir, ModId + ext);
            File.Delete(target);
            File.Copy(Path.Combine(outDir, ModId + ext), target);
        }
    }
    catch (IOException e)
    {
        return Fail($"install failed (is the game running?): {e.Message}");
    }
    Console.WriteLine($"installed to {installDir}");
}
else
    Console.WriteLine($"built {manifest["version"]} in {outDir}");
return 0;

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
