using AudioTranscriber.App.Templates;
using Xunit;

namespace AudioTranscriber.App.Tests;

public sealed class ContextBudgetTests
{
    [Fact]
    public void EstimatesAboutThreeAsciiCharactersPerTokenAndOneTokenPerOtherCharacter()
    {
        Assert.Equal(0, ContextBudget.EstimateTokens(""));
        Assert.Equal(4, ContextBudget.EstimateTokens("Speaker 1: "));
        Assert.Equal(3, ContextBudget.EstimateTokens("日本語"));
    }

    [Fact]
    public void TailCharsKeepsTheMostRecentTextThatFits()
    {
        var text = new string('a', 3000);
        Assert.Equal(300, ContextBudget.TailChars(text, 100));
        Assert.Equal(3000, ContextBudget.TailChars(text, 5000));
        Assert.Equal(0, ContextBudget.TailChars(text, 0));
        Assert.Equal(2, ContextBudget.TailChars("abc日本", 2));
    }

    [Fact]
    public void OllamaContextGrowsInPowersOfTwoUpToTheCapAndNeverShrinks()
    {
        Assert.Equal(8192, ContextBudget.OllamaContext(0, 3000, 131072));
        Assert.Equal(65536, ContextBudget.OllamaContext(8192, 40000, 131072));
        Assert.Equal(65536, ContextBudget.OllamaContext(65536, 5000, 131072));
        Assert.Equal(131072, ContextBudget.OllamaContext(65536, 500000, 131072));
        Assert.Equal(262144, ContextBudget.OllamaContext(0, 200000, 0));
        Assert.Equal(4096, ContextBudget.OllamaContext(0, 3000, 4096));
    }
}
