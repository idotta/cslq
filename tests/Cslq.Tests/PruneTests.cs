namespace Cslq.Tests;

/// <summary>
/// The global packages folder is shared with everything else <c>dotnet</c> has ever restored,
/// so what the prune touches has to be exact: the server's own packages, every RID, and
/// nothing else. A temp tree is the whole of what these need.
/// </summary>
public class PruneTests : IDisposable
{
    private readonly string _packages =
        Path.Combine(Path.GetTempPath(), "cslq-tests", Path.GetRandomFileName());

    private string Package(string id, string version)
    {
        var dir = Path.Combine(_packages, id, version);
        Directory.CreateDirectory(Path.Combine(dir, "tools"));
        File.WriteAllText(Path.Combine(dir, $"{id}.{version}.nupkg.sha512"), "x");
        File.WriteAllText(Path.Combine(dir, "tools", "a.dll"), "x");
        return dir;
    }

    [Fact]
    public void Every_other_version_of_the_shim_and_every_rid_goes_and_the_pin_stays()
    {
        var pin = "5.12.0-1.26426.8";
        var old = "5.11.0-2.26311.5";
        Package("roslyn-language-server", pin);
        Package("roslyn-language-server", old);
        Package("roslyn-language-server.win-x64", pin);
        Package("roslyn-language-server.win-x64", old);
        Package("roslyn-language-server.linux-x64", old);

        var result = Prune.Run(_packages, pin);

        Assert.Equal(
            [
                $"roslyn-language-server.linux-x64/{old}",
                $"roslyn-language-server.win-x64/{old}",
                $"roslyn-language-server/{old}",
            ],
            result.Removed);
        Assert.Empty(result.Kept);
        Assert.True(Directory.Exists(Path.Combine(_packages, "roslyn-language-server", pin)));
        Assert.True(Directory.Exists(Path.Combine(_packages, "roslyn-language-server.win-x64", pin)));
        Assert.False(Directory.Exists(Path.Combine(_packages, "roslyn-language-server.win-x64", old)));
    }

    /// <summary>
    /// The prefix match must not reach a package that merely starts with the same words, and
    /// nothing outside the server's packages is ever a candidate.
    /// </summary>
    [Fact]
    public void Other_packages_are_never_candidates()
    {
        Package("roslyn-language-server", "1.0.0");
        var lookalike = Package("roslyn-language-serverish", "1.0.0");
        var unrelated = Package("streamjsonrpc", "1.0.0");

        Assert.Equal(["roslyn-language-server/1.0.0"], Prune.Run(_packages, "2.0.0").Removed);
        Assert.True(Directory.Exists(lookalike));
        Assert.True(Directory.Exists(unrelated));
    }

    /// <summary>NuGet lower-cases the version directory; the manifest need not.</summary>
    [Fact]
    public void The_pin_is_matched_ignoring_case()
    {
        var dir = Package("roslyn-language-server.osx-arm64", "5.12.0-1.26426.8");
        Assert.Empty(Prune.Run(_packages, "5.12.0-1.26426.8".ToUpperInvariant()).Removed);
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void A_missing_packages_folder_is_nothing_to_do()
    {
        var result = Prune.Run(Path.Combine(_packages, "absent"), "1.0.0");
        Assert.Empty(result.Removed);
        Assert.Empty(result.Kept);
    }

    [Fact]
    public void The_pin_is_read_out_of_the_manifest()
    {
        const string manifest = """
            {
              "version": 1,
              "isRoot": true,
              "tools": {
                "roslyn-language-server": {
                  "version": "5.12.0-1.26426.8",
                  "commands": [ "roslyn-language-server" ],
                  "rollForward": false
                }
              }
            }
            """;
        Assert.Equal("5.12.0-1.26426.8", Prune.PinnedVersion(manifest));
        Assert.Null(Prune.PinnedVersion("""{ "version": 1, "tools": {} }"""));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_packages, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
