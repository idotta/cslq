using System.Text.Json;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace Cslq.Tests;

/// <summary>
/// The session's pure parts: which attach a pipe name stands for, what goes on the wire, and
/// the argv normalisation that makes a request independent of the caller's directory.
/// Everything that needs a live session is a probe leg.
/// </summary>
public class SessionTests
{
    private const string Root = "/w/repo";

    private static string Name(
        string root = Root, string level = "Warning", bool daemon = true, string version = "0.2.0") =>
        Session.PipeName(root, level, daemon, version);

    [Fact]
    public void One_key_is_one_pipe_name()
    {
        Assert.Equal(Name(), Name());
        Assert.StartsWith("cslq-", Name(), StringComparison.Ordinal);
        Assert.Equal("cslq-".Length + 16, Name().Length);
    }

    /// <summary>
    /// Every part of the key changes the attach the session holds, so every part has to
    /// change the name. The version most of all: an upgraded <c>cslq</c> talking to a session
    /// running the old code is a wrong answer with nothing to notice it by.
    /// </summary>
    [Fact]
    public void Every_part_of_the_key_changes_the_pipe_name()
    {
        var names = new[]
        {
            Name(),
            Name(root: "/w/other"),
            Name(level: "Information"),
            Name(daemon: false),
            Name(version: "0.3.0"),
        };

        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>The level is handed to the server as written, but it names one attach.</summary>
    [Fact]
    public void The_log_level_is_matched_case_insensitively()
    {
        Assert.Equal(Name(level: "Warning"), Name(level: "warning"));
    }

    [Fact]
    public void A_request_survives_the_wire()
    {
        var sent = new Session.Request("0.2.0", ["hover", "Greet", "--root", Root]);
        var back = JsonSerializer.Deserialize<Session.Request>(
            JsonSerializer.Serialize(sent, Session.Json), Session.Json);

        Assert.Equal(sent.Version, back!.Version);
        Assert.Equal(sent.Argv, back.Argv);
    }

    /// <summary>
    /// A non-zero exit and non-ASCII output are the two things a naive round trip loses: the
    /// first to a default that swallows it, the second to an encoding that is not UTF-8.
    /// </summary>
    [Fact]
    public void A_response_survives_the_wire()
    {
        var sent = new Session.Response(1, "Grüße/日本語 🙂\n", "cslq: nada\n");
        var back = JsonSerializer.Deserialize<Session.Response>(
            JsonSerializer.Serialize(sent, Session.Json), Session.Json);

        Assert.Equal(sent, back);
        Assert.Null(back!.Error);
    }

    /// <summary>
    /// The three named methods, exercised over a real <c>JsonRpc</c> pair rather than a pipe:
    /// the client and the server agree on nothing else, so a renamed method is otherwise
    /// caught only by a gate run. <c>ping</c> and <c>stop</c> answer without a workspace —
    /// they are asked of sessions in a cold load — and <c>stop</c> is a flag the connection
    /// reads afterwards rather than something that ends anything here.
    /// </summary>
    [Fact]
    public async Task Ping_and_stop_are_answered_off_the_workspace()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var wire = Wired(ct);

        await wire.Client.InvokeWithCancellationAsync("ping", null, ct);
        Assert.False(wire.Endpoint.Stopping);

        await wire.Client.InvokeWithCancellationAsync("stop", null, ct);
        Assert.True(wire.Endpoint.Stopping);
    }

    /// <summary>
    /// Declining to answer is a field of the response rather than an RPC fault: the client
    /// falls back on it instead of reading it as the query's own failure, and a fault would
    /// be indistinguishable from the transport breaking — which it retries.
    /// </summary>
    [Fact]
    public async Task A_declined_run_is_data_rather_than_a_fault()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var wire = Wired(ct);

        var response = await wire.Client.InvokeWithCancellationAsync<Session.Response>(
            "run", [new Session.Request("0.0.0-not-this-one", ["ready"])], ct);

        Assert.Equal(1, response.Exit);
        Assert.Contains("cannot answer", response.Error!, StringComparison.Ordinal);
    }

    /// <summary>A <c>JsonRpc</c> client and a session endpoint joined stream to stream.</summary>
    private sealed class Wire(
        JsonRpc client, JsonRpc server, Session.Endpoint endpoint, Session.State state) : IAsyncDisposable
    {
        public JsonRpc Client => client;

        public Session.Endpoint Endpoint => endpoint;

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            server.Dispose();
            await state.DisposeAsync();
        }
    }

    private static Wire Wired(CancellationToken ct)
    {
        var (a, b) = FullDuplexStream.CreatePair();
        var state = new Session.State(
            new Session.Serve("cslq-test", Path.GetFullPath(Root), "Warning", true), TextWriter.Null);
        var endpoint = new Session.Endpoint(state, new SemaphoreSlim(1, 1), ct);

        var server = Session.Rpc(a);
        server.AddLocalRpcTarget(endpoint);
        server.StartListening();

        var client = Session.Rpc(b);
        client.StartListening();
        return new Wire(client, server, endpoint, state);
    }

    /// <summary>
    /// The session's working directory is its own, so a relative root has to be resolved
    /// before the request leaves — and <c>--no-session</c> has to go, or a session would be
    /// handed the flag that says not to use one.
    /// </summary>
    [Fact]
    public void The_root_is_absolutised_and_the_flag_is_stripped()
    {
        Assert.Equal(
            ["refs", "Greet", "--root", Root, "--max", "5"],
            Session.Normalise(["refs", "Greet", "--no-session", "--root", "fixture", "--max", "5"], Root));
    }

    /// <summary>A run with no <c>--root</c> at all meant the caller's directory.</summary>
    [Fact]
    public void An_absent_root_is_added()
    {
        Assert.Equal(["sym", "Greeter", "--root", Root], Session.Normalise(["sym", "Greeter"], Root));
    }

    [Fact]
    public void Everything_else_is_forwarded_verbatim()
    {
        string[] argv = ["diag", "App", "--errors-only", "--json", "--tfm", "net9.0", "--no-daemon"];
        Assert.Equal([.. argv, "--root", Root], Session.Normalise(argv, Root));
    }

    /// <summary>
    /// <c>--serve</c> is internal — we spawn it, nobody types it — so the spawn and the parse
    /// are each other's only callers and have to agree.
    /// </summary>
    [Fact]
    public void The_serve_argv_round_trips_through_its_own_parse()
    {
        var opts = Options("Information", daemon: false);
        var serve = Session.ServeRequest(Session.ServeArgv("cslq-test", opts));

        Assert.Equal(new Session.Serve("cslq-test", opts.Root, "Information", false), serve);
    }

    [Fact]
    public void An_ordinary_invocation_is_not_a_serve_request()
    {
        Assert.Null(Session.ServeRequest([]));
        Assert.Null(Session.ServeRequest(["hover", "Greet", "--no-session"]));
    }

    /// <summary>
    /// Every branch of an answer redirects the process-global <c>Console</c>, so a request
    /// that starts while another is running has to wait for it — including the ones that
    /// never reach the workspace. A usage error used to be answered before the gate, which
    /// redirected over the running query's buffers and then restored them: that query's
    /// output went into the usage error's response and its own was lost, silently. Holding
    /// the gate and watching a usage error fail to complete is what pins it.
    /// </summary>
    [Fact]
    public async Task An_answer_waits_for_the_console_before_it_parses()
    {
        using var gate = new SemaphoreSlim(1, 1);
        await using var state = new Session.State(
            new Session.Serve("cslq-test", Path.GetFullPath(Root), "Warning", true), TextWriter.Null);

        await gate.WaitAsync(TestContext.Current.CancellationToken);
        var answering = Session.AnswerAsync(
            new Session.Request(Build.Version, ["bogus"]), state, gate, TestContext.Current.CancellationToken);

        Assert.NotSame(answering, await Task.WhenAny(answering, Task.Delay(250, TestContext.Current.CancellationToken)));

        gate.Release();
        var response = await answering;
        Assert.Equal(2, response.Exit);
        Assert.Contains("unknown command", response.Stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// Once a byte of a session's answer has reached the caller the run is committed: falling
    /// back then reruns the query and prints the whole answer a second time, under the
    /// fallback notice, on top of the partial one. A stdout that closes mid-write — a
    /// <c>| head</c>-shaped consumer — is the way it happens.
    /// </summary>
    [Fact]
    public void A_partial_answer_is_never_retried()
    {
        var response = new Session.Response(0, "the answer", "", null);

        Assert.Throws<Session.DeliveryFailure>(
            () => Session.Deliver(response, new Breaks(), TextWriter.Null));
    }

    /// <summary>
    /// The other half, and the reason the guard is a flag rather than a blanket rethrow:
    /// nothing written is nothing the caller has seen, so that run may still fall back.
    /// </summary>
    [Fact]
    public void An_empty_answer_can_still_fall_back()
    {
        Assert.Equal(3, Session.Deliver(new Session.Response(3, "", "", null), new Breaks(), new Breaks()));
    }

    [Fact]
    public void An_answer_that_writes_is_the_exit_code_it_carries()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        Assert.Equal(1, Session.Deliver(new Session.Response(1, "out", "err", null), stdout, stderr));
        Assert.Equal("out", stdout.ToString());
        Assert.Equal("err", stderr.ToString());
    }

    /// <summary>A stdout the consumer has closed.</summary>
    private sealed class Breaks : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(string? value) => throw new IOException("the pipe is being closed");
    }

    private static Program.Options Options(string logLevel, bool daemon) => new(
        "hover", "Greet", Path.GetFullPath(Root), null, 50, 1, TimeSpan.FromSeconds(180),
        logLevel, false, false, daemon, true, null);
}
