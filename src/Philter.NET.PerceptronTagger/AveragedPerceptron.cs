// Faithful C# port of nltk.tag.perceptron.AveragedPerceptron (inference only).
// Original: Matthew Honnibal / Long Duong (NLTK port), MIT License.
// We port only the prediction path; training is out of scope.

namespace Philter.NET.PerceptronTagger;

/// <summary>
/// Inference half of nltk's averaged perceptron: a feature → (tag → weight) table and a
/// greedy argmax. Mirrors <c>AveragedPerceptron.predict</c> exactly, including the
/// alphabetical tiebreak over classes and the score-default-to-zero behavior.
/// </summary>
internal sealed class AveragedPerceptron
{
    private readonly IReadOnlyDictionary<string, Dictionary<string, double>> _weights;
    private readonly string[] _classes;

    public AveragedPerceptron(IReadOnlyDictionary<string, Dictionary<string, double>> weights, string[] classes)
    {
        _weights = weights;
        _classes = classes;
    }

    /// <summary>
    /// Dot-product the (sparse, integer-valued) feature map against the weight table and return
    /// the best label. Replicates <c>max(classes, key=lambda l: (scores[l], l))</c>: argmax by
    /// score, ties broken by the ordinally-greater label, with unseen labels scoring 0.
    /// </summary>
    public string Predict(Dictionary<string, int> features)
    {
        var scores = new Dictionary<string, double>();
        foreach (var (feat, value) in features)
        {
            if (value == 0 || !_weights.TryGetValue(feat, out var labelWeights)) continue;
            foreach (var (label, weight) in labelWeights)
            {
                scores.TryGetValue(label, out var cur);
                scores[label] = cur + value * weight;
            }
        }

        string best = _classes[0];
        double bestScore = scores.TryGetValue(best, out var first) ? first : 0.0;
        for (int i = 1; i < _classes.Length; i++)
        {
            string label = _classes[i];
            double s = scores.TryGetValue(label, out var sv) ? sv : 0.0;
            if (s > bestScore || (s == bestScore && string.CompareOrdinal(label, best) > 0))
            {
                bestScore = s;
                best = label;
            }
        }
        return best;
    }
}
