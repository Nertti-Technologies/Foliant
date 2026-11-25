using Foliant.Mcp;
using Foliant.Mcp.Tools;
using Xunit;

namespace Foliant.Mcp.Tests;

public class PdfInputTests
{
    [Fact]
    public async Task MissingFile_ReturnsShapedError()
    {
        string path = Path.Combine(Path.GetTempPath(), $"foliant-missing-{Guid.NewGuid():N}.pdf");

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

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"foliant-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
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
            string path = Path.Combine(dir, "big.pdf");
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
            string path = Path.Combine(dir, "late.pdf");
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
            string path = Path.Combine(dir, "junk.pdf");
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
            string inside = Path.Combine(allowed, "a.pdf"), outside = Path.Combine(other, "b.pdf");
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
            string target = Path.Combine(other, "secret.pdf");
            await File.WriteAllBytesAsync(target, MinimalHeader);
            string link = Path.Combine(allowed, "link.pdf");
            File.CreateSymbolicLink(link, target);
            string dirLink = Path.Combine(allowed, "dir");
            Directory.CreateSymbolicLink(dirLink, other);
            var options = new FoliantMcpOptions { AllowedRoots = allowed };

            var viaFile = await PdfInput.ReadAsync(link, options, CancellationToken.None);
            var viaDir = await PdfInput.ReadAsync(Path.Combine(dirLink, "secret.pdf"), options, CancellationToken.None);
            var viaDotDot = await PdfInput.ReadAsync(Path.Combine(allowed, "..", Path.GetFileName(other), "secret.pdf"), options, CancellationToken.None);

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
            string path = Path.Combine(dir, "a.pdf");
            await File.WriteAllBytesAsync(path, MinimalHeader);
            var options = new FoliantMcpOptions { AllowedRoots = "relative/dir" + Path.PathSeparator + Path.Combine(dir, "missing") };

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
            string a = Path.Combine(dir, "a"), b = Path.Combine(dir, "b");
            File.CreateSymbolicLink(a, b);
            File.CreateSymbolicLink(b, a);
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
            string fifo = Path.Combine(dir, "pipe.pdf");
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
        Assert.False(PdfInput.IsInside(Path.Combine(Path.GetTempPath(), "docs-private", "a.pdf"),
                                       Path.Combine(Path.GetTempPath(), "docs")));

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
