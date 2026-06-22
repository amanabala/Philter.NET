namespace Philter.NET;

/// <summary>
/// Optional name-detection signal. When an implementation is registered, the
/// de-identification pipeline treats the returned spans as person-name PHI that
/// <b>overrides</b> the whitelist — recovering names a gazetteer cannot, most importantly
/// surnames that are also common English words (e.g. "Read", "Young", "Stone"), which the
/// base <c>nonames</c> whitelist would otherwise keep.
///
/// <para>
/// This is an <b>opt-in</b> recall booster: if no <see cref="INameRecognizer"/> is registered,
/// the pipeline behaves exactly as before. A WikiNER-based implementation is planned in a
/// separate companion package; you can also implement this interface with your own NER.
/// </para>
/// </summary>
public interface INameRecognizer
{
    /// <summary>
    /// Returns half-open <c>[Start, Stop)</c> character spans, relative to <paramref name="text"/>,
    /// that the recognizer believes are person names. Implementations should be conservative —
    /// every returned span that is not actually a name becomes an over-redaction.
    /// </summary>
    IReadOnlyList<(int Start, int Stop)> FindNames(string text);
}
