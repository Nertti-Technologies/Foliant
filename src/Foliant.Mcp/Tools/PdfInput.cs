using System.Globalization;
using System.Text;
using Foliant.Mcp.Extraction;
using Foliant.Mcp.Shaping;

namespace Foliant.Mcp.Tools;

/// <summary>A PDF read from disk for a tool call: its bytes and page count, or a shaped error.</summary>
/// <remarks>
/// The path comes from the MCP client, which acts on prompts that may contain document text, so it is
/// treated as untrusted input (ADR-0010 T4). Checks, in order, before the file is read: the path is
/// absolute and not a Windows device path (<c>\\?\</c>, <c>\\.\</c>); with
/// <see cref="FoliantMcpOptions.AllowedRoots"/> set, the path as written is inside a root (checked as
/// text, so a UNC or automounted path outside the roots is refused before the file system is touched)
/// and so is its real path (every symbolic link resolved); it is at least as long as the signature and no larger than
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
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Contains('\0', StringComparison.Ordinal))
            return Failed("The path must be absolute.");
        if (IsDevicePath(path))
            return Failed($"Device paths are not accepted: {path}");

        var roots = Roots(options);
        // Network paths (UNC on Windows, //host) make the OS connect and, on Windows, authenticate to
        // that host: allowed only when a configured root is itself a network path (ADR-0010 T4).
        bool networkAllowed = roots?.Any(root => IsNetworkPath(root.Configured) || IsNetworkPath(root.Real)) ?? false;
        if (!networkAllowed && IsNetworkPath(path))
            return Failed($"Network paths are not accepted unless Foliant__AllowedRoots lists one: {path}");

        string full, real;
        try
        {
            // Guard before touch: with roots set, the path as written must already be inside one, so
            // nothing outside them (a UNC share, an automount) is stat'ed while resolving links.
            full = Path.GetFullPath(path);
            if (roots is not null && !roots.Any(root => IsInside(full, root.Configured) || IsInside(full, root.Real)))
                return OutsideRoots(path);
            real = RealPath(full, refuseNetworkLinks: !networkAllowed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Failed($"Could not resolve the path: {path}");
        }

        if (roots is not null && !roots.Any(root => IsInside(real, root.Real)))
            return OutsideRoots(path);

        var file = new FileInfo(real);
        if (!file.Exists)
            return Failed($"File not found: {path}");
        // A FIFO or device reports length 0, and opening a FIFO blocks; nothing shorter than the
        // signature can be a PDF, so it is refused before the file is opened.
        if (file.Length < Signature.Length)
            return Failed($"Not a PDF file (too short): {path}");
        long maxBytes = Math.Min(options.MaxInputBytes, Array.MaxLength);
        if (file.Length > maxBytes)
            return Failed($"The file is larger than the {maxBytes.ToString("N0", CultureInfo.InvariantCulture)}-byte limit (Foliant__MaxInputBytes).");

        try
        {
            byte[] pdf;
            await using (var stream = new FileStream(real, FileMode.Open, FileAccess.Read, FileShare.Read,
                             bufferSize: 1, useAsync: true))
            {
                // Read the length checked above; a file that grows meanwhile is not read past it.
                long length = Math.Min(stream.Length, maxBytes);
                pdf = new byte[length];
                // The signature first, so a large non-PDF is refused after reading 1 KiB, not all of it.
                int head = (int)Math.Min(length, SignatureWindow);
                await stream.ReadExactlyAsync(pdf.AsMemory(0, head), cancellationToken).ConfigureAwait(false);
                if (pdf.AsSpan(0, head).IndexOf(Signature) < 0)
                    return Failed($"Not a PDF file (no %PDF- header): {path}");
                await stream.ReadExactlyAsync(pdf.AsMemory(head), cancellationToken).ConfigureAwait(false);
            }

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
    /// The configured roots, each as written (made absolute) and as its real path, or null when none
    /// are configured (any path is allowed). Missing, relative, device or unresolvable entries are
    /// dropped; if none is usable the list is empty and nothing may be read, rather than falling back
    /// to everything. Roots are operator configuration, so resolving them touches only trusted paths.
    /// </summary>
    internal static IReadOnlyList<(string Configured, string Real)>? Roots(FoliantMcpOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.AllowedRoots)) return null;
        var roots = new List<(string, string)>();
        foreach (string entry in options.AllowedRoots.Split(
                     Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Path.IsPathFullyQualified(entry) || IsDevicePath(entry)) continue;
            try
            {
                string configured = Path.GetFullPath(entry);
                string real = RealPath(configured);
                if (Directory.Exists(real)) roots.Add((configured, real));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // An unresolvable root grants nothing.
            }
        }

        return roots;
    }

    // Windows device paths skip path normalisation (no ".." collapsing) and reach devices and named
    // pipes; a PDF never needs them. Matches .NET's own rule: two separators of either kind, '.' or '?',
    // a separator; or the NT prefix \??\.
    internal static bool IsDevicePath(string path) =>
        (path.Length >= 4 && IsSeparator(path[0]) && IsSeparator(path[1]) && (path[2] is '.' or '?') && IsSeparator(path[3]))
        || path.StartsWith(@"\??\", StringComparison.Ordinal);

    // Two leading separators: a UNC path (Windows) or an implementation-defined root (POSIX).
    private static bool IsNetworkPath(string path) => path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]);

    private static bool IsSeparator(char c) => c is '/' or '\\';

    private static PdfInput OutsideRoots(string path) =>
        Failed($"The file is outside the folders this server may read (Foliant__AllowedRoots): {path}");

    internal static bool IsInside(string path, string root)
    {
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    /// <summary>
    /// The canonical path with every symbolic link resolved, component by component (a link to a
    /// directory part way along would otherwise hide where the file really is). Throws
    /// <see cref="IOException"/> on a link loop and, with <paramref name="refuseNetworkLinks"/>, on a
    /// link to a network or device path, before that target is touched.
    /// </summary>
    internal static string RealPath(string path, bool refuseNetworkLinks = false)
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
            if (refuseNetworkLinks && (IsNetworkPath(target) || IsDevicePath(target)))
                throw new IOException("A symbolic link points to a network or device path.");
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

    // Windows and macOS file systems are case-insensitive by default; Linux is case-sensitive. On a
    // case-sensitive macOS volume this admits a sibling folder differing only in case from a root.
    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static PdfInput Failed(string message) => new([], 0, Shape.Error(message));
}
