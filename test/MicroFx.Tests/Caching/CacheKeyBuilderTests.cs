using MicroFx.Caching;
using MicroFx.Core;
using MicroFx.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MicroFx.Tests.Caching;

[TestFixture]
internal sealed class CacheKeyBuilderTests
{
    private static ICacheKeyBuilder Build(string? tenant = null, int maxKeyLength = 512)
    {
        var services = new ServiceCollection();

        if (tenant is not null)
        {
            services.AddSingleton<ITenantContext>(new StubTenantContext(tenant));
        }

        var metadata = new ServiceMetadata
        {
            Name = "orders",
            Version = "1.0.0",
            Environment = "Production",
            InstanceId = "i1",
        };

        return new DefaultCacheKeyBuilder(
            metadata,
            services.BuildServiceProvider(),
            Options.Create(new CachingOptions { MaximumKeyLength = maxKeyLength }));
    }

    [Test]
    public void The_key_follows_the_platform_convention() =>
        Assert.That(
            Build().Build("order", "12345"),
            Is.EqualTo("orders:Production:-:order:v1:12345"));

    [Test]
    public void The_tenant_is_applied_automatically()
    {
        // Applied by the platform rather than by callers: one forgotten prefix leaks one tenant's
        // data to another through a cache hit, with no exception and no obvious symptom.
        Assert.That(
            Build(tenant: "acme").Build("order", "12345"),
            Is.EqualTo("orders:Production:acme:order:v1:12345"));
    }

    [Test]
    public void Two_tenants_never_share_a_key()
    {
        var acme = Build(tenant: "acme").Build("order", "12345");
        var globex = Build(tenant: "globex").Build("order", "12345");

        Assert.That(acme, Is.Not.EqualTo(globex));
    }

    [Test]
    public void A_separator_in_a_segment_cannot_forge_another_tenants_key()
    {
        // Without sanitization, an id of "x:order:v1:secret" under tenant "-" would produce the
        // same key as a legitimate lookup in another namespace.
        var forged = Build().Build("order", "x:orders:Production:acme:order:v1:12345");
        var legitimate = Build(tenant: "acme").Build("order", "12345");

        Assert.Multiple(() =>
        {
            Assert.That(forged, Is.Not.EqualTo(legitimate));
            Assert.That(forged, Does.Not.Contain(":acme:"));
        });
    }

    [TestCase("a b")]
    [TestCase("a/b")]
    [TestCase("a\nb")]
    [TestCase("a*b")]
    public void Unsafe_characters_are_replaced(string id) =>
        Assert.That(Build().Build("order", id), Does.EndWith("a_b"));

    [Test]
    public void An_over_long_key_is_rejected() =>
        Assert.Throws<ArgumentException>(() => Build(maxKeyLength: 64).Build("order", new string('a', 100)));

    [Test]
    public void An_explicit_version_participates_in_the_key() =>
        Assert.That(Build().Build("order", "1", "v2"), Does.Contain(":v2:"));

    [TestCase("")]
    [TestCase("  ")]
    public void Blank_segments_are_rejected(string value) =>
        Assert.Throws<ArgumentException>(() => Build().Build("order", value));

    private sealed class StubTenantContext(string tenant) : ITenantContext
    {
        public string? Current { get; } = tenant;
    }
}
