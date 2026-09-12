using System.Text;
using System.Text.Json;
using Nerdbank.Streams;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Cslq.Tests;

/// <summary>
/// The language server's wire, byte for byte. Every payload in <c>Protocol.cs</c> is
/// hand-rolled and a shape mistake does not fail cleanly — a malformed
/// <c>workspace/textDocumentContent</c> returned <c>TaskCanceled</c> and then took the
/// server's whole request queue down with <c>-32000</c> for everything after it. Moving the
/// serializer onto a source-generated context is exactly the kind of change that could alter
/// a name silently, so the bytes are pinned here rather than round-tripped: two ends sharing
/// one wrong policy round-trip perfectly well.
/// </summary>
public class LspWireTests
{
    private static string Json(object value) =>
        JsonSerializer.Serialize(value, value.GetType(), Lsp.Options);

    /// <summary>
    /// Nothing crossing this connection is serialized reflectively, and that is what makes
    /// the <c>IL2026</c> suppressions on <see cref="LspClient"/>'s request helpers true for a
    /// caller that has not been written yet: a payload type nobody registered cannot reach
    /// the server at all.
    /// </summary>
    [Fact]
    public void An_unregistered_type_is_refused_rather_than_reflected_over()
    {
        var ex = Assert.Throws<NotSupportedException>(
            () => JsonSerializer.Serialize(new Unregistered("x"), typeof(Unregistered), Lsp.Options));

        Assert.Contains(nameof(Unregistered), ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(LspWire), ex.Message, StringComparison.Ordinal);
    }

    private sealed record Unregistered(string Name);

    /// <summary>
    /// camelCase members, and the <c>_vs_</c> ones verbatim. Generated property names are
    /// baked in when the context is generated rather than applied from the options instance,
    /// so this is the assertion that a context declaring the wrong policy would fail.
    /// </summary>
    [Fact]
    public void Every_request_payload_keeps_its_shape()
    {
        Assert.Equal(
            "{\"textDocument\":{\"uri\":\"file:///x.cs\"},\"position\":{\"line\":3,\"character\":7}}",
            Json(new TextDocumentPositionParams(new TextDocumentIdentifier("file:///x.cs"), new Position(3, 7))));

        Assert.Equal(
            "{\"textDocument\":{\"uri\":\"file:///x.cs\"},\"position\":{\"line\":3,\"character\":7},"
            + "\"context\":{\"includeDeclaration\":true}}",
            Json(new ReferenceParams(
                new TextDocumentIdentifier("file:///x.cs"), new Position(3, 7), new ReferenceContext(true))));

        Assert.Equal(
            "{\"textDocument\":{\"uri\":\"file:///x.cs\"}}",
            Json(new DocumentSymbolParams(new TextDocumentIdentifier("file:///x.cs"))));

        Assert.Equal(
            "{\"textDocument\":{\"uri\":\"file:///x.cs\"}}",
            Json(new DocumentDiagnosticParams(new TextDocumentIdentifier("file:///x.cs"))));

        Assert.Equal(
            "{\"textDocument\":{\"uri\":\"file:///x.cs\"}}",
            Json(new DidCloseTextDocumentParams(new TextDocumentIdentifier("file:///x.cs"))));

        Assert.Equal(
            "{\"textDocument\":{\"uri\":\"file:///x.cs\",\"languageId\":\"csharp\",\"version\":1,\"text\":\"class A;\"}}",
            Json(new DidOpenTextDocumentParams(new TextDocumentItem("file:///x.cs", "csharp", 1, "class A;"))));

        Assert.Equal("{\"query\":\"Greet\"}", Json(new WorkspaceSymbolParams("Greet")));

        Assert.Equal(
            "{\"uri\":\"roslyn-source-generated:///x/Stamp.g.cs\"}",
            Json(new TextDocumentContentParams("roslyn-source-generated:///x/Stamp.g.cs")));

        Assert.Equal(
            "{\"_vs_textDocument\":{\"uri\":\"file:///x.cs\"}}",
            Json(new ProjectContextParams(new TextDocumentIdentifier("file:///x.cs"))));
    }

    /// <summary>
    /// <c>initialized</c> carries an empty object. It was an anonymous <c>new { }</c>, which
    /// no context can generate; the record that replaced it has to write the same two bytes.
    /// </summary>
    [Fact]
    public void Initialized_is_still_an_empty_object()
    {
        Assert.Equal("{}", Json(new InitializedParams()));
    }

    /// <summary>
    /// The context a positional request carries is absent unless one was chosen, and present
    /// with the <c>_vs_id</c> the server sent when it was — Roslyn matches on that alone.
    /// </summary>
    [Fact]
    public void A_chosen_project_context_travels_back_verbatim()
    {
        var doc = new TextDocumentIdentifier("file:///x.cs")
        {
            ProjectContext = new VsProjectContext(
                "16c1|C:\\repo\\Multi\\Multi.csproj ($net9.0)", "Multi (net9.0)"),
        };

        Assert.Equal(
            "{\"uri\":\"file:///x.cs\",\"_vs_projectContext\":"
            + "{\"_vs_id\":\"16c1|C:\\\\repo\\\\Multi\\\\Multi.csproj ($net9.0)\","
            + "\"_vs_label\":\"Multi (net9.0)\"}}",
            Json(doc));

        Assert.DoesNotContain(
            "_vs_projectContext", Json(new TextDocumentIdentifier("file:///x.cs")), StringComparison.Ordinal);
    }

    /// <summary>
    /// The capabilities <c>initialize</c> declares are what the server chooses its answer
    /// shapes by: <c>hierarchicalDocumentSymbolSupport</c> picks the outline form this client
    /// can read at all, and <c>contentFormat</c> is what makes a hover printable as-is.
    /// Misspell one and the server answers a different shape at exit 0.
    /// </summary>
    [Fact]
    public void Initialize_declares_the_capabilities_the_answers_depend_on()
    {
        var wire = Json(new ClientCapabilities(
            new GeneralCapabilities(["utf-16"]),
            new TextDocumentCapabilities(
                new SynchronizationCapabilities(true),
                new DiagnosticCapabilities(true, true),
                new DocumentSymbolCapabilities(true, true),
                new HoverCapabilities(true, ["plaintext"])),
            new WorkspaceCapabilities(true, true, new SymbolCapabilities(true)),
            new WindowCapabilities(true)));

        Assert.Equal(
            "{\"general\":{\"positionEncodings\":[\"utf-16\"]},"
            + "\"textDocument\":{\"synchronization\":{\"dynamicRegistration\":true},"
            + "\"diagnostic\":{\"dynamicRegistration\":true,\"relatedDocumentSupport\":true},"
            + "\"documentSymbol\":{\"dynamicRegistration\":true,\"hierarchicalDocumentSymbolSupport\":true},"
            + "\"hover\":{\"dynamicRegistration\":true,\"contentFormat\":[\"plaintext\"]}},"
            + "\"workspace\":{\"configuration\":true,\"workspaceFolders\":true,"
            + "\"symbol\":{\"dynamicRegistration\":true}},"
            + "\"window\":{\"workDoneProgress\":true}}",
            wire);
    }

    /// <summary>
    /// The answers, read the way the server sends them. <c>_vs_defaultIndex</c> is
    /// deserialised and deliberately unused; <c>code</c> is string-or-int, so it stays a
    /// <see cref="JsonElement"/>.
    /// </summary>
    [Fact]
    public void Every_result_shape_still_reads()
    {
        var contexts = JsonSerializer.Deserialize<ProjectContextList>(
            "{\"_vs_projectContexts\":[{\"_vs_id\":\"a|/r/A.csproj ($net10.0)\",\"_vs_label\":\"A\"}],"
            + "\"_vs_defaultIndex\":0}",
            Lsp.Options);
        Assert.Equal("a|/r/A.csproj ($net10.0)", contexts!.Contexts![0].Id);

        var init = JsonSerializer.Deserialize<InitializeResult>(
            "{\"capabilities\":{\"positionEncoding\":\"utf-16\"}}", Lsp.Options);
        Assert.Equal("utf-16", init!.Capabilities.PositionEncoding);

        var hover = JsonSerializer.Deserialize<Hover>(
            "{\"contents\":{\"kind\":\"plaintext\",\"value\":\"string Greeter.Greet(string name)\"}}",
            Lsp.Options);
        Assert.Equal("string Greeter.Greet(string name)", hover!.Contents!.Value);

        var locations = JsonSerializer.Deserialize<Location[]>(
            "[{\"uri\":\"file:///x.cs\",\"range\":{\"start\":{\"line\":1,\"character\":2},"
            + "\"end\":{\"line\":1,\"character\":7}}}]", Lsp.Options);
        Assert.Equal(7, locations![0].Range.End.Character);

        var symbols = JsonSerializer.Deserialize<SymbolInformation[]>(
            "[{\"name\":\"Greet\",\"kind\":6,\"location\":{\"uri\":\"file:///x.cs\","
            + "\"range\":{\"start\":{\"line\":1,\"character\":2},\"end\":{\"line\":1,\"character\":7}}},"
            + "\"containerName\":\"in Greeter\"}]", Lsp.Options);
        Assert.Equal("Greet", symbols![0].Name);

        var outline = JsonSerializer.Deserialize<DocumentSymbol[]>(
            "[{\"name\":\"Greeter\",\"detail\":null,\"kind\":5,"
            + "\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":9,\"character\":1}},"
            + "\"selectionRange\":{\"start\":{\"line\":0,\"character\":6},\"end\":{\"line\":0,\"character\":13}},"
            + "\"children\":[{\"name\":\"Greet\",\"detail\":null,\"kind\":6,"
            + "\"range\":{\"start\":{\"line\":2,\"character\":4},\"end\":{\"line\":4,\"character\":5}},"
            + "\"selectionRange\":{\"start\":{\"line\":2,\"character\":18},"
            + "\"end\":{\"line\":2,\"character\":23}},"
            + "\"children\":null}]}]", Lsp.Options);
        Assert.Equal("Greet", outline![0].Children![0].Name);

        var report = JsonSerializer.Deserialize<DocumentDiagnosticReport>(
            "{\"kind\":\"full\",\"resultId\":null,\"items\":[{\"range\":{\"start\":{\"line\":1,\"character\":2},"
            + "\"end\":{\"line\":1,\"character\":7}},\"severity\":1,\"code\":\"CS0029\",\"message\":\"nope\"}]}",
            Lsp.Options);
        Assert.Equal("CS0029", report!.Items![0].Code.GetString());

        var content = JsonSerializer.Deserialize<TextDocumentContentResult>(
            "{\"text\":\"// <auto-generated/>\"}", Lsp.Options);
        Assert.Equal("// <auto-generated/>", content!.Text);
    }

    /// <summary>
    /// What actually leaves the process, framing and all — the pinning above goes through
    /// <see cref="JsonSerializer"/>, and this goes through the formatter the connection is
    /// built with, which is the thing that changed.
    /// </summary>
    [Fact]
    public async Task A_request_leaves_the_formatter_in_the_shape_the_server_expects()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mine, theirs) = FullDuplexStream.CreatePair();
        using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(
            mine, mine, new SystemTextJsonFormatter { JsonSerializerOptions = Lsp.Options }));
        rpc.StartListening();

        await rpc.NotifyWithParameterObjectAsync(
            "textDocument/didOpen",
            new DidOpenTextDocumentParams(new TextDocumentItem("file:///x.cs", "csharp", 1, "class A;")));

        var buffer = new byte[4096];
        var read = await theirs.ReadAsync(buffer, ct);
        var sent = Encoding.UTF8.GetString(buffer, 0, read);

        Assert.Contains("Content-Length: ", sent, StringComparison.Ordinal);
        Assert.EndsWith(
            "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":"
            + "{\"textDocument\":{\"uri\":\"file:///x.cs\",\"languageId\":\"csharp\",\"version\":1,"
            + "\"text\":\"class A;\"}}}",
            sent,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The server's own calls, answered through the shape-fed target metadata. Without
    /// <c>IncludeMethods</c> the metadata carries properties alone and every one of these
    /// comes back <c>RemoteMethodNotFoundException</c> — and a notification is not answered at
    /// all, so <c>projectInitializationComplete</c> would simply never arrive and readiness
    /// would wait out the whole timeout. <c>UseSingleObjectParameterDeserialization</c> is
    /// under test here too: each of these takes the params object whole.
    /// </summary>
    [Fact]
    public async Task The_endpoints_answer_the_calls_the_server_makes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mine, theirs) = FullDuplexStream.CreatePair();

        var endpoints = new LspClient.Endpoints();
        using var server = new JsonRpc(new HeaderDelimitedMessageHandler(
            mine, mine, new SystemTextJsonFormatter { JsonSerializerOptions = Lsp.Options }));
        server.AddLocalRpcTarget(RpcTargetMetadata.FromShape<LspClient.Endpoints>(), endpoints, null);
        server.StartListening();

        using var client = new JsonRpc(new HeaderDelimitedMessageHandler(
            theirs, theirs, new SystemTextJsonFormatter { JsonSerializerOptions = Lsp.Options }));
        client.StartListening();

        var answer = await client.InvokeWithParameterObjectAsync<object?[]>(
            "workspace/configuration",
            new ConfigurationParams([new ConfigurationItem(null, "csharp|background_analysis")]),
            ct);
        Assert.Equal([null], answer);

        Assert.Empty(await client.InvokeWithParameterObjectAsync<string[]>(
            "workspace/_roslyn_restorableProjects",
            new DocumentSymbolParams(new TextDocumentIdentifier("file:///x.cs")),
            ct));

        Assert.Null(await client.InvokeWithParameterObjectAsync<object?>(
            "client/registerCapability",
            new DocumentSymbolParams(new TextDocumentIdentifier("file:///x.cs")),
            ct));

        Assert.False(endpoints.ProjectInitialized.IsCompleted);
        await client.NotifyAsync("workspace/projectInitializationComplete");
        await endpoints.ProjectInitialized.WaitAsync(TimeSpan.FromSeconds(10), ct);
    }

    /// <summary>
    /// The error path. StreamJsonRpc serializes a fault's <c>data</c> with these same
    /// options, and this server really does return errors — <c>-32000: Server was requested
    /// to shut down</c> is what every request after a malformed one gets. Leave
    /// <c>CommonErrorData</c> out of the context and that payload cannot be written, which
    /// turns a diagnosable failure into a dead connection with the cause erased.
    /// </summary>
    [Fact]
    public async Task A_server_error_reaches_the_client_with_its_data_intact()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mine, theirs) = FullDuplexStream.CreatePair();
        using var server = new JsonRpc(new HeaderDelimitedMessageHandler(
            mine, mine, new SystemTextJsonFormatter { JsonSerializerOptions = Lsp.Options }));
        server.AddLocalRpcTarget(new ShuttingDown());
        server.StartListening();

        using var client = new JsonRpc(new HeaderDelimitedMessageHandler(
            theirs, theirs, new SystemTextJsonFormatter { JsonSerializerOptions = Lsp.Options }));
        client.StartListening();

        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => client.InvokeWithCancellationAsync("workspace/symbol", null, ct));

        Assert.Equal("Server was requested to shut down.", ex.Message);
        var data = Assert.IsType<CommonErrorData>(ex.DeserializedErrorData);
        Assert.Equal(typeof(InvalidOperationException).FullName, data.TypeName);
    }

    private sealed class ShuttingDown
    {
        [JsonRpcMethod("workspace/symbol")]
        public Task Symbol() => throw new InvalidOperationException("Server was requested to shut down.");
    }
}
