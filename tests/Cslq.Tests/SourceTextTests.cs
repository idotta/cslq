using System.Text;

namespace Cslq.Tests;

/// <summary>
/// The decoder, pinned in a temp tree: a file that is not valid UTF-8 has to be refused and
/// the three encodings that are legitimately not plain UTF-8 — a UTF-8 BOM, a UTF-16 LE BOM
/// and a UTF-16 BE BOM — have to keep working. Both halves matter equally. Substitution is
/// what T-75 measured (one U+FFFD for two CP1252 bytes, every later column off by one, and
/// <c>def</c> at the position cslq itself printed answering <c>no results</c>), and the
/// obvious fix for it — reading bytes as UTF-8 and nothing else — silently breaks every
/// BOM'd file in a repository.
/// </summary>
public class SourceTextTests
{
    [Fact]
    public async Task Invalid_utf8_is_refused_rather_than_substituted()
    {
        using var ws = new Workspace(solution: false);
        var path = Bytes(ws, "Bad.cs", "public class Bad { public string P => \"caf", [0xE9, 0xA0], "\"; }\n");

        var ex = await Assert.ThrowsAsync<InvalidTextException>(
            () => SourceText.ReadAsync(ws.Root, path, TestContext.Current.CancellationToken));

        Assert.Contains("Bad.cs is not valid UTF-8", ex.Message);
        Assert.DoesNotContain("�", ex.Message);
    }

    /// <summary>
    /// The line is the whole point of naming it — a repository-wide walk says which file, and
    /// a 4,000-line file needs saying where. It is recovered from the lenient decode rather
    /// than from the decoder's own index, which is an offset into a buffer.
    /// </summary>
    [Fact]
    public async Task The_message_names_the_line_the_first_invalid_byte_sits_on()
    {
        using var ws = new Workspace(solution: false);
        var path = Bytes(ws, "Bad.cs", "one\ntwo\nthree ", [0xE9, 0xA0], "\nfour\n");

        var ex = await Assert.ThrowsAsync<InvalidTextException>(
            () => SourceText.ReadAsync(ws.Root, path, TestContext.Current.CancellationToken));

        Assert.Contains("first invalid byte on line 3", ex.Message);
    }

    /// <summary>
    /// Root-relative, like every other path cslq prints: the exception is rendered by
    /// <c>Main</c>, which has no root to render it against, so the root is taken at the read.
    /// </summary>
    [Fact]
    public async Task The_path_in_the_message_is_relative_to_the_root()
    {
        using var ws = new Workspace(solution: false);
        var path = Bytes(ws, "Core/Deep/Bad.cs", "x", [0xFF], "y");

        var ex = await Assert.ThrowsAsync<InvalidTextException>(
            () => SourceText.ReadAsync(ws.Root, path, TestContext.Current.CancellationToken));

        Assert.StartsWith("Core/Deep/Bad.cs is not valid UTF-8", ex.Message);
        Assert.DoesNotContain(ws.Root, ex.Message);
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-8-bom")]
    [InlineData("utf-16-le")]
    [InlineData("utf-16-be")]
    public async Task A_file_in_a_legitimate_encoding_is_read_whole(string encoding)
    {
        using var ws = new Workspace(solution: false);
        const string Text = "public class Café { }\n";
        Encoding writer = encoding switch
        {
            "utf-8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            "utf-8-bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            "utf-16-le" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            _ => new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
        };

        var path = Path.Combine(ws.Root, "Ok.cs");
        await File.WriteAllBytesAsync(
            path,
            [.. writer.GetPreamble(), .. writer.GetBytes(Text)],
            TestContext.Current.CancellationToken);

        Assert.Equal(
            Text, await SourceText.ReadAsync(ws.Root, path, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Context rows are indexed by line, so the split has to agree with the one
    /// <c>File.ReadAllLines</c> used to do: CRLF and LF both end a line, and a trailing
    /// newline yields no phantom final row.
    /// </summary>
    [Theory]
    [InlineData("a\nb\n", 2)]
    [InlineData("a\r\nb\r\n", 2)]
    [InlineData("a\nb", 2)]
    [InlineData("a\n", 1)]
    [InlineData("", 0)]
    public void Lines_splits_the_way_the_renderer_indexes(string text, int count)
    {
        Assert.Equal(count, SourceText.Lines(text).Length);
    }

    private static string Bytes(Workspace ws, string relative, string head, byte[] bad, string tail)
    {
        var path = Path.Combine(ws.Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(
            path,
            [.. Encoding.UTF8.GetBytes(head), .. bad, .. Encoding.UTF8.GetBytes(tail)]);
        return path;
    }
}
