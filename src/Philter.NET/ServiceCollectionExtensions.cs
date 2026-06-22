using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace Philter.NET;

/// <summary>
/// DI extension methods for wiring the Philter.NET pipeline into an
/// <see cref="IServiceCollection"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Philter.NET PHI de-identification pipeline as singletons.
    /// Registers <see cref="IPhilterConfigLoader"/> and
    /// <see cref="IPhiDeidentificationService"/>; the consumer is responsible
    /// for separately registering an <see cref="IPosTagger"/> implementation
    /// (see <c>Philter.NET.PerceptronTagger</c> for the recommended tagger, or
    /// implement <see cref="IPosTagger"/> with your tagger of choice).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>A <see cref="PhilterBuilder"/> for chaining sidecar registration.</returns>
    /// <example>
    /// <code>
    /// services.AddPhilter()
    ///         .AddSidecar(typeof(MyApp).Assembly,
    ///                     "MyApp.PhiFilters.specialty_safe.json");
    /// services.AddPerceptronTagger(); // from Philter.NET.PerceptronTagger
    /// </code>
    /// </example>
    public static PhilterBuilder AddPhilter(this IServiceCollection services)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        services.AddSingleton<IPhilterConfigLoader, PhilterConfigLoader>();
        // Factory registration so INameRecognizer is OPTIONAL: GetService returns null when no
        // recognizer is registered (the default), and the service runs without NER name detection.
        services.AddSingleton<IPhiDeidentificationService>(sp => new PhilterDeidentificationService(
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PhilterDeidentificationService>>(),
            sp.GetRequiredService<IPhilterConfigLoader>(),
            sp.GetRequiredService<IPosTagger>(),
            sp.GetService<INameRecognizer>()));
        return new PhilterBuilder(services);
    }
}

/// <summary>
/// Fluent helper returned by <see cref="ServiceCollectionExtensions.AddPhilter"/>
/// for chaining sidecar registrations.
/// </summary>
public sealed class PhilterBuilder
{
    /// <summary>The underlying service collection. Exposed so other extensions
    /// (e.g. <c>AddPerceptronTagger</c>) can register additional services.</summary>
    public IServiceCollection Services { get; }

    internal PhilterBuilder(IServiceCollection services) { Services = services; }

    /// <summary>
    /// Registers a sidecar PHI-filter configuration backed by an embedded resource
    /// in <paramref name="assembly"/>. Sidecars prepend to the base pipeline, so
    /// their filters claim spans before any upstream Philter filter can.
    /// </summary>
    public PhilterBuilder AddSidecar(Assembly assembly, string resourceName, string? label = null)
    {
        Services.AddSingleton(new PhilterSidecar(assembly, resourceName, label));
        return this;
    }

    /// <summary>
    /// Registers a sidecar PHI-filter configuration backed by an on-disk JSON file.
    /// </summary>
    public PhilterBuilder AddSidecar(string filePath, string? label = null)
    {
        Services.AddSingleton(new PhilterSidecar(filePath, label));
        return this;
    }

    /// <summary>
    /// Registers a pre-built <see cref="PhilterSidecar"/> instance — useful when
    /// the consumer needs full control over construction (e.g. wrapping a stream
    /// from a non-standard source).
    /// </summary>
    public PhilterBuilder AddSidecar(PhilterSidecar sidecar)
    {
        Services.AddSingleton(sidecar);
        return this;
    }
}
