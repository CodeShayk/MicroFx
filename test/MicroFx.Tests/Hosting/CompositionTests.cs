using MicroFx.Core;
using MicroFx.Features;
using MicroFx.Hosting;
using MicroFx.Tests.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MicroFx.Tests.Hosting;

/// <summary>Exercises the kernel through a real generic host rather than the resolver alone.</summary>
[TestFixture]
internal sealed class CompositionTests
{
    private static HostApplicationBuilder CreateBuilder(
        Dictionary<string, string?>? configuration = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
            ApplicationName = "composition-tests",
        });

        if (configuration is not null)
        {
            builder.Configuration.AddInMemoryCollection(configuration);
        }

        return builder;
    }

    [Test]
    public void A_bare_AddMicroFx_composes_the_kernel_features()
    {
        var builder = CreateBuilder();
        builder.AddMicroFx(fx => fx.DisableAssemblyScanning());

        using var host = builder.Build();
        var catalog = host.Services.GetRequiredService<IFeatureCatalog>();

        Assert.Multiple(() =>
        {
            Assert.That(catalog.IsEnabled(BuiltIn.Core), Is.True);
            Assert.That(catalog.IsEnabled(BuiltIn.Configuration), Is.True);
            Assert.That(catalog.IsEnabled(BuiltIn.Observability), Is.True);
            Assert.That(catalog.IsEnabled(BuiltIn.Health), Is.True);
        });
    }

    [Test]
    public void Kernel_features_resolve_before_anything_that_depends_on_them()
    {
        var builder = CreateBuilder();
        builder.AddMicroFx(fx => fx
            .DisableAssemblyScanning()
            .AddFeature(TestFeature.Create("test.custom", dependsOn: [BuiltIn.Core])));

        using var host = builder.Build();
        var order = host.Services.GetRequiredService<IFeatureCatalog>().Enabled
            .Select(e => e.Id).ToList();

        Assert.That(order.IndexOf(BuiltIn.Core), Is.LessThan(order.IndexOf("test.custom")));
    }

    [Test]
    public void Core_registers_a_TimeProvider_that_a_service_can_substitute()
    {
        // TryAdd throughout is what preserves this escape hatch; a stray AddSingleton in any
        // built-in feature would silently remove it for that one interface.
        var fake = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();

        var builder = CreateBuilder();
        builder.Services.AddSingleton<TimeProvider>(fake);
        builder.AddMicroFx(fx => fx.DisableAssemblyScanning());

        using var host = builder.Build();

        Assert.That(host.Services.GetRequiredService<TimeProvider>(), Is.SameAs(fake));
    }

    [Test]
    public void Service_metadata_comes_from_configuration_when_supplied()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["MicroFx:Service:Name"] = "orders",
            ["MicroFx:Service:Team"] = "commerce",
            ["MicroFx:Role"] = "consumer",
        });

        builder.AddMicroFx(fx => fx.DisableAssemblyScanning());

        using var host = builder.Build();
        var metadata = host.Services.GetRequiredService<ServiceMetadata>();

        Assert.Multiple(() =>
        {
            Assert.That(metadata.Name, Is.EqualTo("orders"));
            Assert.That(metadata.Team, Is.EqualTo("commerce"));
            Assert.That(metadata.Role, Is.EqualTo("consumer"));
        });
    }

    [Test]
    public void Disabling_a_kernel_feature_from_configuration_fails_composition()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["MicroFx:Features:microfx.observability:Enabled"] = "false",
        });

        var exception = Assert.Throws<FeatureResolutionException>(
            () => builder.AddMicroFx(fx => fx.DisableAssemblyScanning()));

        Assert.That(exception!.Problems.Single(),
            Does.Contain(BuiltIn.Observability).And.Contain("cannot be disabled"));
    }

    [Test]
    public void A_custom_feature_can_declare_options_health_and_reported_facts()
    {
        var builder = CreateBuilder();
        var feature = new RecordingFeature();
        builder.AddMicroFx(fx => fx.DisableAssemblyScanning().AddFeature(feature));

        using var host = builder.Build();
        var entry = host.Services.GetRequiredService<IFeatureCatalog>()["test.recording"]!;

        Assert.Multiple(() =>
        {
            Assert.That(feature.Configured, Is.True);
            Assert.That(entry.Facts["role"], Is.EqualTo("recorder"));
        });
    }

    [Test]
    public async Task Lifecycle_runs_forward_on_start_and_reverse_on_stop()
    {
        // Reverse-order shutdown is what makes "cancel consumers, then close the transport,
        // then flush telemetry" correct rather than coincidental.
        var log = new List<string>();

        var builder = CreateBuilder();
        builder.AddMicroFx(fx => fx
            .DisableAssemblyScanning()
            .AddFeature(new LifecycleFeature("first", log, dependsOn: [BuiltIn.Core]))
            .AddFeature(new LifecycleFeature("second", log, dependsOn: ["test.first"])));

        using var host = builder.Build();
        await host.StartAsync();
        await host.StopAsync();

        Assert.That(log, Is.EqualTo(new[]
        {
            "test.first:Starting",
            "test.second:Starting",
            "test.second:Stopping",
            "test.first:Stopping",
        }));
    }

    [Test]
    public void Aggregated_validation_reports_every_failure_in_one_startup()
    {
        // One restart should reveal every problem, not one problem per restart.
        var builder = CreateBuilder();
        builder.AddMicroFx(fx => fx
            .DisableAssemblyScanning()
            .AddFeature(new FailingValidatorFeature("test.one", "first problem"))
            .AddFeature(new FailingValidatorFeature("test.two", "second problem")));

        using var host = builder.Build();

        var exception = Assert.ThrowsAsync<FeatureValidationException>(() => host.StartAsync());

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Failures, Has.Count.EqualTo(2));
            Assert.That(exception.Message, Does.Contain("first problem").And.Contain("second problem"));
        });
    }

    [Test]
    public void Lifecycle_timings_are_recorded_per_feature()
    {
        var builder = CreateBuilder();
        builder.AddMicroFx(fx => fx
            .DisableAssemblyScanning()
            .AddFeature(new LifecycleFeature("timed", [], dependsOn: [BuiltIn.Core])));

        using var host = builder.Build();
        host.StartAsync().GetAwaiter().GetResult();

        var entry = host.Services.GetRequiredService<IFeatureCatalog>()["test.timed"]!;

        Assert.That(entry.Timings, Does.ContainKey("Starting"));

        host.StopAsync().GetAwaiter().GetResult();
    }

    // ---- Fixtures ----------------------------------------------------------------------------

    private sealed class RecordingFeature : IMicroFxFeature
    {
        public bool Configured { get; private set; }

        public FeatureDescriptor Descriptor { get; } = new()
        {
            Id = "test.recording",
            DependsOn = [BuiltIn.Core],
        };

        public void Configure(FeatureBuildContext context)
        {
            Configured = true;
            context.Report("role", "recorder");
            context.AddMeter("Test.Recording");
        }
    }

    private sealed class LifecycleFeature(string name, List<string> log, string[] dependsOn)
        : IMicroFxFeature, IFeatureLifecycle
    {
        public FeatureDescriptor Descriptor { get; } = new()
        {
            Id = $"test.{name}",
            DependsOn = dependsOn,
        };

        public void Configure(FeatureBuildContext context)
        {
        }

        public ValueTask StartingAsync(FeatureLifecycleContext context, CancellationToken cancellationToken)
        {
            log.Add($"{Descriptor.Id}:Starting");
            return ValueTask.CompletedTask;
        }

        public ValueTask StoppingAsync(FeatureLifecycleContext context, CancellationToken cancellationToken)
        {
            log.Add($"{Descriptor.Id}:Stopping");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingValidatorFeature(string id, string message)
        : IMicroFxFeature, IFeatureValidator
    {
        public FeatureDescriptor Descriptor { get; } = new()
        {
            Id = id,
            DependsOn = [BuiltIn.Core],
        };

        public void Configure(FeatureBuildContext context)
        {
        }

        public ValueTask<ValidationReport> ValidateAsync(
            FeatureValidationContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ValidationReport.Error(message));
    }
}
