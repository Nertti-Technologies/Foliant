using Foliant.Mcp;
using Foliant.Mcp.Tools;
using Xunit;

namespace Foliant.Mcp.Tests;

public class PdfInputTests
{
    [Fact]
    public async Task MissingFile_ReturnsShapedError()
    {
        string path = Path.Join(Path.GetTempPath(), $"foliant-missing-{Guid.NewGuid():N}.pdf");

        var input = await PdfInput.ReadAsync(path, new FoliantMcpOptions(), CancellationToken.None);

        Assert.NotNull(input.Error);
        Assert.Contains("File not found", input.Error);
    }

    [Fact]
    public async Task NotAPdf_ReturnsShapedError()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "this is not a pdf");

            var input = await PdfInput.ReadAsync(path, new FoliantMcpOptions(), CancellationToken.None);

            Assert.NotNull(input.Error);
            Assert.Contains("Not a PDF file", input.Error);
            Assert.DoesNotContain("Exception", input.Error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static readonly byte[] MinimalHeader = "%PDF-1.7\n"u8.ToArray();

    // Creating symbolic links needs a privilege on Windows without Developer Mode: skip, don't fail.
    private static bool TryCreateLinks(params Action[] create)
    {
        try
        {
            foreach (var c in create) c();
            return true;
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string TempDir()
    {
        string dir = Path.Join(Path.GetTempPath(), $"foliant-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task PathWithANulCharacter_IsAShapedError()
    {
        string path = Path.Join(Path.GetTempPath(), "a\0b.pdf");
        var input = await PdfInput.ReadAsync(path, new FoliantMcpOptions(), CancellationToken.None);
        Assert.Contains("must be absolute", input.Error);
    }

    [Theory]
    [InlineData(@"\\?\C:\docs\a.pdf")]
    [InlineData(@"\\.\pipe\foliant")]
    [InlineData("//./pipe/foliant")]
    public async Task DevicePath_IsRefused(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return;   // not an absolute path on this OS: refused earlier
        var input = await PdfInput.ReadAsync(path, new FoliantMcpOptions(), CancellationToken.None);
        Assert.Contains("Device paths are not accepted", input.Error);
    }

    [Theory]
    [InlineData(@"\\?\C:\a.pdf", true)]
    [InlineData(@"\\.\pipe\x", true)]
    [InlineData(@"/\.\pipe\x", true)]
    [InlineData(@"\/?/C:/a.pdf", true)]
    [InlineData(@"\??\C:\a.pdf", true)]
    [InlineData(@"\\server\share\a.pdf", false)]
    [InlineData("/tmp/a.pdf", false)]
    public void IsDevicePath_MatchesTheDotNetRule(string path, bool expected) =>
        Assert.Equal(expected, PdfInput.IsDevicePath(path));

    [Fact]
    public async Task LinkInsideARoot_ToANetworkPath_IsRefusedWithoutFollowingIt()
    {
        if (OperatingSystem.IsWindows()) return;   // creating a link to a UNC target needs a share to point at
        string allowed = TempDir();
        try
        {
            string link = Path.Join(allowed, "net");
            File.CreateSymbolicLink(link, "//203.0.113.1/share");

            var input = await PdfInput.ReadAsync(Path.Join(link, "x.pdf"), new FoliantMcpOptions { AllowedRoots = allowed },
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Contains("Could not resolve the path", input.Error);
        }
        finally
        {
            Directory.Delete(allowed, recursive: true);
        }
    }

    [Fact]
    public async Task NetworkPath_IsRefused_WhenNoRootIsANetworkPath()
    {
        string unc = OperatingSystem.IsWindows() ? @"\\203.0.113.1\share\x.pdf" : "//203.0.113.1/share/x.pdf";

        var input = await PdfInput.ReadAsync(unc, new FoliantMcpOptions(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("Network paths are not accepted", input.Error);
    }

    [Fact]
    public async Task LinkToANetworkPath_IsRefused_WithRootsUnset()
    {
        if (OperatingSystem.IsWindows()) return;
        string dir = TempDir();
        try
        {
            string link = Path.Join(dir, "net");
            File.CreateSymbolicLink(link, "//203.0.113.1/share");

            var input = await PdfInput.ReadAsync(Path.Join(link, "x.pdf"), new FoliantMcpOptions(), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Contains("Could not resolve the path", input.Error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LinkToANetworkPath_IsFollowedOnlyIntoAConfiguredNetworkRoot()
    {
        if (OperatingSystem.IsWindows()) return;   // on POSIX "//host" is a local path, so nothing connects
        string dir = TempDir();
        try
        {
            string inside = Path.Join(dir, "inside"), other = Path.Join(dir, "other");
            File.CreateSymbolicLink(inside, "//203.0.113.1/share/sub");
            File.CreateSymbolicLink(other, "//203.0.113.2/x");
            IReadOnlyList<string> networkRoots = ["//203.0.113.1/share"];

            PdfInput.RealPath(Path.Join(inside, "a.pdf"), networkRoots);   // allowed: no throw
            Assert.Throws<IOException>(() => PdfInput.RealPath(Path.Join(other, "a.pdf"), networkRoots));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task LocalPathOutsideTheRoots_IsRefused()
    {
        string allowed = TempDir(), other = TempDir();
        try
        {
            var input = await PdfInput.ReadAsync(Path.Join(other, "missing.pdf"),
                new FoliantMcpOptions { AllowedRoots = allowed }, CancellationToken.None);

            Assert.Contains("outside the folders", input.Error);   // not "File not found": no existence answer
        }
        finally
        {
            Directory.Delete(allowed, recursive: true);
            Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public async Task NetworkPathOutsideTheRoots_IsRefused_BeforeItIsResolved()
    {
        string allowed = TempDir();
        try
        {
            // A network path outside the roots: refused by the textual check, so it is never stat'ed.
            string unc = OperatingSystem.IsWindows() ? @"\\203.0.113.1\share\x.pdf" : "//203.0.113.1/share/x.pdf";
            var options = new FoliantMcpOptions { AllowedRoots = allowed };

            var input = await PdfInput.ReadAsync(unc, options, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Contains("Network paths are not accepted", input.Error);   // refused before it is touched
        }
        finally
        {
            Directory.Delete(allowed, recursive: true);
        }
    }

    [Fact]
    public async Task RelativePath_IsRefused()
    {
        var input = await PdfInput.ReadAsync("some/file.pdf", new FoliantMcpOptions(), CancellationToken.None);
        Assert.Contains("must be absolute", input.Error);
    }

    [Fact]
    public async Task FileOverTheSizeLimit_IsRefusedBeforeReading()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Join(dir, "big.pdf");
            await File.WriteAllBytesAsync(path, [.. MinimalHeader, .. new byte[2048]]);

            var input = await PdfInput.ReadAsync(path, new FoliantMcpOptions { MaxInputBytes = 1024 }, CancellationToken.None);

            Assert.Contains("larger than the 1,024-byte limit", input.Error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SignatureAfterTheFirst1024Bytes_IsNotAPdf()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Join(dir, "late.pdf");
            await File.WriteAllBytesAsync(path, [.. new byte[1024], .. MinimalHeader]);

            var input = await PdfInput.ReadAsync(path, new FoliantMcpOptions(), CancellationToken.None);

            Assert.Contains("Not a PDF file", input.Error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SignatureWithinTheWindow_PassesToTheParser()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Join(dir, "junk.pdf");
            await File.WriteAllBytesAsync(path, [.. new byte[100], .. MinimalHeader]);

            var input = await PdfInput.ReadAsync(path, new FoliantMcpOptions(), CancellationToken.None);

            // Past the signature check; the parser then rejects the truncated body without its message.
            Assert.Contains("not a valid PDF", input.Error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task OutsideAllowedRoots_IsRefused_AndInsideIsNot()
    {
        string allowed = TempDir(), other = TempDir();
        try
        {
            string inside = Path.Join(allowed, "a.pdf"), outside = Path.Join(other, "b.pdf");
            await File.WriteAllBytesAsync(inside, MinimalHeader);
            await File.WriteAllBytesAsync(outside, MinimalHeader);
            var options = new FoliantMcpOptions { AllowedRoots = allowed };

            var refused = await PdfInput.ReadAsync(outside, options, CancellationToken.None);
            var admitted = await PdfInput.ReadAsync(inside, options, CancellationToken.None);

            Assert.Contains("outside the folders", refused.Error);
            Assert.DoesNotContain("outside the folders", admitted.Error ?? "");
        }
        finally
        {
            Directory.Delete(allowed, recursive: true);
            Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public async Task SymbolicLinkInsideARoot_PointingOutside_IsRefused()
    {
        string allowed = TempDir(), other = TempDir();
        try
        {
            string target = Path.Join(other, "secret.pdf");
            await File.WriteAllBytesAsync(target, MinimalHeader);
            string link = Path.Join(allowed, "link.pdf");
            string dirLink = Path.Join(allowed, "dir");
            if (!TryCreateLinks(() => File.CreateSymbolicLink(link, target), () => Directory.CreateSymbolicLink(dirLink, other)))
                return;
            var options = new FoliantMcpOptions { AllowedRoots = allowed };

            var viaFile = await PdfInput.ReadAsync(link, options, CancellationToken.None);
            var viaDir = await PdfInput.ReadAsync(Path.Join(dirLink, "secret.pdf"), options, CancellationToken.None);
            var viaDotDot = await PdfInput.ReadAsync(Path.Join(allowed, "..", Path.GetFileName(other), "secret.pdf"), options, CancellationToken.None);

            Assert.Contains("outside the folders", viaFile.Error);
            Assert.Contains("outside the folders", viaDir.Error);
            Assert.Contains("outside the folders", viaDotDot.Error);
        }
        finally
        {
            Directory.Delete(allowed, recursive: true);
            Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public async Task AllowedRoots_WithNoUsableEntry_RefusesEverything()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Join(dir, "a.pdf");
            await File.WriteAllBytesAsync(path, MinimalHeader);
            var options = new FoliantMcpOptions { AllowedRoots = "relative/dir" + Path.PathSeparator + Path.Join(dir, "missing") };

            var input = await PdfInput.ReadAsync(path, options, CancellationToken.None);

            Assert.Contains("outside the folders", input.Error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RealPath_DetectsALinkLoop()
    {
        string dir = TempDir();
        try
        {
            string a = Path.Join(dir, "a"), b = Path.Join(dir, "b");
            if (!TryCreateLinks(() => File.CreateSymbolicLink(a, b), () => File.CreateSymbolicLink(b, a)))
                return;
            Assert.Throws<IOException>(() => PdfInput.RealPath(a));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Fifo_IsRefusedWithoutBlocking()
    {
        if (OperatingSystem.IsWindows()) return;
        string dir = TempDir();
        try
        {
            string fifo = Path.Join(dir, "pipe.pdf");
            using (var mk = System.Diagnostics.Process.Start("mkfifo", fifo)) mk.WaitForExit();

            var input = await PdfInput.ReadAsync(fifo, new FoliantMcpOptions(), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Contains("too short", input.Error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void IsInside_DoesNotMatchASiblingWithTheSamePrefix() =>
        Assert.False(PdfInput.IsInside(Path.Join(Path.GetTempPath(), "docs-private", "a.pdf"),
                                       Path.Join(Path.GetTempPath(), "docs")));

    [Fact]
    public async Task Cancellation_PropagatesInsteadOfBecomingAnError()
    {
        string path = Path.GetTempFileName();
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => PdfInput.ReadAsync(path, new FoliantMcpOptions(), cts.Token));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
