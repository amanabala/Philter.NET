// De-identification logic ported from Philter (philter-ucsf / philter-lite).
// Copyright (c) 2018, REGENTS OF THE UNIVERSITY OF CALIFORNIA. BSD 3-Clause License.

namespace Philter.NET;

/// <summary>
/// Tokenizes a clinical note into the (word, POS-tag, char-offset) tuples that Philter's
/// set/pos_matcher filters operate on. Tags should be Penn Treebank style (NNP, VB, CD,
/// JJ, ...) because the philter_delta filter config references those tags directly.
/// </summary>
public interface IPosTagger
{
    IReadOnlyList<TaggedToken> Tag(string text);
}

public readonly record struct TaggedToken(string Word, string Pos, int Start, int Stop);
