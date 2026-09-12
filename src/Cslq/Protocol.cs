using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cslq;

// Minimal LSP 3.17 subset, hand-defined because no maintained Microsoft package
// supplies these types:
//   * Microsoft.CodeAnalysis.LanguageServer.Protocol is unlisted on nuget.org.
//   * Microsoft.VisualStudio.LanguageServer.Protocol last shipped 17.2.8 (May 2022),
//     predates LSP 3.17, has no PositionEncoding, and is Newtonsoft-based.
// dotnet/roslyn#68696 tracks making the real ones public; it is still open.
// Framing and request correlation still come from StreamJsonRpc — only the
// payload shapes are ours.

internal static class Lsp
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = LspWire.Default,
    };
}

/// <summary>
/// The whole of the language server's wire, source-generated, so nothing crossing it is
/// serialized reflectively. It is the <em>only</em> resolver <see cref="Lsp.Options"/> has:
/// a type that is not declared here throws rather than falling back to reflection, which
/// is what makes the suppressions in <see cref="LspClient"/> true for a caller that does
/// not exist yet — a new payload fails loudly on its first call instead of reaching a
/// server that answers a malformed request by taking its whole queue down.
/// <c>LspWireTests</c> pins both halves: the bytes of every payload, and that an
/// unregistered type throws.
/// <para>
/// The options are repeated here rather than inherited from <see cref="Lsp.Options"/>:
/// generated property names are baked in at generation time, so a context that did not
/// declare the same policy would write PascalCase past a camelCase reader — and the
/// <c>[JsonPropertyName]</c> members of this file are the shapes where that is fatal
/// rather than merely wrong.
/// </para>
/// <para>
/// <c>object</c>, <c>object[]</c> and <c>string[]</c> are what <see cref="LspClient"/>'s
/// endpoints answer the server's own requests with; <c>JsonElement</c> is what the ones
/// that accept and discard take. <c>CommonErrorData</c> is the error path: StreamJsonRpc
/// serializes a fault's <c>data</c> with these options, and this server really does
/// return errors — <c>-32000: Server was requested to shut down</c> is the one a dropped
/// registration would erase, leaving a dead connection and no cause.
/// </para>
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(InitializeParams))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(InitializedParams))]
[JsonSerializable(typeof(DidOpenTextDocumentParams))]
[JsonSerializable(typeof(DidCloseTextDocumentParams))]
[JsonSerializable(typeof(ReferenceParams))]
[JsonSerializable(typeof(TextDocumentPositionParams))]
[JsonSerializable(typeof(DocumentSymbolParams))]
[JsonSerializable(typeof(DocumentSymbol[]))]
[JsonSerializable(typeof(WorkspaceSymbolParams))]
[JsonSerializable(typeof(SymbolInformation[]))]
[JsonSerializable(typeof(Location[]))]
[JsonSerializable(typeof(Hover))]
[JsonSerializable(typeof(TextDocumentContentParams))]
[JsonSerializable(typeof(TextDocumentContentResult))]
[JsonSerializable(typeof(ProjectContextParams))]
[JsonSerializable(typeof(ProjectContextList))]
[JsonSerializable(typeof(DocumentDiagnosticParams))]
[JsonSerializable(typeof(DocumentDiagnosticReport))]
[JsonSerializable(typeof(ConfigurationParams))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(object[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(StreamJsonRpc.Protocol.CommonErrorData))]
internal sealed partial class LspWire : JsonSerializerContext;

internal sealed record Position(int Line, int Character);

internal sealed record Range(Position Start, Position End);

internal sealed record Location(string Uri, Range Range);

// _vs_projectContext is VS's own extension to the identifier, and it is what makes a
// positional request on a multi-targeted document repeatable: without it Roslyn answers
// from whichever context sorted first that attach. Null on every single-context request, so
// the wire shape is unchanged there -- JsonIgnoreCondition.WhenWritingNull drops it.
internal sealed record TextDocumentIdentifier(string Uri)
{
    [JsonPropertyName("_vs_projectContext")]
    public VsProjectContext? ProjectContext { get; init; }
}

// The context to answer in, sent back with the _vs_id exactly as received: Roslyn matches on
// that alone, the label being display text like _vs_label everywhere else. The server neither
// advertises the field nor requires a client capability for it, and -- unlike
// _vs_getProjectContexts, which answers or fails -- an unrecognised member of a request
// payload is silently ignored, so nothing but a pinned deterministic answer can tell whether
// it is still honoured. Measured against 5.12.0-1.26426.8 on 2026-09-10.
internal sealed record VsProjectContext(
    [property: JsonPropertyName("_vs_id")] string Id,
    [property: JsonPropertyName("_vs_label")] string Label);

internal sealed record TextDocumentItem(string Uri, string LanguageId, int Version, string Text);

// `initialized` carries an empty object and nothing else. It was an anonymous `new { }`
// until the wire had to be source-generated, which an anonymous type cannot be; a record
// with no members serialises to the same `{}`, pinned by LspWireTests.
internal sealed record InitializedParams;

internal sealed record DidOpenTextDocumentParams(TextDocumentItem TextDocument);

internal sealed record DidCloseTextDocumentParams(TextDocumentIdentifier TextDocument);

internal sealed record ReferenceContext(bool IncludeDeclaration);

internal sealed record ReferenceParams(
    TextDocumentIdentifier TextDocument,
    Position Position,
    ReferenceContext Context);

internal sealed record TextDocumentPositionParams(TextDocumentIdentifier TextDocument, Position Position);

internal sealed record DocumentSymbolParams(TextDocumentIdentifier TextDocument);

// textDocument/hover takes the same params as definition and implementation, so the
// existing shape is reused rather than a second identical record defined: a hand-rolled
// payload with the wrong shape does not fail cleanly -- it takes the server's whole queue
// down (see CLAUDE.md), so the fewer distinct payloads the better.
//
// `contents` is MarkupContent because the client declares plaintext as its only
// contentFormat; the deprecated MarkedString and MarkedString[] forms are what a server
// sends a client that declares neither, and neither deserializes into this, which is a
// loud failure rather than a half-rendered answer.
internal sealed record MarkupContent(string? Kind, string Value);

internal sealed record Hover(MarkupContent? Contents, Range? Range);

// Hierarchical form. The flat SymbolInformation[] fallback is what the server sends when
// hierarchicalDocumentSymbolSupport is missing or misspelled in the client capabilities, and
// it does not deserialize into this shape -- a loud failure, which is the point.
internal sealed record DocumentSymbol(
    string Name,
    string? Detail,
    int Kind,
    Range Range,
    Range SelectionRange,
    DocumentSymbol[]? Children);

internal sealed record WorkspaceSymbolParams(string Query);

// LSP 3.18. The server implements this but never advertises a textDocumentContentProvider
// in its initialize result, and answers regardless of whether the client declares the
// matching capability — verified both ways against 5.12.0-1.26426.8.
internal sealed record TextDocumentContentParams(string Uri);

internal sealed record TextDocumentContentResult(string Text);

// VS's own protocol extension, not LSP. It is the only thing that answers "which project
// does this document belong to" — the alternative, SymbolInformation.containerName, is
// localised display text. The underscore-prefixed names are the wire shape and do not come
// from the camelCase policy, hence the attributes.
internal sealed record ProjectContextParams(
    [property: JsonPropertyName("_vs_textDocument")] TextDocumentIdentifier TextDocument);

// _vs_defaultIndex is deserialised and deliberately not used: measured 0 in 6 of 6 runs on a
// two-context document while the array order around it varied per attach, so it says nothing
// about which context to prefer. It stays on the record because it is part of the shape the
// server sends, and the next reader needs to see that ignoring it was a measurement rather
// than an oversight. Contexts.Order is what picks instead.
internal sealed record ProjectContextList(
    [property: JsonPropertyName("_vs_projectContexts")] ProjectContext[]? Contexts,
    [property: JsonPropertyName("_vs_defaultIndex")] int DefaultIndex);

// _vs_id is "<projectId guid>|<absolute .csproj path> ($<tfm>)". The guid half is
// regenerated on every workspace load; the path half is what makes this worth asking for.
// _vs_label ("Core (net10.0)") is display text and is deliberately not read.
internal sealed record ProjectContext(
    [property: JsonPropertyName("_vs_id")] string Id,
    [property: JsonPropertyName("_vs_label")] string? Label);

internal sealed record SymbolInformation(
    string Name,
    int Kind,
    Location Location,
    string? ContainerName);

internal sealed record WorkspaceFolder(string Uri, string Name);

internal sealed record ClientInfo(string Name, string Version);

internal sealed record GeneralCapabilities(string[] PositionEncodings);

internal sealed record SynchronizationCapabilities(bool DynamicRegistration);

internal sealed record DiagnosticCapabilities(bool DynamicRegistration, bool RelatedDocumentSupport);

// The property name has to serialise to textDocument.documentSymbol: get it wrong and the
// server quietly answers with the flat SymbolInformation[] form instead.
internal sealed record DocumentSymbolCapabilities(
    bool DynamicRegistration,
    bool HierarchicalDocumentSymbolSupport);

// contentFormat is plaintext alone, deliberately. Roslyn reads it to choose how to render a
// hover, and markdown means fenced code blocks and &nbsp; runs that an agent then has to
// undo. Asking for plaintext is what makes `cslq hover` printable as-is.
internal sealed record HoverCapabilities(bool DynamicRegistration, string[] ContentFormat);

internal sealed record TextDocumentCapabilities(
    SynchronizationCapabilities Synchronization,
    DiagnosticCapabilities Diagnostic,
    DocumentSymbolCapabilities DocumentSymbol,
    HoverCapabilities Hover);

internal sealed record SymbolCapabilities(bool DynamicRegistration);

internal sealed record WorkspaceCapabilities(
    bool Configuration,
    bool WorkspaceFolders,
    SymbolCapabilities Symbol);

internal sealed record WindowCapabilities(bool WorkDoneProgress);

internal sealed record ClientCapabilities(
    GeneralCapabilities General,
    TextDocumentCapabilities TextDocument,
    WorkspaceCapabilities Workspace,
    WindowCapabilities Window);

internal sealed record InitializeParams(
    int ProcessId,
    ClientInfo ClientInfo,
    string Locale,
    string RootUri,
    ClientCapabilities Capabilities,
    WorkspaceFolder[] WorkspaceFolders);

internal sealed record ServerCapabilities(string? PositionEncoding);

internal sealed record InitializeResult(ServerCapabilities Capabilities);

internal sealed record ConfigurationItem(string? ScopeUri, string? Section);

internal sealed record ConfigurationParams(ConfigurationItem[] Items);

// Pull diagnostics (LSP 3.17). The server advertises no diagnosticProvider in its
// initialize result -- it registers dynamically via client/registerCapability, which we
// accept and discard, so the endpoint is called optimistically. Only the per-document one:
// workspace/diagnostic exists and answers, but returns zero reports, matching the
// workspaceDiagnostics: false in that registration. Whole-workspace mode walks the files.
internal sealed record DocumentDiagnosticParams(TextDocumentIdentifier TextDocument);

internal sealed record DocumentDiagnosticReport(string? Kind, string? ResultId, Diagnostic[]? Items);

// Code is string-or-int per the spec, and severity is absent for "as the client sees fit".
// No `source`: the spec has one and this server never sends it. Measured 2026-09-10 over
// fixture, fixture2 and this repository -- 53 findings, compiler CS, IDE analyzer and
// Microsoft.CodeAnalysis.NetAnalyzers CA alike, every one of them null, matching the four
// corpora the 0.1.0 testing pass covered. A key that could only ever be null is omitted
// rather than emitted as null, and a field nothing reads is dead, so it is not parsed either.
internal sealed record Diagnostic(
    Range Range,
    int? Severity,
    JsonElement Code,
    string Message);
