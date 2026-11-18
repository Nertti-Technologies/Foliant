using Foliant.Mcp.Tools;
using Xunit;

namespace Foliant.Mcp.Tests;

public class PdfInputTests
{
    [Fact]
    public async Task MissingFile_ReturnsShapedError()
    {
        string path = Path.Combine(Path.GetTempPath(), $"foliant-missing-{Guid.NewGuid():N}.pdf");

        var input = await PdfInput.ReadAsync(path, CancellationToken.None);

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

            var input = await PdfInput.ReadAsync(path, CancellationToken.None);

            Assert.NotNull(input.Error);
            Assert.Contains("Could not read PDF", input.Error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Cancellation_PropagatesInsteadOfBecomingAnError()
    {
        string path = Path.GetTempFileName();
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => PdfInput.ReadAsync(path, cts.Token));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
