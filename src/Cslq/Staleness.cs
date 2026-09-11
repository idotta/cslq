namespace Cslq;

/// <summary>
/// What an open document looked like on disk when its text was read, and whether the file
/// still matches it. A one-shot <c>cslq</c> never needed this — the process died with the
/// answer, so every run re-sent the text it had just read — but a session holds
/// <c>didOpen</c> across an edit, and Roslyn owns an open document's text rather than
/// re-reading the file. Without a check the session answers from the text as it was when it
/// first opened the document: wrong line numbers, wrong context lines, a renamed symbol
/// still found, all at exit 0.
/// </summary>
internal static class Staleness
{
    /// <summary>
    /// The stamp is a <c>stat</c>, not a hash: it is taken before every use of a document,
    /// and hashing every open file per request would cost the reads the session exists to
    /// avoid. Length alone misses an in-place edit of the same size, so both.
    /// </summary>
    internal readonly record struct Stamp(DateTime WriteTimeUtc, long Length);

    /// <summary>
    /// Whether a URI has a file behind it at all. A source-generated document is the
    /// server's own — <c>PathUri.ToPath</c> answers a confident <c>/BuildInfo.g.cs</c> for
    /// one — and a decompiled document is a real file under the temp directory that no edit
    /// will ever touch. Neither may be stat'ed: the generated one would read as deleted and
    /// the decompiled one would cost a stat per request for an answer that cannot change.
    /// </summary>
    internal static bool HasFile(string uri) =>
        !PathUri.IsGenerated(uri) && !PathUri.IsDecompiled(uri);

    internal static Stamp? OfPath(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new Stamp(info.LastWriteTimeUtc, info.Length) : null;
    }

    internal static Stamp? Of(string uri) => HasFile(uri) ? OfPath(PathUri.ToPath(uri)) : null;

    /// <summary>
    /// How the file now compares with <paramref name="opened"/>. A URI with no file behind it
    /// is always <see cref="DocumentState.Unchanged"/> and is never stat'ed.
    /// </summary>
    internal static DocumentState Check(string uri, Stamp? opened)
    {
        if (!HasFile(uri)) return DocumentState.Unchanged;
        if (OfPath(PathUri.ToPath(uri)) is not { } now) return DocumentState.Deleted;
        return now == opened ? DocumentState.Unchanged : DocumentState.Changed;
    }
}

internal enum DocumentState
{
    Unchanged,
    Changed,
    Deleted,
}
