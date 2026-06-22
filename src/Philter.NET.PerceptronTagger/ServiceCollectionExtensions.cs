using Microsoft.Extensions.DependencyInjection;
using Philter.NET;

namespace Philter.NET.PerceptronTagger;

/// <summary>
/// DI extension methods for wiring the lightweight perceptron POS tagger.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PerceptronPosTagger"/> as the <see cref="IPosTagger"/> implementation —
    /// a pure-managed, ~5.5 MB faithful port of nltk's averaged-perceptron tagger. A small-footprint
    /// alternative to <c>AddCatalystPosTagger()</c> for consumers who only need POS (not NER).
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddPhilter()
    ///         .AddClinicalSidecar()
    ///         .AddPerceptronTagger();   // instead of .AddCatalystPosTagger()
    /// </code>
    /// </example>
    public static PhilterBuilder AddPerceptronTagger(this PhilterBuilder builder)
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));
        builder.Services.AddSingleton<IPosTagger, PerceptronPosTagger>();
        return builder;
    }

    /// <summary>
    /// Plain <see cref="IServiceCollection"/> overload for consumers that don't chain off
    /// <c>AddPhilter()</c>.
    /// </summary>
    public static IServiceCollection AddPerceptronTagger(this IServiceCollection services)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        services.AddSingleton<IPosTagger, PerceptronPosTagger>();
        return services;
    }
}
