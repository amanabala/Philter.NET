using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Philter.NET;
using Philter.NET.PerceptronTagger;
using Xunit;

namespace Philter.NET.Tests;

/// <summary>
/// End-to-end DI integration tests. These don't replace deep unit tests for
/// the filter engine (those should also exist; tracked separately) — they
/// verify the public package surface works the way the README says it does.
/// </summary>
public class AddPhilterIntegrationTests
{
    private static IServiceCollection BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        return services;
    }

    [Fact]
    public void AddPhilter_registers_required_singletons()
    {
        var services = BuildServices();

        services.AddPhilter().AddPerceptronTagger();

        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetRequiredService<IPhilterConfigLoader>());
        Assert.NotNull(sp.GetRequiredService<IPhiDeidentificationService>());
        Assert.NotNull(sp.GetRequiredService<IPosTagger>());
    }

    [Fact]
    public void IPhiDeidentificationService_is_a_singleton()
    {
        var services = BuildServices();
        services.AddPhilter().AddPerceptronTagger();

        using var sp = services.BuildServiceProvider();
        var a = sp.GetRequiredService<IPhiDeidentificationService>();
        var b = sp.GetRequiredService<IPhiDeidentificationService>();
        Assert.Same(a, b);
    }

    [Fact]
    public async Task DeidentifyAsync_redacts_a_well_known_name()
    {
        var services = BuildServices();
        services.AddPhilter().AddPerceptronTagger();

        using var sp = services.BuildServiceProvider();
        var deid = sp.GetRequiredService<IPhiDeidentificationService>();

        var result = await deid.DeidentifyAsync(
            "Patient John Smith was seen on 03/12/2026 for follow-up.");

        Assert.True(result.PhiDetected,
            "Expected at least one PHI replacement on a note with an obvious name + date.");
        Assert.True(result.Stats.TotalReplacements >= 1);
        Assert.DoesNotContain("John Smith", result.DeidentifiedNote);
    }

    [Fact]
    public async Task AddSidecar_via_embedded_resource_rescues_claimed_spans()
    {
        // Sidecar semantics: prepended filters CLAIM the matched span and remove
        // it from further consideration, so a downstream PHI filter can't claim
        // it as a name/date/etc. That's the entire point — sidecars are for
        // RESCUING legitimate clinical text from over-redaction, not for adding
        // new redactions.
        //
        // Test input contains:
        //   - "John Smith"  — a real PHI name that should redact regardless
        //   - "WIDGET-042"  — a token the test sidecar claims; should survive
        //                     with sidecar, may or may not survive without
        const string input = "John Smith ordered WIDGET-042 on file.";

        // Baseline: no sidecar — WIDGET-042 is unprotected.
        var baselineSp = BuildServices()
            .AddPhilter()
            .AddPerceptronTagger()
            .Services
            .BuildServiceProvider();
        var baselineDeid = baselineSp.GetRequiredService<IPhiDeidentificationService>();
        var baseline = await baselineDeid.DeidentifyAsync(input);

        // With sidecar: WIDGET-042 is claimed (rescued) by the test sidecar.
        var services = BuildServices();
        services.AddPhilter()
                .AddSidecar(
                    typeof(AddPhilterIntegrationTests).Assembly,
                    "Philter.NET.Tests.Resources.test_sidecar.json")
                .AddPerceptronTagger();

        using var sp = services.BuildServiceProvider();
        var deid = sp.GetRequiredService<IPhiDeidentificationService>();
        var withSidecar = await deid.DeidentifyAsync(input);

        // Hard assertion: the sidecar-claimed token survives in the output.
        Assert.Contains("WIDGET-042", withSidecar.DeidentifiedNote);
        // Sanity: the real PHI name still got redacted.
        Assert.DoesNotContain("John Smith", withSidecar.DeidentifiedNote);
        // The total replacement count should be the same or lower with the sidecar
        // (since at most one token was rescued; the name still redacts).
        Assert.True(withSidecar.Stats.TotalReplacements <= baseline.Stats.TotalReplacements,
            $"Sidecar should claim spans, not add new redactions " +
            $"(baseline={baseline.Stats.TotalReplacements}, with sidecar={withSidecar.Stats.TotalReplacements}).");
    }

    [Fact]
    public void Sidecar_with_missing_resource_is_ignored_with_warning()
    {
        // Confirm fail-soft behavior — a misconfigured sidecar must not bring
        // down the de-id pipeline.
        var services = BuildServices();
        services.AddPhilter()
                .AddSidecar(
                    typeof(AddPhilterIntegrationTests).Assembly,
                    "Philter.NET.Tests.Resources.does_not_exist.json")
                .AddPerceptronTagger();

        using var sp = services.BuildServiceProvider();
        var deid = sp.GetRequiredService<IPhiDeidentificationService>();

        // Resolving the service triggers PhilterConfigLoader's constructor, which
        // tries (and fails) to open the missing sidecar — but the load must complete.
        Assert.NotNull(deid);
    }
}
