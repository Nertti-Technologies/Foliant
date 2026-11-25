using System.Text;
using Foliant.Mcp.Extraction;
using Foliant.Mcp.Shaping;

namespace Foliant.Mcp.Tools;

/// <summary>A PDF read from disk for a tool call: its bytes and page count, or a shaped error.</summary>
/// <remarks>
/// The path comes from the MCP client, which acts on prompts that may contain document text, so it is
/// treated as untrusted input (ADR-0010 T4). Checks, in order, before the file is read: the path is
/// absolute; with <see cref="FoliantMcpOptions.AllowedRoots"/> set, its real path (every symbolic link
/// resolved) is inside one of them; it is at least as long as the signature and no larger than
/// <see cref="FoliantMcpOptions.MaxInputBytes"/>; and it starts with the PDF signature. Errors name the
/// failure, never an exception message.
/// </remarks>
public sealed record PdfInput(byte[] Bytes, int TotalPages, string? Error)
{
    private const int SignatureWindow = 1024;   // PDF 1.7 §7.5.2 allows the header anywhere in the first 1,024 bytes
    private const int MaxLinkHops = 40;         // the same bound as Linux SYMLOOP_MAX
    private static readonly byte[] Signature = Encoding.ASCII.GetBytes("%PDF-");

    /// <summary>
    /// Reads <paramref name="path"/> within <paramref name="options"/>' limits and counts its pages. An
    /// unreadable, refused or invalid file becomes a shaped error the client can act on; cancellation
    /// is not an input error and propagates.
    /// </summary>
    public static async Task<PdfInput> ReadAsync(
        string path, FoliantMcpOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return Failed("The path must be absolute.");

        string real;
        try
        {
            real = RealPath(path);
        }
        catch (IOException)
        {
            return Failed($"Could not resolve the path: {path}");
        }

        var roots = Roots(options);
        if (roots is not null && !roots.Any(root => IsInside(real, root)))
            return Failed($"The file is outside the folders this server may read (Foliant__AllowedRoots): {path}");

        var file = new FileInfo(real);
        if (!file.Exists)
            return Failed($"File not found: {path}");
        // A FIFO or device reports length 0, and opening a FIFO blocks; nothing shorter than the
        // signature can be a PDF, so it is refused before the file is opened.
        if (file.Length < Signature.Length)
            return Failed($"Not a PDF file (too short): {path}");
        if (file.Length > options.MaxInputBytes)
            return Failed($"The file is larger than the {options.MaxInputBytes:N0}-byte limit (Foliant__MaxInputBytes).");

        try
        {
            byte[] pdf;
            await using (var stream = new FileStream(real, FileMode.Open, FileAccess.Read, FileShare.Read,
                             bufferSize: 1, useAsync: true))
            {
                // Read the length checked above; a file that grows meanwhile is not read past it.
                long length = Math.Min(stream.Length, options.MaxInputBytes);
                pdf = new byte[length];
                await stream.ReadExactlyAsync(pdf, cancellationToken).ConfigureAwait(false);
            }

            if (pdf.AsSpan(0, Math.Min(pdf.Length, SignatureWindow)).IndexOf(Signature) < 0)
                return Failed($"Not a PDF file (no %PDF- header): {path}");
            return new PdfInput(pdf, Pdf.GetPageCount(pdf), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed($"Could not read the file: {path}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A PDF-parse failure is reported, not thrown: the tool contract is a JSON reply.
            return Failed($"Could not read PDF '{path}': the file is not a valid PDF.");
        }
    }

    /// <summary>
    /// The configured roots as real paths, or null when none are configured (any path is allowed).
    /// Missing, relative or unresolvable entries are dropped; if none is usable the list is empty and
    /// nothing may be read, rather than falling back to everything.
    /// </summary>
    internal static IReadOnlyList<string>? Roots(FoliantMcpOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.AllowedRoots)) return null;
        var roots = new List<string>();
        foreach (string entry in options.AllowedRoots.Split(
                     Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Path.IsPathFullyQualified(entry)) continue;
            try
            {
                string root = RealPath(entry);
                if (Directory.Exists(root)) roots.Add(root);
            }
            catch (IOException)
            {
                // An unresolvable root grants nothing.
            }
        }

        return roots;
    }

    internal static bool IsInside(string path, string root)
    {
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    /// <summary>
    /// The canonical path with every symbolic link resolved, component by component (a link to a
    /// directory part way along would otherwise hide where the file really is). Throws
    /// <see cref="IOException"/> on a link loop.
    /// </summary>
    internal static string RealPath(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full)!;
        var pending = new Stack<string>(full[root.Length..]
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Reverse());
        string current = root;
        int hops = 0;
        while (pending.Count > 0)
        {
            string next = Path.Join(current, pending.Pop());
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.LinkTarget is not { } target)
            {
                current = next;
                continue;
            }

            if (++hops > MaxLinkHops) throw new IOException("Too many symbolic links.");
            string resolved = Path.GetFullPath(target, current);
            string resolvedRoot = Path.GetPathRoot(resolved)!;
            // Restart from the link's target, then continue with the remaining components.
            foreach (string part in resolved[resolvedRoot.Length..]
                         .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Reverse())
                pending.Push(part);
            current = resolvedRoot;
        }

        return current;
    }

    // Windows and macOS file systems are case-insensitive by default; Linux is case-sensitive.
    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static PdfInput Failed(string message) => new([], 0, Shape.Error(message));
}
