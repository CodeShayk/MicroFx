using MicroFx.Core;
using MicroFx.Features;
using MicroFx.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MicroFx.Tests.Security;

/// <summary>
/// The security feature's startup validation is the last line of defence against shipping an
/// unintentionally open service, so each way of doing that gets its own test.
/// </summary>
[TestFixture]
internal sealed class SecurityValidationTests
{
    private static async Task<ValidationReport> ValidateAsync(
        Action<SecurityOptions> configure, string? environment = null)
    {
        var options = new SecurityOptions { Authority = "https://idp.example.com" };
        configure(options);

        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(options));

        var metadata = new ServiceMetadata
        {
            Name = "test",
            Version = "1.0.0",
            Environment = environment ?? Environments.Production,
            InstanceId = "test",
        };

        var context = new FeatureValidationContext(
            services.BuildServiceProvider(), metadata, EmptyCatalog.Instance);

        return await new SecurityFeature().ValidateAsync(context, CancellationToken.None);
    }

    [Test]
    public async Task Disabled_authentication_in_production_is_an_error()
    {
        var report = await ValidateAsync(options => options.Enabled = false);

        Assert.Multiple(() =>
        {
            Assert.That(report.HasErrors, Is.True);
            Assert.That(report.Findings[0].Message, Does.Contain("Authentication is disabled"));
        });
    }

    [Test]
    public async Task A_missing_authority_in_production_is_an_error()
    {
        var report = await ValidateAsync(options => options.Authority = null);

        Assert.Multiple(() =>
        {
            Assert.That(report.HasErrors, Is.True);
            Assert.That(report.Findings[0].Message, Does.Contain("Authority"));
        });
    }

    [Test]
    public async Task Plaintext_metadata_in_production_is_an_error()
    {
        // Fetching signing keys over plaintext lets an attacker supply their own.
        var report = await ValidateAsync(options => options.RequireHttpsMetadata = false);

        Assert.Multiple(() =>
        {
            Assert.That(report.HasErrors, Is.True);
            Assert.That(
                report.Findings.Any(f => f.Message.Contains("RequireHttpsMetadata", StringComparison.Ordinal)),
                Is.True);
        });
    }

    [Test]
    public async Task Allow_by_default_authorization_is_a_warning()
    {
        var report = await ValidateAsync(options =>
        {
            options.RequireAuthenticatedUser = false;
            options.Audiences.Add("api");
        });

        Assert.Multiple(() =>
        {
            Assert.That(report.HasErrors, Is.False);
            Assert.That(report.Findings[0].Severity, Is.EqualTo(ValidationSeverity.Warning));
        });
    }

    [Test]
    public async Task An_absent_audience_is_a_warning()
    {
        var report = await ValidateAsync(_ => { });

        Assert.That(
            report.Findings.Any(f => f.Message.Contains("audiences", StringComparison.Ordinal)),
            Is.True);
    }

    [Test]
    public async Task Generous_clock_skew_is_a_warning()
    {
        // Skew extends the usable life of an expired token, so the framework's 5-minute default
        // is deliberately not inherited.
        var report = await ValidateAsync(options =>
        {
            options.ClockSkew = TimeSpan.FromMinutes(5);
            options.Audiences.Add("api");
        });

        Assert.That(
            report.Findings.Any(f => f.Message.Contains("skew", StringComparison.OrdinalIgnoreCase)),
            Is.True);
    }

    [Test]
    public async Task Development_is_exempt_so_a_local_run_needs_no_identity_provider()
    {
        var report = await ValidateAsync(
            options => options.Enabled = false, Environments.Development);

        Assert.That(report.Findings, Is.Empty);
    }

    [Test]
    public async Task A_correctly_configured_service_produces_no_findings()
    {
        var report = await ValidateAsync(options => options.Audiences.Add("orders-api"));

        Assert.That(report.Findings, Is.Empty);
    }

    private sealed class EmptyCatalog : IFeatureCatalog
    {
        public static readonly EmptyCatalog Instance = new();

        public IReadOnlyList<FeatureCatalogEntry> All => [];

        public IReadOnlyList<FeatureCatalogEntry> Enabled => [];

        public FeatureCatalogEntry? this[string id] => null;

        public bool IsEnabled(string id) => false;

        public int Count => 0;

        public IEnumerator<FeatureCatalogEntry> GetEnumerator() =>
            Enumerable.Empty<FeatureCatalogEntry>().GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
