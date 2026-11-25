// Stage this folder for Mega Crit's ModUploader: content/ from dist/ (dotnet run build.cs --dist first), and the
// description from description.bbcode, its {version}, {id} and {author} filled from the release manifest, into
// workshop.json (the uploader reads the description only from there). Usage: dotnet run workshop/prepare.cs
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

const string ModId = "CombatStatisticsFix";
var here = ScriptDir();
var dist = Path.Combine(here, "..", "dist", ModId);
var manifestPath = Path.Combine(dist, ModId + ".json");
if (!File.Exists(manifestPath) || !File.Exists(Path.Combine(dist, ModId + ".dll")))
    return Fail($"no release in {dist}: run dotnet run build.cs --dist first");
var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
if (((string?)manifest["version"])?.Contains("-dev") != false)
    return Fail("the release manifest has a dev version: run dotnet run build.cs --dist");

var content = Path.Combine(here, "content");
if (Directory.Exists(content))
    Directory.Delete(content, true);
Directory.CreateDirectory(content);
foreach (var file in Directory.GetFiles(dist))
    File.Copy(file, Path.Combine(content, Path.GetFileName(file)));

var description = File.ReadAllText(Path.Combine(here, "description.bbcode")).Replace("\r\n", "\n").TrimEnd()
    .Replace("{version}", (string?)manifest["version"]).Replace("{id}", (string?)manifest["id"]).Replace("{author}", (string?)manifest["author"]);
if (System.Text.Encoding.UTF8.GetByteCount(description) >= 8000)
    return Fail("the description is 8,000 bytes or more: Steam cuts it");
var workshopPath = Path.Combine(here, "workshop.json");
var workshop = JsonNode.Parse(File.ReadAllText(workshopPath))!.AsObject();
workshop["description"] = description;
File.WriteAllText(workshopPath, workshop.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
Console.WriteLine($"staged {manifest["version"]}: content/ ({string.Join(", ", Directory.GetFiles(content).Select(Path.GetFileName))}), workshop.json");

// the checks before an upload: each failure is listed, and only a clean run ends with "Ready."
var problems = new List<string>();
var image = Path.Combine(here, "image.png");
if (!File.Exists(image))
    problems.Add("image.png (the thumbnail) is missing");
else
{
    var png = File.ReadAllBytes(image);
    // a PNG's width and height are big-endian at bytes 16 and 20 (its IHDR chunk)
    int Size(int at) => png.Length >= at + 4 ? png[at] << 24 | png[at + 1] << 16 | png[at + 2] << 8 | png[at + 3] : 0;
    if (png.Length < 24 || png[1] != 'P' || png[2] != 'N' || png[3] != 'G')
        problems.Add("image.png isn't a PNG");
    else if (Size(16) != Size(20) || Size(16) is < 512 or > 1024)
        problems.Add($"image.png is {Size(16)}x{Size(20)}: it should be square, 512 to 1024 px");
    if (png.Length >= 1024 * 1024)
        problems.Add($"image.png is {png.Length / 1024} KB: it should be under 1 MB");
}
var changeNote = (string?)workshop["changeNote"];
if (changeNote?.StartsWith($"{manifest["version"]}: ") != true)
    problems.Add($"changeNote should start with \"{manifest["version"]}: \", but is \"{changeNote}\"");
if (!File.Exists(Path.Combine(here, "mod_id.txt")))
    problems.Add("no mod_id.txt: the upload would make a new Workshop item (fine only for the very first upload)");
var visibility = (string?)workshop["visibility"];
Console.WriteLine($"title: {workshop["title"]}\nvisibility: {visibility}\nbranches: {workshop["minBranch"] ?? "any"} to {workshop["maxBranch"] ?? "any"}"
    + $"\ndependencies: {workshop["dependencies"]?.ToJsonString()}\nchangeNote: {changeNote}\ndescription: {System.Text.Encoding.UTF8.GetByteCount(description)} bytes");
if (problems.Count > 0)
    return Fail(string.Join("\n", problems.Select(p => "- " + p)));
Console.WriteLine("Ready.");
return 0;

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

static string ScriptDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path) ?? ".";
