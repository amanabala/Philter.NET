using Philter.NET;

namespace Philter.NET.Clinical;

/// <summary>
/// DI extension methods for registering the Philter.NET.Clinical sidecar.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the production-tested clinical-text safe patterns shipped in
    /// this package as a <see cref="PhilterSidecar"/>. Prepends to the
    /// upstream pipeline so the patterns claim clinical spans (BP readings,
    /// lab values, dose patterns, drug names, abbreviations, pain scales,
    /// etc.) before any base PHI filter can mis-tokenize them.
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddPhilter()
    ///         .AddClinicalSidecar()
    ///         .AddCatalystPosTagger();
    /// </code>
    /// </example>
    public static PhilterBuilder AddClinicalSidecar(this PhilterBuilder builder)
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));
        return builder.AddSidecar(
            typeof(ServiceCollectionExtensions).Assembly,
            "Philter.NET.Clinical.Configuration.philter_clinical_safe.json",
            label: "Philter.NET.Clinical");
    }
}
