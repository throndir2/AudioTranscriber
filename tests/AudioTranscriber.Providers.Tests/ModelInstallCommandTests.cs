using AudioTranscriber.Benchmarks;
using AudioTranscriber.Providers;
using Xunit;

namespace AudioTranscriber.Providers.Tests;

public sealed class ModelInstallCommandTests
{
    [Fact]
    public async Task ListDisclosesAllPinnedSizesLicensesAndHashesWithoutInstalling()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await ModelInstallCommand.RunAsync(["--list"], output, error, NeverInstall);
        Assert.Equal(0, code);
        foreach (var model in LocalWhisperModelCatalog.All)
        {
            Assert.Contains(model.Id, output.ToString());
            Assert.Contains(model.Bytes.ToString(), output.ToString());
            Assert.Contains(model.Sha256, output.ToString());
            Assert.Contains(model.LicenseUri.ToString(), output.ToString());
        }
        Assert.Contains("exceeds 1 GiB", output.ToString());
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 77691713)]
    [InlineData(true, 77691712)]
    public async Task BothLicenseAndExactByteAcknowledgmentsAreRequired(bool license, long bytes)
    {
        var args = new List<string> { "--model", "tiny", "--directory", "never-created" };
        if (license) args.Add("--accept-model-license");
        if (bytes > 0) args.AddRange(["--accept-download-bytes", bytes.ToString()]);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await ModelInstallCommand.RunAsync(args.ToArray(), output, error, NeverInstall));
        Assert.Contains("Required SHA256:", output.ToString());
        Assert.Contains("Download not authorized", error.ToString());
    }

    [Fact]
    public async Task ApprovedInstallInvokesTheExistingCatalogDownloaderContract()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var calls = 0;
        var result = await ModelInstallCommand.RunAsync(
            ["--model", "tiny", "--directory", "selected-model-folder",
                "--accept-model-license", "--accept-download-bytes", "77691713"],
            output, error, (model, directory, approved, progress, ct) =>
            {
                Assert.Same(LocalWhisperModelCatalog.All[0], model);
                Assert.Equal("selected-model-folder", directory);
                Assert.True(approved);
                Assert.Contains(model.Sha256, output.ToString());
                progress!.Report(model.Bytes);
                calls++;
                return Task.FromResult(Path.Combine(directory, model.FileName));
            });
        Assert.Equal(0, result);
        Assert.Equal(1, calls);
        Assert.Contains("SHA256 verified", output.ToString());
        Assert.Contains(".LICENSE.txt", output.ToString());
    }

    [Theory]
    [InlineData("--production-config", "must-not-read.json")]
    [InlineData("--approve-cloud", "")]
    [InlineData("--api-key", "public-test-placeholder")]
    [InlineData("--accept-download-bytes", "not-a-number")]
    public async Task CloudAndInvalidOptionsAreRejectedWithoutEchoingValues(string option, string value)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(6, await ModelInstallCommand.RunAsync([option, value], output, error, NeverInstall));
        Assert.DoesNotContain(value.Length == 0 ? "never-present" : value, error.ToString());
        Assert.Equal("", output.ToString());
    }

    private static Task<string> NeverInstall(LocalWhisperModel model, string directory, bool approved,
        IProgress<long>? progress, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("No model installer should be invoked.");
}
