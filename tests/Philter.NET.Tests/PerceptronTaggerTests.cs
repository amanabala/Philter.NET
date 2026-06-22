using Philter.NET;
using Philter.NET.PerceptronTagger;
using Xunit;

namespace Philter.NET.Tests;

/// <summary>
/// Parity tests for the ported averaged-perceptron tagger. Expected (token, tag) sequences
/// were generated from nltk 3.9.4 (<c>nltk.pos_tag</c>) on the SAME alnum-tokenized input
/// philter-lite feeds it (its <c>_get_clean</c> path). If the C# port is faithful, it must
/// reproduce these exactly — tokenization, normalization, features, and the argmax tiebreak.
/// </summary>
public class PerceptronTaggerTests
{
    // Shared singleton — loading the ~5.5 MB model per test is wasteful.
    private static readonly PerceptronPosTagger Tagger = new();

    [Theory]
    // text | space-joined expected tokens | space-joined expected PTB tags (from nltk)
    [InlineData("The quick brown fox jumps over the lazy dog",
        "The quick brown fox jumps over the lazy dog",
        "DT JJ NN NN VBZ IN DT JJ NN")]
    [InlineData("Patient John Doe presents with chest pain and SOB",
        "Patient John Doe presents with chest pain and SOB",
        "NNP NNP NNP NNS IN NN NN CC NNP")]
    [InlineData("BP 142/88, HR 72. Afebrile, well-appearing male in NAD.",
        "BP 142 88 HR 72 Afebrile well appearing male in NAD",
        "NNP CD CD NNP CD NNP RB VBG NN IN NNP")]
    [InlineData("MoCA 14/30. Cranial nerves II-XII intact.",
        "MoCA 14 30 Cranial nerves II XII intact",
        "NNP CD CD JJ NNS NNP NNP NN")]
    [InlineData("Ms. Read is a 52-year-old with stage 3 CKD.",
        "Ms Read is a 52 year old with stage 3 CKD",
        "NNP NNP VBZ DT CD NN JJ IN NN CD NNP")]
    public void Matches_nltk_pos_tags(string text, string expectedTokens, string expectedTags)
    {
        var tagged = Tagger.Tag(text);

        Assert.Equal(expectedTokens.Split(' '), tagged.Select(t => t.Word).ToArray());
        Assert.Equal(expectedTags.Split(' '), tagged.Select(t => t.Pos).ToArray());
    }

    [Fact]
    public void Tokens_carry_correct_input_offsets()
    {
        const string text = "BP 142/88, HR 72.";
        var tagged = Tagger.Tag(text);
        // Each token's offsets must slice back to its surface form.
        Assert.All(tagged, t => Assert.Equal(t.Word, text.Substring(t.Start, t.Stop - t.Start)));
        // "142" sits at index 3.
        var n142 = Assert.Single(tagged, t => t.Word == "142");
        Assert.Equal(3, n142.Start);
        Assert.Equal("CD", n142.Pos);
    }
}
