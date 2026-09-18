using System.Text.Json;

namespace Cslq.Tests;

/// <summary>
/// What the client keeps of <c>window/logMessage</c>. Roslyn reports the design-time build's
/// failure there and nowhere a response can carry, so discarding the payload was the reason a
/// readiness failure could only ever say "every project answered empty" and guess at the rest.
/// The notification is a one-way call over a live wire, which is exactly what a unit test is
/// worst at staging — so the handler is driven directly and the wire is a probe leg's job.
/// </summary>
public class ServerLogTests
{
    [Fact]
    public void Nothing_logged_adds_nothing_to_the_message()
    {
        Assert.Equal(string.Empty, new LspClient.Endpoints().LogTail());
    }

    [Fact]
    public void An_error_is_kept_and_labelled()
    {
        var endpoints = Logged((1, "MSB4166: Child node exited prematurely"));

        Assert.Equal(
            "\n--- server log ---\nerror: MSB4166: Child node exited prematurely",
            endpoints.LogTail());
    }

    /// <summary>
    /// Info and Log are the load's running commentary — one line per project and more — and
    /// keeping them would push the two lines worth reading out of a bounded tail.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void The_running_commentary_is_not_kept(int type)
    {
        Assert.Equal(string.Empty, Logged((type, "Loading Core.csproj")).LogTail());
    }

    [Fact]
    public void A_warning_is_kept_and_labelled_as_one()
    {
        Assert.Contains("warning: NU1701", Logged((2, "NU1701")).LogTail());
    }

    /// <summary>
    /// A failing design-time build repeats itself per project, and the repeat says nothing the
    /// first one did not.
    /// </summary>
    [Fact]
    public void A_repeated_line_is_kept_once()
    {
        var tail = Logged((1, "MSB4166"), (1, "MSB4166"), (1, "MSB4166")).LogTail();

        Assert.Equal("\n--- server log ---\nerror: MSB4166", tail);
    }

    /// <summary>
    /// Bounded, and the end is the end that matters: the lines before the wait gave up.
    /// </summary>
    [Fact]
    public void Only_the_last_lines_are_kept()
    {
        var endpoints = new LspClient.Endpoints();
        for (var i = 0; i < 20; i++) Log(endpoints, 1, $"failure {i}");

        var lines = endpoints.LogTail().Split('\n')[2..];

        Assert.Equal(8, lines.Length);
        Assert.Equal("error: failure 12", lines[0]);
        Assert.Equal("error: failure 19", lines[^1]);
    }

    /// <summary>
    /// The failure message is read a line at a time, and the server's log messages carry
    /// embedded newlines — a stack trace behind a one-line summary.
    /// </summary>
    [Fact]
    public void A_multi_line_message_becomes_one_line()
    {
        var tail = Logged((1, "boom\n   at Roslyn.Thing()\n   at More()")).LogTail();

        Assert.Single(tail.Split('\n')[2..]);
        Assert.Contains("boom    at Roslyn.Thing()    at More()", tail);
    }

    [Fact]
    public void A_long_message_is_elided()
    {
        var tail = Logged((1, new string('x', 500))).LogTail();

        // "error: " + 300 + the ellipsis.
        Assert.Equal(308, tail.Split('\n')[2].Length);
        Assert.EndsWith("…", tail);
    }

    /// <summary>
    /// A notification that fails to bind takes the notification with it, so a payload this does
    /// not recognise costs a skipped log line rather than a dropped one.
    /// </summary>
    [Theory]
    [InlineData("""{"type":1}""")]
    [InlineData("""{"message":"no type"}""")]
    [InlineData("""{"type":"1","message":"a string type"}""")]
    [InlineData("""{"type":1,"message":null}""")]
    [InlineData("""{"type":1,"message":""}""")]
    [InlineData("""{"type":0,"message":"below the range"}""")]
    [InlineData("""{}""")]
    public void A_payload_it_cannot_read_is_skipped_rather_than_thrown(string payload)
    {
        var endpoints = new LspClient.Endpoints();
        using var document = JsonDocument.Parse(payload);

        endpoints.OnLogMessage(document.RootElement);

        Assert.Equal(string.Empty, endpoints.LogTail());
    }

    private static LspClient.Endpoints Logged(params (int Type, string Message)[] messages)
    {
        var endpoints = new LspClient.Endpoints();
        foreach (var (type, message) in messages) Log(endpoints, type, message);
        return endpoints;
    }

    private static void Log(LspClient.Endpoints endpoints, int type, string message)
    {
        using var document = JsonDocument.Parse(
            $$"""{"type":{{type}},"message":{{JsonSerializer.Serialize(message)}}}""");
        endpoints.OnLogMessage(document.RootElement);
    }
}
