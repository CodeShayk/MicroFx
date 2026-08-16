using System.Security.Claims;
using MicroFx.MultiTenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace MicroFx.Tests.MultiTenancy;

[TestFixture]
internal sealed class TenantResolverTests
{
    private static ITenantResolver Resolver(Action<MultiTenancyOptions>? configure = null)
    {
        var options = new MultiTenancyOptions();
        configure?.Invoke(options);
        return new DefaultTenantResolver(Options.Create(options));
    }

    private static HttpContext WithClaim(string? value)
    {
        var context = new DefaultHttpContext();

        if (value is not null)
        {
            context.User = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim("tenant_id", value)], "test"));
        }

        return context;
    }

    [Test]
    public void A_tenant_claim_resolves() =>
        Assert.That(Resolver().Resolve(WithClaim("acme")), Is.EqualTo("acme"));

    [Test]
    public void An_absent_claim_resolves_to_null() =>
        Assert.That(Resolver().Resolve(WithClaim(null)), Is.Null);

    // The tenant flows into cache keys, log scopes, storage prefixes, and query filters, so an
    // unconstrained value is a key-injection vector at every one of those sites.
    [TestCase("acme:evil")]
    [TestCase("acme/evil")]
    [TestCase("acme evil")]
    [TestCase("../acme")]
    [TestCase("acme\nevil")]
    [TestCase("acme*")]
    [TestCase("'; DROP TABLE--")]
    public void A_tenant_containing_a_separator_or_control_character_is_refused(string value) =>
        Assert.That(Resolver().Resolve(WithClaim(value)), Is.Null);

    [TestCase("acme")]
    [TestCase("acme-corp")]
    [TestCase("acme_corp")]
    [TestCase("ACME123")]
    public void Ordinary_identifiers_are_accepted(string value) =>
        Assert.That(Resolver().Resolve(WithClaim(value)), Is.EqualTo(value));

    [Test]
    public void An_over_long_tenant_is_refused() =>
        Assert.That(Resolver().Resolve(WithClaim(new string('a', 65))), Is.Null);

    [Test]
    public void Header_source_reads_the_configured_header()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tenant-Id"] = "acme";

        var resolver = Resolver(options => options.Source = TenantSource.Header);

        Assert.That(resolver.Resolve(context), Is.EqualTo("acme"));
    }

    [Test]
    public void Header_source_applies_the_same_sanitization_as_claims()
    {
        // A header is caller-supplied and trivially forged, so it gets no more latitude than a
        // claim does — only less trust in how it is configured.
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tenant-Id"] = "acme:evil";

        var resolver = Resolver(options => options.Source = TenantSource.Header);

        Assert.That(resolver.Resolve(context), Is.Null);
    }

    [Test]
    public void The_default_source_is_the_claim_because_a_header_can_be_forged() =>
        Assert.That(new MultiTenancyOptions().Source, Is.EqualTo(TenantSource.Claim));

    [Test]
    public void A_tenant_is_required_by_default() =>
        // An unscoped query in a multi-tenant store returns everyone's data.
        Assert.That(new MultiTenancyOptions().RequireTenant, Is.True);

    [Test]
    public void Route_segment_source_reads_the_first_segment()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/acme/v1/orders";

        var resolver = Resolver(options => options.Source = TenantSource.RouteSegment);

        Assert.That(resolver.Resolve(context), Is.EqualTo("acme"));
    }
}
