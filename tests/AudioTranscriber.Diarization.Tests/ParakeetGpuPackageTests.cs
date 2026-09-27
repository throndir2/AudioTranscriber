using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.BZip2;
using Xunit;

namespace AudioTranscriber.Diarization.Tests;

public sealed class ParakeetGpuPackageTests
{
    [Fact]
    public void ManifestPinsEveryFileOnceAndStaysInsideItsFolders()
    {
        var files = ParakeetGpuPackage.Sources.SelectMany(source => source.Files).ToArray();
        Assert.Equal(files.Length, files.Select(file => file.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(files, file =>
        {
            Assert.Equal(64, file.Sha256.Length);
            Assert.True(file.Bytes > 0);
            Assert.Equal(file.Name, Path.GetFileName(file.Name));
        });
        Assert.All(ParakeetGpuPackage.Sources, source => Assert.StartsWith("https://", source.Url));
        Assert.Contains(files, file => file.Name == "onnxruntime_providers_cuda.dll");
        Assert.Contains(files, file => file.Name == "encoder.weights" && file.Model);
        Assert.InRange(ParakeetGpuPackage.DownloadBytes, 4_000_000_000, 5_000_000_000);
    }

    [Fact]
    public async Task OnlyPinnedEntriesAreExtractedFromZipAndTarArchives()
    {
        using var directory = new TestDirectory();
        byte[] wanted = [1, 2, 3, 4], other = [9, 9];
        var file = new ParakeetGpuPackage.PinnedFile("pkg/bin/wanted.dll", "wanted.dll", wanted.Length, Convert.ToHexString(SHA256.HashData(wanted)));
        var zip = Path.Combine(directory.Path, "a.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var (name, bytes) in new[] { ("pkg/bin/other.dll", other), ("pkg/bin/wanted.dll", wanted) })
                await using (var stream = archive.CreateEntry(name).Open()) await stream.WriteAsync(bytes);
        }
        await ParakeetGpuPackage.ExtractZipAsync(zip, directory.Path, [file], CancellationToken.None);
        var runtime = ParakeetGpuPackage.RuntimeDirectory(directory.Path);
        Assert.Equal(wanted, await File.ReadAllBytesAsync(Path.Combine(runtime, "wanted.dll")));
        Assert.False(File.Exists(Path.Combine(runtime, "other.dll")));

        File.Delete(Path.Combine(runtime, "wanted.dll"));
        var tar = Path.Combine(directory.Path, "a.tar.bz2");
        await using (var output = File.Create(tar))
        using (var bzip = new BZip2OutputStream(output))
        using (var writer = new TarWriter(bzip, TarEntryFormat.Pax))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "pkg/bin/other.dll") { DataStream = new MemoryStream(other) });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "pkg/bin/wanted.dll") { DataStream = new MemoryStream(wanted) });
        }
        await ParakeetGpuPackage.ExtractTarBz2Async(tar, directory.Path, [file], CancellationToken.None);
        Assert.Equal(wanted, await File.ReadAllBytesAsync(Path.Combine(runtime, "wanted.dll")));

        var tampered = file with { Sha256 = new string('0', 64) };
        await Assert.ThrowsAsync<InvalidDataException>(() => ParakeetGpuPackage.ExtractZipAsync(zip, directory.Path, [tampered], CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => ParakeetGpuPackage.ExtractZipAsync(zip, directory.Path,
            [file with { Entry = "pkg/bin/missing.dll" }], CancellationToken.None));
    }
}
