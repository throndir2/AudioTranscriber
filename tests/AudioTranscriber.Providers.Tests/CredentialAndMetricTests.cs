using AudioTranscriber.Benchmarks;
using Xunit;

namespace AudioTranscriber.Providers.Tests;

public sealed class CredentialAndMetricTests : IDisposable
{
    private readonly string directory = Path.Combine(Environment.CurrentDirectory, "provider-test-data", Guid.NewGuid().ToString("N"));
    public CredentialAndMetricTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData("\"Nvidia\"")]
    [InlineData("7")]
    public async Task ProductionLoaderIsReadOnlyUniqueAndNeverInheritsEndpoint(string identity)
    {
        var path = Path.Combine(directory, "synthetic-config.json");
        var json = $$"""{"Providers":[{"ProviderName":{{identity}},"ApiKey":"public-test-placeholder","Endpoint":"https://not-nvidia.invalid"},{"ProviderName":"Other","ApiKey":"unrelated-placeholder"}]}""";
        await File.WriteAllTextAsync(path, json);
        using var vault = await ReadOnlyProductionCredentialLoader.LoadAsync(path);
        using var credential = await vault.GetAsync();
        Assert.NotNull(credential);
        Assert.Equal("Bearer public-test-placeholder", credential.BearerHeader);
        Assert.Equal(json, await File.ReadAllTextAsync(path));
        Assert.DoesNotContain("placeholder", credential.ToString());
        Assert.DoesNotContain("placeholder", vault.ToString());
    }

    [Theory]
    [InlineData("""{"Providers":[{"ProviderName":"Nvidia","ApiKey":"one"},{"ProviderName":7,"ApiKey":"two"}]}""")]
    [InlineData("""{"Providers":[{"ProviderName":"Other","ApiKey":"not-nvidia"}]}""")]
    [InlineData("""{"Providers":[{"ProviderName":"Nvidia","ApiKey":" "}]}""")]
    [InlineData("""{"Providers":[{"ProviderName":"Other","Provider":7,"ApiKey":"other-placeholder"}]}""")]
    [InlineData("""{"Providers":[{"ProviderName":"Nvidia","Provider":3,"ApiKey":"other-placeholder"}]}""")]
    [InlineData("""PRIVATE MALFORMED DATA""")]
    public async Task ProductionLoaderFailsClosedWithoutPrintingConfig(string json)
    {
        var path = Path.Combine(directory, "synthetic-config.json");
        await File.WriteAllTextAsync(path, json);
        var error = await Assert.ThrowsAsync<ProviderException>(() => ReadOnlyProductionCredentialLoader.LoadAsync(path));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(json, error.ToString());
    }

    [Fact]
    public async Task NumericProviderEnumAndOnlyNonemptyNvidiaRowsAreAccepted()
    {
        var path = Path.Combine(directory, "synthetic-config.json");
        await File.WriteAllTextAsync(path, """{"Providers":[{"ProviderName":"Nvidia","ApiKey":""},{"Provider":7,"ApiKey":"public-test-placeholder"}]}""");
        using var vault = await ReadOnlyProductionCredentialLoader.LoadAsync(path);
        using var key = await vault.GetAsync();
        Assert.Equal("Bearer public-test-placeholder", key!.BearerHeader);
    }

    [Fact]
    public async Task InstallerRequiresExplicitApprovalWithoutMakingRequest()
    {
        await Assert.ThrowsAsync<ProviderException>(() => VerifiedModelDownload.InstallWhisperAsync(
            LocalWhisperModelCatalog.All[0], directory, explicitlyApproved: false));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task CredentialPersistenceRequiresApprovalAndUsesCurrentUserDpapi()
    {
        var path = Path.Combine(directory, "nvidia.dpapi");
        using var vault = new NvidiaCredentialVault();
        vault.SetMemoryOnly("public-test-placeholder");
        await Assert.ThrowsAsync<ProviderException>(() => vault.SaveForCurrentUserAsync(path, false));
        Assert.False(File.Exists(path));
        await vault.SaveForCurrentUserAsync(path, true);
        Assert.DoesNotContain("public-test-placeholder", await File.ReadAllTextAsync(path));
        vault.Clear();
        Assert.Null(await vault.GetAsync());
        await vault.LoadForCurrentUserAsync(path);
        using var loaded = await vault.GetAsync();
        Assert.Equal("Bearer public-test-placeholder", loaded!.BearerHeader);
    }

    [Theory]
    [InlineData(" HELLO,  WORLD! ", "hello world")]
    [InlineData("ＡＢＣ １２", "abc 12")]
    [InlineData("A—B don't", "a b don t")]
    public void NormalizationIsExplicitAndDeterministic(string source, string expected) =>
        Assert.Equal(expected, ErrorMetrics.Normalize(source));

    [Fact]
    public void MetricsExposeInsertionDeletionSubstitutionAndEmptyHypothesis()
    {
        var metrics = ErrorMetrics.Compare("one two three", "one too four extra");
        Assert.Equal(2, metrics.Words.Substitutions);
        Assert.Equal(1, metrics.Words.Insertions);
        Assert.Equal(0, metrics.Words.Deletions);
        Assert.Equal(1, metrics.Words.Rate);
        Assert.Equal(1, ErrorMetrics.Compare("one two", "").Words.Rate);
        Assert.Equal(0, ErrorMetrics.Compare("Hello!", "hello").Characters.Rate);
    }

    [Fact]
    public void BudgetCountsFailedAttemptsBeforeExecutionAndHardStopsAtNine()
    {
        var budget = new RequestBudget();
        for (var i = 0; i < 9; i++) budget.CountBeforeAttempt(TimeSpan.FromSeconds(15));
        Assert.Equal(9, budget.Attempts);
        Assert.Equal(135, budget.SubmittedAudioSeconds);
        Assert.Throws<InvalidOperationException>(() => budget.CountBeforeAttempt(TimeSpan.FromSeconds(1)));
        Assert.Equal(9, budget.Attempts);
        Assert.Throws<InvalidOperationException>(() => new RequestBudget().CountBeforeAttempt(TimeSpan.FromSeconds(15.001)));
    }

    public void Dispose() => Directory.Delete(directory, true);
}
