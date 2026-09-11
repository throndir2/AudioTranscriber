using System.Formats.Tar;
using System.Net;
using System.Security.Cryptography;
using Xunit;

namespace AudioTranscriber.Diarization.Tests;

public sealed class ModelSafetyTests
{
    [Theory]
    [InlineData("../escape.onnx", TarEntryType.RegularFile)]
    [InlineData("sherpa-onnx-pyannote-segmentation-3-0/../../escape", TarEntryType.RegularFile)]
    [InlineData("sherpa-onnx-pyannote-segmentation-3-0/link", TarEntryType.SymbolicLink)]
    [InlineData("sherpa-onnx-pyannote-segmentation-3-0/model.onnx:stream", TarEntryType.RegularFile)]
    public async Task UnsafeArchivePathsAndLinksAreRejected(string name, TarEntryType type)
    {
        using var directory = new TestDirectory();
        using var archive = new MemoryStream();
        using (var writer = new TarWriter(archive, TarEntryFormat.Pax, leaveOpen: true))
        {
            var entry = new PaxTarEntry(type, name);
            if (type == TarEntryType.SymbolicLink) entry.LinkName = "../outside";
            else entry.DataStream = new MemoryStream([1, 2, 3]);
            writer.WriteEntry(entry);
        }
        archive.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DiarizationModels.ExtractSafeTarAsync(archive, directory.Path));
    }

    [Fact]
    public async Task BoundedDownloadRejectsExtraBytesAndCleansPartial()
    {
        using var directory = new TestDirectory();
        using var client = new HttpClient(new FakeDownload([1, 2, 3, 4]));
        await Assert.ThrowsAsync<InvalidDataException>(() => DiarizationModels.DownloadAsync(client,
            new Uri("https://example.invalid/model"), System.IO.Path.Combine(directory.Path, "model"), 3,
            Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 }))));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task DownloadRejectsHashMismatchAndDoesNotPublish()
    {
        using var directory = new TestDirectory();
        using var client = new HttpClient(new FakeDownload([1, 2, 3]));
        await Assert.ThrowsAsync<InvalidDataException>(() => DiarizationModels.DownloadAsync(client,
            new Uri("https://example.invalid/model"), System.IO.Path.Combine(directory.Path, "model"), 3, new string('0', 64)));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task MatchingDownloadIsPublishedAndReusedWithoutNetwork()
    {
        using var directory = new TestDirectory();
        var handler = new FakeDownload([1, 2, 3]);
        using var client = new HttpClient(handler);
        var path = System.IO.Path.Combine(directory.Path, "model");
        var hash = Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 }));
        await DiarizationModels.DownloadAsync(client, new Uri("https://example.invalid/model"), path, 3, hash);
        await DiarizationModels.DownloadAsync(client, new Uri("https://example.invalid/model"), path, 3, hash);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
    }

    private sealed class FakeDownload(byte[] bytes) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request, Content = new ByteArrayContent(bytes)
            });
        }
    }
}

internal sealed class TestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(Environment.CurrentDirectory, "diarization-test-data", Guid.NewGuid().ToString("N"));
    public TestDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, true);
}
