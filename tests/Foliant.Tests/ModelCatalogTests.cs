using Foliant.Models;
using Xunit;

namespace Foliant.Tests;

public class ModelCatalogTests
{
    [Fact]
    public void AllAssets_HaveWellFormedMetadata()
    {
        foreach (var asset in ModelCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(asset.Id));
            Assert.False(string.IsNullOrWhiteSpace(asset.FileName));
            Assert.StartsWith("https://huggingface.co/", asset.Url);
            Assert.Matches("^[0-9a-f]{64}$", asset.Sha256);
            Assert.True(asset.SizeBytes > 0);
        }
    }

    [Fact]
    public void Ids_AndFileNames_AreUnique()
    {
        Assert.Equal(ModelCatalog.All.Count, ModelCatalog.All.Select(a => a.Id).Distinct().Count());
        Assert.Equal(ModelCatalog.All.Count, ModelCatalog.All.Select(a => a.FileName).Distinct().Count());
    }

    [Fact]
    public void DefaultPipeline_IsSubsetOfAll()
    {
        Assert.All(ModelCatalog.DefaultPipeline, a => Assert.Contains(a, ModelCatalog.All));
    }

    [Fact]
    public async Task ModelCache_RejectsChecksumMismatch()
    {
        string dir = Path.Combine(Path.GetTempPath(), "foliant-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            // Pre-place a file with wrong contents; cache must not accept it silently.
            var asset = ModelCatalog.OcrRecognitionEnglishDict with
            {
                Url = "https://huggingface.co/invalid/never-fetched",
            };
            await File.WriteAllTextAsync(Path.Combine(dir, asset.FileName), "tampered");

            var cache = new ModelCache(dir, new HttpClient(new FailingHandler()));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => cache.GetPathAsync(asset));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ModelCache_ReturnsThePathJoinedOntoARelativeCacheDirectory()
    {
        string dir = Path.Join(Path.GetTempPath(), "foliant-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var asset = ModelCatalog.OcrRecognitionEnglishDict;
            string relative = Path.GetRelativePath(Environment.CurrentDirectory, dir);
            await File.WriteAllTextAsync(Path.Join(dir, asset.FileName), "verified earlier");
            await File.WriteAllTextAsync(Path.Join(dir, asset.FileName + ".sha256-ok"), asset.Sha256);

            var cache = new ModelCache(relative, new HttpClient(new FailingHandler()));
            Assert.Equal(Path.Join(relative, asset.FileName), await cache.GetPathAsync(asset));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AllAssets_HavePlainFileNames()
    {
        Assert.All(ModelCatalog.All, a => Assert.Equal(Path.GetFileName(a.FileName), a.FileName));
    }

    [Theory]
    [InlineData("../escape.onnx")]
    [InlineData("..")]
    [InlineData("sub/model.onnx")]
    [InlineData("sub\\model.onnx")]
    [InlineData("/etc/escape.onnx")]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("   ")]
    [InlineData("...")]
    [InlineData("model.onnx.")]
    [InlineData("model.onnx ")]
    [InlineData("C:x.onnx")]
    [InlineData("model.onnx:stream")]
    public void ModelAsset_RejectsFileNamesOutsideTheCache(string fileName)
    {
        var asset = ModelCatalog.OcrRecognitionEnglishDict;
        Assert.Throws<ArgumentException>(nameof(ModelAsset.FileName),
            () => new ModelAsset(asset.Id, fileName, asset.Url, asset.Sha256, asset.SizeBytes));
        Assert.Throws<ArgumentException>(nameof(ModelAsset.FileName),
            () => asset with { FileName = fileName });
    }

    [Theory]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]
    public void ModelAsset_RejectsInvalidSha256(string sha256)
    {
        var asset = ModelCatalog.OcrRecognitionEnglishDict;
        Assert.Throws<ArgumentException>(nameof(ModelAsset.Sha256),
            () => new ModelAsset(asset.Id, asset.FileName, asset.Url, sha256, asset.SizeBytes));
        Assert.Throws<ArgumentException>(nameof(ModelAsset.Sha256),
            () => asset with { Sha256 = sha256 });
    }




    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline test");
    }
}
