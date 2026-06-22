using System.Reflection;

namespace Philter.NET;

/// <summary>
/// A sidecar PHI-filter configuration that prepends to the upstream Philter pipeline.
/// Sidecar filters claim spans <i>before</i> any base filter runs, so consumers can
/// rescue legitimate clinical text (e.g. <c>microalbumin/Cr ratio</c>, <c>well-appearing</c>)
/// that a permissive base filter might otherwise tokenize as PHI.
/// </summary>
/// <remarks>
/// Register one or more <see cref="PhilterSidecar"/> instances in DI as singletons:
/// <code>
/// services.AddSingleton(new PhilterSidecar(
///     typeof(MyApp).Assembly,
///     "MyApp.PhiFilters.specialty_safe.json"));
/// </code>
/// The <see cref="PhilterConfigLoader"/> resolves <see cref="IEnumerable{T}"/> of these
/// at construction time and prepends each sidecar's filters to the base pipeline in
/// registration order. The sidecar JSON must match the same schema as
/// <c>philter_delta.json</c>: an object with a <c>filters</c> array of filter records.
/// </remarks>
public sealed class PhilterSidecar
{
    /// <summary>Embedded-resource source: the assembly containing the sidecar JSON.</summary>
    public Assembly? Assembly { get; }

    /// <summary>Embedded-resource name (e.g. <c>"MyApp.PhiFilters.specialty.json"</c>).</summary>
    public string? ResourceName { get; }

    /// <summary>File-path source: absolute path to a sidecar JSON file on disk.</summary>
    public string? FilePath { get; }

    /// <summary>Optional human-readable label for diagnostics / logging.</summary>
    public string Label { get; }

    /// <summary>Create a sidecar source backed by an embedded resource.</summary>
    public PhilterSidecar(Assembly assembly, string resourceName, string? label = null)
    {
        Assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
        ResourceName = resourceName ?? throw new ArgumentNullException(nameof(resourceName));
        Label = label ?? resourceName;
    }

    /// <summary>Create a sidecar source backed by an on-disk JSON file.</summary>
    public PhilterSidecar(string filePath, string? label = null)
    {
        FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        Label = label ?? System.IO.Path.GetFileName(filePath);
    }

    internal Stream? OpenStream()
    {
        if (Assembly is not null && ResourceName is not null)
            return Assembly.GetManifestResourceStream(ResourceName);
        if (FilePath is not null)
            return System.IO.File.Exists(FilePath) ? System.IO.File.OpenRead(FilePath) : null;
        return null;
    }
}
