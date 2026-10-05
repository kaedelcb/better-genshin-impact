using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneDragonMigration.Core;

var user = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
if (output.StartsWith(user + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Probe output must be independent of the source User");
var files = Directory.GetFiles(Path.Combine(user, "OneDragon"), "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
var schedule = Path.Combine(user, "config.json");
var inputs = files.Concat(File.Exists(schedule) ? new[] { schedule } : Array.Empty<string>()).ToArray();
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
var before = inputs.ToDictionary(x => x, Hash);
var result = OneDragonMigrationEngine.Migrate(files, File.Exists(schedule) ? schedule : null, output);
var allUnchanged = before.All(x => Hash(x.Key) == x.Value);
var report = new {
    sourceUnchanged = allUnchanged,
    configCount = result.Configs.Count,
    configs = result.Configs.Select(c => new { file = Path.GetFileName(c.SourceFile), format = c.Format.ToString(), c.ParseError, taskCount = c.Tasks.Count,
        outputExists = File.Exists(Path.Combine(result.CandidateDir!, "standard", "OneDragon", c.OutputName + ".json")) }),
    blockers = result.ActivationBlockers,
    issues = result.Issues,
    dropped = result.Dropped.Select(x => new { x.Config, x.Field, x.Reason }),
    candidateDirectory = result.CandidateDir
};
File.WriteAllText(Path.Combine(output, "probe-result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(report));
if (!allUnchanged) return 2;
return result.ActivationBlockers.Count == 0 ? 0 : 3;
