using MicroFx.Features;
using MicroFx.Messaging.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

[assembly: MicroFxFeatureAssembly]
[assembly: MicroFxFeature(typeof(MicroFx.Messaging.RabbitMq.RabbitMqTransportFeature))]

namespace MicroFx.Messaging.RabbitMq;

/// <summary>
/// Registers RabbitMQ as the message transport.
/// </summary>
/// <remarks>
/// Discovered by the assembly attributes above, so adding the package reference is the whole
/// installation — there is no registration call to remember and no order to get wrong.
/// </remarks>
public sealed class RabbitMqTransportFeature : IMicroFxFeature, IFeatureValidator
{
    /// <summary>Feature id.</summary>
    public const string FeatureId = "microfx.messaging.rabbitmq";

    /// <inheritdoc />
    public FeatureDescriptor Descriptor { get; } = new()
    {
        Id = FeatureId,
        DisplayName = "RabbitMQ transport",

        // Ordered before messaging so the transport is registered by the time the messaging
        // feature looks for one, and its in-memory default never wins.
        Before = [BuiltIn.Messaging],
        DependsOn = [BuiltIn.Core],
        SupportedHosts = HostKinds.Any,
        ConfigurationSection = "MicroFx:Messaging:RabbitMq",
    };

    /// <inheritdoc />
    public void Configure(FeatureBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.AddValidatedOptions<RabbitMqOptions>();

        context.Services.TryAddSingleton<RabbitMqConnectionProvider>();

        // TryAdd, so a service that registered its own transport — a fake in a test, or a second
        // adapter during a migration — keeps it.
        context.Services.TryAddSingleton<IMessageTransport>(provider => new RabbitMqTransport(
            provider.GetRequiredService<RabbitMqConnectionProvider>(),
            provider.GetRequiredService<IOptions<RabbitMqOptions>>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILoggerFactory>()));

        // Readiness, never liveness. A broker outage must stop traffic being routed here; it must
        // not restart every replica, which would turn one outage into an outage plus a restart
        // storm — and the restarts would not help, because the broker is still down.
        context.AddHealthContribution(HealthContribution.Ready(
            "rabbitmq",
            (provider, _) =>
            {
                var connections = provider.GetRequiredService<RabbitMqConnectionProvider>();

                return ValueTask.FromResult(connections.IsConnected
                    ? HealthCheckResult.Healthy("The broker connection is open.")
                    // Deliberately generic: a broker exception carries the URI and often the user.
                    : HealthCheckResult.Unhealthy("The broker connection is not open."));
            },
            TimeSpan.FromSeconds(2)));

        var options = new RabbitMqOptions();
        context.Configuration.GetSection("MicroFx:Messaging:RabbitMq").Bind(options);

        context.Report("host", SafeHost(options.Uri));
        context.Report("vhost", options.VirtualHost);
        context.Report("queues", options.UseQuorumQueues ? "quorum" : "classic");
    }

    /// <inheritdoc />
    public ValueTask<ValidationReport> ValidateAsync(
        FeatureValidationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = context.Services.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
        var findings = new List<ValidationFinding>();

        if (context.Metadata.IsDevelopment)
        {
            return ValueTask.FromResult(ValidationReport.Ok());
        }

        if (options.AllowInsecureTransport)
        {
            findings.Add(new ValidationFinding(
                ValidationSeverity.Error,
                "AllowInsecureTransport is set outside Development. Credentials and every message " +
                "body would cross the network in the clear."));
        }

        if (!options.UseQuorumQueues)
        {
            findings.Add(new ValidationFinding(
                ValidationSeverity.Warning,
                "Quorum queues are disabled. A non-replicated classic queue loses every message it " +
                "holds when its node dies, and classic mirrored queues are removed upstream."));
        }

        if (options.UseQuorumQueues && options.QuorumGroupSize < 3)
        {
            findings.Add(new ValidationFinding(
                ValidationSeverity.Warning,
                $"A quorum group of {options.QuorumGroupSize} cannot tolerate a node failure; " +
                "three is the smallest useful size."));
        }

        // Credentials in configuration are readable by anything that can read the config endpoint,
        // a crash dump, or the deployment manifest.
        if (!string.IsNullOrEmpty(options.Password))
        {
            findings.Add(new ValidationFinding(
                ValidationSeverity.Warning,
                "The broker password is supplied through configuration. Prefer a secret store so it " +
                "can be rotated without a redeploy and does not appear in deployment manifests."));
        }

        return ValueTask.FromResult(
            findings.Count == 0 ? ValidationReport.Ok() : ValidationReport.FromFindings(findings));
    }

    /// <summary>Extracts the host for diagnostics, discarding any embedded credentials.</summary>
    private static string SafeHost(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            ? $"{parsed.Host}:{parsed.Port}"
            : "unparsable";
}
