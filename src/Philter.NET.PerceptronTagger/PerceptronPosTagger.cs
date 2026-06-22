// Faithful C# port of nltk's averaged-perceptron POS tagger (the tagger philter-lite uses
// via nltk.pos_tag). Pure managed, offline, emits Penn Treebank tags directly.
//
// Tokenization matches philter-lite's _get_clean: split on whitespace AND non-alphanumerics
// into bare alnum tokens, then tag the whole note as ONE sequence (no sentence splitting).
// That is exactly the granularity FilterEngine.MapSet/MapPos already operate on, so this is a
// more faithful reproduction of philter-lite than Treebank tokenization would be.

using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Philter.NET.PerceptronTagger;

/// <summary>
/// Lightweight <see cref="IPosTagger"/> backed by nltk's averaged-perceptron model (shipped as
/// embedded JSON, ~5.5 MB). A small-footprint, pure-managed alternative to the Catalyst tagger
/// for consumers who only need POS. Loads the model once in the constructor (singleton).
/// </summary>
public sealed class PerceptronPosTagger : IPosTagger
{
    private static readonly string[] Start = { "-START-", "-START2-" };
    private static readonly string[] End = { "-END-", "-END2-" };

    private readonly AveragedPerceptron _model;
    private readonly IReadOnlyDictionary<string, string> _tagdict;

    public PerceptronPosTagger() : this(NullLogger<PerceptronPosTagger>.Instance) { }

    public PerceptronPosTagger(ILogger<PerceptronPosTagger> logger)
    {
        var asm = typeof(PerceptronPosTagger).Assembly;
        var weights = LoadJson<Dictionary<string, Dictionary<string, double>>>(asm, "weights.json");
        _tagdict = LoadJson<Dictionary<string, string>>(asm, "tagdict.json");
        var classes = LoadJson<string[]>(asm, "classes.json");
        _model = new AveragedPerceptron(weights, classes);
        logger.LogInformation(
            "Perceptron POS tagger loaded ({Features} features, {Tagdict} tagdict words, {Classes} tags).",
            weights.Count, _tagdict.Count, classes.Length);
    }

    private static T LoadJson<T>(Assembly asm, string suffix)
    {
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith(suffix, StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<T>(stream)
               ?? throw new InvalidOperationException($"Failed to load embedded model resource '{name}'.");
    }

    /// <inheritdoc/>
    public IReadOnlyList<TaggedToken> Tag(string text)
    {
        var result = new List<TaggedToken>();
        if (string.IsNullOrEmpty(text)) return result;

        // Tokenize: maximal [A-Za-z0-9]+ runs, carrying their char offsets.
        var tokens = new List<(string Value, int Start, int Stop)>();
        int n = text.Length, p = 0;
        while (p < n)
        {
            if (!IsAlnum(text[p])) { p++; continue; }
            int s = p;
            while (p < n && IsAlnum(text[p])) p++;
            tokens.Add((text.Substring(s, p - s), s, p));
        }
        if (tokens.Count == 0) return result;

        // context = [-START-, -START2-] + normalized tokens + [-END-, -END2-]
        var context = new string[tokens.Count + 4];
        context[0] = Start[0];
        context[1] = Start[1];
        for (int k = 0; k < tokens.Count; k++) context[k + 2] = Normalize(tokens[k].Value);
        context[^2] = End[0];
        context[^1] = End[1];

        string prev = Start[0], prev2 = Start[1];
        for (int k = 0; k < tokens.Count; k++)
        {
            var (value, start, stop) = tokens[k];
            // tagdict fast-path uses the RAW (case-sensitive) token, per nltk.
            string tag = _tagdict.TryGetValue(value, out var td)
                ? td
                : _model.Predict(GetFeatures(k, value, context, prev, prev2));
            result.Add(new TaggedToken(value, tag, start, stop));
            prev2 = prev;
            prev = tag;
        }
        return result;
    }

    private static bool IsAlnum(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');

    // Mirrors PerceptronTagger.normalize. Note: our alnum tokens never contain '-', so the
    // !HYPHEN branch is effectively dead here — same as philter-lite, whose _get_clean also
    // strips non-alnum before tagging. Kept for fidelity.
    private static string Normalize(string word)
    {
        if (word.Length > 0 && word[0] != '-' && word.IndexOf('-') >= 0) return "!HYPHEN";
        if (word.Length == 4 && IsAllDigits(word)) return "!YEAR";
        if (word.Length > 0 && char.IsDigit(word[0])) return "!DIGITS";
        return word.ToLowerInvariant();
    }

    private static bool IsAllDigits(string s)
    {
        if (s.Length == 0) return false;
        foreach (var c in s) if (c < '0' || c > '9') return false;
        return true;
    }

    private static string Suffix(string s) => s.Length <= 3 ? s : s.Substring(s.Length - 3);

    // Mirrors PerceptronTagger._get_features. Feature keys are space-joined "name [args...]",
    // matching the model's weight-table keys (e.g. "i-1 suffix ity"). Raw token drives
    // suffix/pref1; normalized context drives the word features.
    private static Dictionary<string, int> GetFeatures(int i, string word, string[] context, string prev, string prev2)
    {
        var features = new Dictionary<string, int>();
        void Add(string name, string? a = null, string? b = null)
        {
            string key = b is not null ? $"{name} {a} {b}" : a is not null ? $"{name} {a}" : name;
            features.TryGetValue(key, out var c);
            features[key] = c + 1;
        }

        i += Start.Length; // index into the padded context array
        Add("bias");
        Add("i suffix", Suffix(word));
        Add("i pref1", word.Length > 0 ? word[0].ToString() : "");
        Add("i-1 tag", prev);
        Add("i-2 tag", prev2);
        Add("i tag+i-2 tag", prev, prev2);
        Add("i word", context[i]);
        Add("i-1 tag+i word", prev, context[i]);
        Add("i-1 word", context[i - 1]);
        Add("i-1 suffix", Suffix(context[i - 1]));
        Add("i-2 word", context[i - 2]);
        Add("i+1 word", context[i + 1]);
        Add("i+1 suffix", Suffix(context[i + 1]));
        Add("i+2 word", context[i + 2]);
        return features;
    }
}
