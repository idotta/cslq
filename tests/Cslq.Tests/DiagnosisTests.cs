namespace Cslq.Tests;

/// <summary>
/// The interpretation of a failed design-time build. The real repro needs an SDK that is not installed, which
/// <c>probes/run.sh</c> stages in a throwaway tree with a <c>global.json</c> pinning 9.9.900;
/// what is pinned here is the reading of what that probe finds, because the branch order is
/// the part that can go quietly wrong — an absent restore reported as an SDK mismatch sends
/// the reader to the wrong file.
/// </summary>
public class DiagnosisTests
{
    /// <summary>
    /// Measured 2026-09-10 against a root pinning an SDK nobody has: this is the shape of the
    /// stderr, boilerplate first and the answer four lines down.
    /// </summary>
    private const string SdkNotFound = """
        The command could not be loaded, possibly because:
          * You intended to execute a .NET application:
              The application '--version' does not exist or is not a managed .dll or .exe.
          * You intended to execute a .NET SDK command:
              A compatible .NET SDK was not found.

        Requested SDK version: 9.9.900
        global.json file: C:\repo\global.json

        Installed SDKs:
        """;

    [Fact]
    public void The_sdk_summary_skips_the_boilerplate_and_keeps_what_names_the_cause()
    {
        var summary = Diagnosis.Summary(SdkNotFound);

        Assert.Contains("A compatible .NET SDK was not found.", summary);
        Assert.Contains("Requested SDK version: 9.9.900", summary);
        Assert.Contains("global.json", summary);
        Assert.DoesNotContain("The command could not be loaded", summary);
    }

    [Fact]
    public void An_unfamiliar_failure_falls_back_to_its_first_line()
    {
        Assert.Equal("something else went wrong", Diagnosis.Summary("\n\nsomething else went wrong\nand more"));
    }

    [Fact]
    public void A_failing_sdk_is_the_cause_even_when_nothing_is_restored()
    {
        var cause = Diagnosis.Cause(new Diagnosis.Sdk(155, Diagnosis.Summary(SdkNotFound)), ["Lib"]);

        Assert.Contains("the .NET SDK cannot run in this root", cause);
        Assert.Contains("A compatible .NET SDK was not found.", cause);
        // The number is deliberately absent: Windows reports this exit as -2147450725.
        Assert.DoesNotContain("155", cause);
    }

    [Fact]
    public void A_missing_project_assets_file_is_the_cause_when_the_sdk_is_fine()
    {
        var cause = Diagnosis.Cause(new Diagnosis.Sdk(0, "10.0.301"), ["Lib", "App"]);

        Assert.Contains("no restore output under Lib, App", cause);
        Assert.Contains("project.assets.json", cause);
    }

    /// <summary>
    /// A long list is a message, not an inventory: the failure already names every pending
    /// project one per line below this.
    /// </summary>
    [Fact]
    public void A_long_unrestored_list_is_capped()
    {
        var cause = Diagnosis.Cause(new Diagnosis.Sdk(0, "10.0.301"), ["a", "b", "c", "d", "e", "f"]);

        Assert.Contains("a, b, c, d and 2 more", cause);
    }

    /// <summary>
    /// Nothing found still says something: the load finished and every project compiled to
    /// nothing, which is the fact the caller could not otherwise see. A design-time build
    /// failure is reported to the server and never to us.
    /// </summary>
    [Fact]
    public void With_nothing_found_the_reading_itself_is_the_cause()
    {
        var cause = Diagnosis.Cause(new Diagnosis.Sdk(0, "10.0.301"), []);

        Assert.Contains("every project compiled to nothing", cause);
    }

    [Fact]
    public void A_dotnet_that_could_not_be_launched_is_not_reported_as_a_mismatch()
    {
        // Null is what LspClient answers when the launch itself failed -- `dotnet` off PATH
        // has its own message and must not be re-reported here as an SDK problem.
        var cause = Diagnosis.Cause(null, []);

        Assert.Contains("every project compiled to nothing", cause);
        Assert.DoesNotContain("dotnet --version", cause);
    }

    [Fact]
    public void Unrestored_names_the_projects_with_no_assets_file_and_skips_the_rest()
    {
        using var ws = new Workspace();
        var restored = ws.Project("Restored");
        var bare = ws.Project("Bare");
        Directory.CreateDirectory(Path.Combine(restored, "obj"));
        File.WriteAllText(Path.Combine(restored, "obj", "project.assets.json"), "{}");

        Assert.Equal(["Bare"], Diagnosis.Unrestored(ws.Root, [restored, bare]));
    }
}
