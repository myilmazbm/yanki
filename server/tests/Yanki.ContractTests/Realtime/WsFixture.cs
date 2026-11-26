using System.Text.Json;

namespace Yanki.ContractTests.Realtime;

/// <summary>
/// A golden flow from <c>api/fixtures/ws</c>. See <c>docs/protocol.md</c> for the base format and
/// <c>docs/realtime.md</c> for the two optional extensions this repository adds
/// (<c>close</c> and the <c>server-action</c> entry).
/// </summary>
internal sealed class WsFixture
{
    private const string FixtureDirectory = "api/fixtures/ws";

    private WsFixture(string name, JsonDocument document)
    {
        Name = name;
        Document = document;
    }

    public string Name { get; }

    public JsonDocument Document { get; }

    public string Description => Document.RootElement.GetProperty("description").GetString() ?? string.Empty;

    public JsonElement Frames => Document.RootElement.GetProperty("frames");

    /// <summary>Close code the server is expected to send after the listed frames, when any.</summary>
    public int? ExpectedCloseCode =>
        Document.RootElement.TryGetProperty("close", out JsonElement close)
            ? close.GetProperty("code").GetInt32()
            : null;

    public static WsFixture Load(string name)
    {
        string path = Path.Combine(Directory, name);
        Assert.True(File.Exists(path), $"Fixture '{name}' does not exist at {path}.");
        return new WsFixture(name, JsonDocument.Parse(File.ReadAllBytes(path)));
    }

    /// <summary>Every fixture file, so a new one is picked up without touching the test.</summary>
    public static IEnumerable<string> AllNames() =>
        System.IO.Directory.EnumerateFiles(Directory, "*.json")
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .Order(StringComparer.Ordinal);

    /// <summary>
    /// Resolves <c>api/fixtures/ws</c> by walking up from the test assembly, so the suite works from
    /// any working directory and from a git worktree.
    /// </summary>
    public static string Directory { get; } = Resolve();

    private static string Resolve()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, FixtureDirectory.Replace('/', Path.DirectorySeparatorChar));
            if (System.IO.Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not find '{FixtureDirectory}' above {AppContext.BaseDirectory}.");
    }
}
