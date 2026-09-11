using StreamJsonRpc;

namespace Cslq.Tests;

/// <summary>
/// <c>Main</c> catches <c>CslqException</c> and <c>OperationCanceledException</c> and nothing
/// else, so every post-initialize request is routed through <c>LspClient.RequestAsync</c> and
/// its failures described here. Before that, a server-side rejection or a daemon that died
/// mid-request left the caller a stack trace and exit 127.
/// <para>
/// <c>RemoteMethodNotFoundException</c> — a sibling of <c>RemoteInvocationException</c>, not a
/// subclass — is covered by the same <c>RemoteRpcException</c> arm but has no public
/// constructor, so there is no case for it here.
/// </para>
/// </summary>
public class RequestFailureTests
{
    [Fact]
    public void A_server_rejection_names_the_method_and_the_server_text()
    {
        var ex = new RemoteInvocationException(
            "The requested line number 98 must be less than the number of lines 14. (Parameter 'Line')",
            -32602,
            new object());

        var message = LspClient.Describe("textDocument/references", ex, null);

        Assert.NotNull(message);
        Assert.Contains("textDocument/references failed:", message);
        Assert.Contains("must be less than the number of lines 14", message);
    }

    [Fact]
    public void A_lost_connection_names_the_method_and_says_to_rerun()
    {
        var message = LspClient.Describe("textDocument/diagnostic", new ConnectionLostException(), null);

        Assert.NotNull(message);
        Assert.Contains("textDocument/diagnostic", message);
        Assert.Contains("rerun", message);
        Assert.DoesNotContain("pipe", message);
    }

    [Fact]
    public void A_lost_connection_names_the_daemon_pipe_when_there_is_one()
    {
        var message = LspClient.Describe("workspace/symbol", new ConnectionLostException(), "cslq-probe-123");

        Assert.NotNull(message);
        Assert.Contains("cslq-probe-123", message);
        Assert.Contains("rerun", message);
    }

    /// <summary>Ctrl+C is exit 130; wrapping it as a <c>CslqException</c> would report exit 1.</summary>
    [Fact]
    public void A_cancellation_is_not_wrapped()
    {
        Assert.Null(LspClient.Describe("workspace/symbol", new OperationCanceledException(), "cslq-probe-123"));
    }

    [Fact]
    public void An_unexpected_exception_is_not_wrapped()
    {
        Assert.Null(LspClient.Describe("workspace/symbol", new InvalidOperationException("boom"), null));
    }

    /// <summary>
    /// <c>textDocument/_vs_getProjectContexts</c> is the one request that does not go through
    /// <c>RequestAsync</c>, because an unimplemented optional extension is a label to soften
    /// rather than a command to fail. Only that one failure may be remembered: the catch was
    /// <c>RemoteRpcException</c>, which is also the base of <c>ConnectionLostException</c>, so
    /// one dropped connection set the flag for the life of a session — which stops <c>diag</c>
    /// skipping misc-file documents — and cached an empty context list for the document, which
    /// asks every later positional request with no <c>_vs_projectContext</c>.
    /// <para>
    /// <c>RemoteMethodNotFoundException</c> has no public constructor, so as in the cases
    /// above there is no positive leg here; the negatives are the regression.
    /// </para>
    /// </summary>
    [Fact]
    public void Only_an_unimplemented_extension_is_remembered()
    {
        Assert.False(LspClient.ContextsUnsupported(new ConnectionLostException()));
        Assert.False(LspClient.ContextsUnsupported(
            new RemoteInvocationException("internal error", -32603, new object())));
        Assert.False(LspClient.ContextsUnsupported(new IOException("the pipe is being closed")));
    }
}
