using System.Text;

namespace Cslq;

/// <summary>
/// Every read of a source file's text goes through here, and the decoder throws rather than
/// substitutes. <see cref="File.ReadAllTextAsync(string, CancellationToken)"/> replaces an
/// invalid byte sequence with U+FFFD and says nothing, which desyncs the <c>didOpen</c> text
/// from what Roslyn parses off disk: measured on a CP1252 file, <c>E9 A0</c> became one
/// U+FFFD, so every column after it was one short and <c>def</c> at the position cslq itself
/// printed answered <c>no results</c>. That is a wrong answer at exit 0, and the file is the
/// only thing that can be named, so it is named.
/// <para>
/// BOM detection still runs first, so a UTF-8 BOM and a UTF-16 LE/BE BOM file are decoded by
/// their own encoding exactly as before — only a file claiming to be UTF-8 and not being it
/// is refused.
/// </para>
/// </summary>
internal static class SourceText
{
    private static readonly UTF8Encoding Strict = new(
        encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The whole document, or <see cref="InvalidTextException"/> naming the file and the
    /// line. The root is taken here rather than at the printer so the path in the message is
    /// root-relative like every other path cslq renders — the exception is printed by
    /// <c>Main</c>, which has no root to render it against.
    /// </summary>
    public static async Task<string> ReadAsync(string root, string path, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(path, Strict, detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(ct);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidTextException(
                PathUri.Relative(root, path), await FirstBadLineAsync(path, ct));
        }
    }

    /// <summary>
    /// The same read split into lines, for rendering context rows. Line endings are handled
    /// the way <c>File.ReadAllLines</c> handles them — CRLF, LF and a trailing newline that
    /// yields no phantom final row.
    /// </summary>
    public static async Task<string[]> ReadLinesAsync(
        string root, string path, CancellationToken ct) => Lines(await ReadAsync(root, path, ct));

    internal static string[] Lines(string text)
    {
        if (text.Length == 0) return [];
        var normalised = text.ReplaceLineEndings("\n");
        if (normalised.EndsWith('\n')) normalised = normalised[..^1];
        return normalised.Split('\n');
    }

    /// <summary>
    /// Which line the first invalid sequence sits on, for the message. The strict decoder
    /// throws a <see cref="DecoderFallbackException"/> whose <c>Index</c> is into whatever
    /// buffer the reader happened to hand it, not into the file, so the offset is recovered
    /// the other way round: decode the same bytes leniently and find the first U+FFFD the
    /// substitution produced. Reached only on the failure path, so the second read costs
    /// nothing on any file that is valid.
    /// </summary>
    private static async Task<int?> FirstBadLineAsync(string path, CancellationToken ct)
    {
        try
        {
            var lenient = await File.ReadAllTextAsync(path, ct);
            var at = lenient.IndexOf('�');
            return at < 0 ? null : lenient.AsSpan(0, at).Count('\n') + 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// A file that is not valid UTF-8. Its own type because the two callers answer it
/// differently: a command given the document as its target fails on it (exit 1, like any
/// other unusable argument), while <c>diag</c>'s directory walk names it and carries on —
/// failing a whole-tree walk because one file in the tree cannot be decoded would hide every
/// real diagnostic behind it.
/// </summary>
internal sealed class InvalidTextException(string path, int? line)
    : CslqException(Describe(path, line))
{
    private static string Describe(string path, int? line) =>
        $"{path} is not valid UTF-8" +
        (line is null ? string.Empty : $" (first invalid byte on line {line})") +
        "; cslq reads source files as UTF-8, and decoding one with substitutions would move "
        + "every column after the bad bytes";
}
