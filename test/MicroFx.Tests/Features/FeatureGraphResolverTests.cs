using MicroFx.Features;

namespace MicroFx.Tests.Features;

[TestFixture]
internal sealed class FeatureGraphResolverTests
{
    // ---- Ordering ---------------------------------------------------------------------------

    [Test]
    public void Linear_chain_resolves_in_dependency_order()
    {
        var catalog = Resolve.Graph(
        [
            TestFeature.Create("c", dependsOn: ["b"]),
            TestFeature.Create("a"),
            TestFeature.Create("b", dependsOn: ["a"]),
        ]);

        Assert.That(Resolve.Order(catalog), Is.EqualTo(new[] { "a", "b", "c" }));
    }

    [Test]
    public void Diamond_resolves_with_root_first_and_join_last()
    {
        var catalog = Resolve.Graph(
        [
            TestFeature.Create("join", dependsOn: ["left", "right"]),
            TestFeature.Create("left", dependsOn: ["root"]),
            TestFeature.Create("right", dependsOn: ["root"]),
            TestFeature.Create("root"),
        ]);

        var order = Resolve.Order(catalog);

        Assert.Multiple(() =>
        {
            Assert.That(order[0], Is.EqualTo("root"));
            Assert.That(order[3], Is.EqualTo("join"));
            Assert.That(order, Does.Contain("left"));
            Assert.That(order, Does.Contain("right"));
        });
    }

    [Test]
    public void Order_is_identical_regardless_of_registration_sequence()
    {
        // Determinism matters more than it looks: a resolved order that varies between machines
        // makes startup diagnostics unreproducible and turns an ordering bug into a heisenbug.
        string[] ids = ["alpha", "beta", "gamma", "delta", "epsilon"];
        var expected = Resolve.Order(Resolve.Graph(ids.Select(id => TestFeature.Create(id))));

        for (var seed = 0; seed < 25; seed++)
        {
            var shuffled = ids.OrderBy(_ => Random.Shared.Next()).Select(id => TestFeature.Create(id));
            Assert.That(Resolve.Order(Resolve.Graph(shuffled)), Is.EqualTo(expected),
                $"Resolution order changed on shuffle {seed}.");
        }
    }

    [Test]
    public void Ties_break_by_order_then_id()
    {
        var catalog = Resolve.Graph(
        [
            TestFeature.Create("zulu", order: 10),
            TestFeature.Create("alpha", order: 20),
            TestFeature.Create("bravo", order: 10),
        ]);

        Assert.That(Resolve.Order(catalog), Is.EqualTo(new[] { "bravo", "zulu", "alpha" }));
    }

    [Test]
    public void Before_and_after_express_soft_ordering()
    {
        var catalog = Resolve.Graph(
        [
            TestFeature.Create("logger", before: ["handler"]),
            TestFeature.Create("handler"),
            TestFeature.Create("auditor", after: ["handler"]),
        ]);

        var order = Resolve.Order(catalog);

        Assert.Multiple(() =>
        {
            Assert.That(Array.IndexOf(order, "logger"), Is.LessThan(Array.IndexOf(order, "handler")));
            Assert.That(Array.IndexOf(order, "auditor"), Is.GreaterThan(Array.IndexOf(order, "handler")));
        });
    }

    [Test]
    public void Unknown_soft_ordering_targets_are_ignored()
    {
        var catalog = Resolve.Graph(
        [
            TestFeature.Create("a", after: ["never-registered"], before: ["also-absent"]),
        ]);

        Assert.That(Resolve.Order(catalog), Is.EqualTo(new[] { "a" }));
    }

    // ---- Failure reporting ------------------------------------------------------------------

    [Test]
    public void Cycle_reports_the_full_path_not_merely_that_one_exists()
    {
        var exception = Assert.Throws<FeatureResolutionException>(() => Resolve.Graph(
        [
            TestFeature.Create("a", dependsOn: ["c"]),
            TestFeature.Create("b", dependsOn: ["a"]),
            TestFeature.Create("c", dependsOn: ["b"]),
        ]));

        var problem = exception!.Problems.Single();

        Assert.Multiple(() =>
        {
            Assert.That(problem, Does.Contain("cycle"));
            // The path is what makes the message actionable rather than merely true.
            Assert.That(problem, Does.Contain("a"));
            Assert.That(problem, Does.Contain("b"));
            Assert.That(problem, Does.Contain("c"));
            Assert.That(problem, Does.Contain("->"));
        });
    }

    [Test]
    public void Missing_hard_dependency_names_the_dependent_and_says_it_is_absent()
    {
        var exception = Assert.Throws<FeatureResolutionException>(() => Resolve.Graph(
        [
            TestFeature.Create("consumer", dependsOn: ["storage"]),
        ]));

        Assert.That(exception!.Problems.Single(),
            Does.Contain("consumer").And.Contain("storage").And.Contain("not registered"));
    }

    [Test]
    public void Disabled_hard_dependency_is_reported_differently_from_an_absent_one()
    {
        // Different causes need different fixes: register the package, versus re-enable the feature.
        var exception = Assert.Throws<FeatureResolutionException>(() => Resolve.Graph(
            [
                TestFeature.Create("consumer", dependsOn: ["storage"]),
                TestFeature.Create("storage"),
            ],
            disabledByCode: ["storage"]));

        Assert.That(exception!.Problems.Single(), Does.Contain("disabled by code"));
    }

    [Test]
    public void All_problems_are_reported_together()
    {
        var exception = Assert.Throws<FeatureResolutionException>(() => Resolve.Graph(
        [
            TestFeature.Create("a", dependsOn: ["missing-one"]),
            TestFeature.Create("b", dependsOn: ["missing-two"]),
        ]));

        Assert.That(exception!.Problems, Has.Count.EqualTo(2));
    }

    // ---- Enablement -------------------------------------------------------------------------

    [Test]
    public void Configuration_disable_wins_over_code_enable_and_records_the_key()
    {
        var catalog = Resolve.Graph(
            [TestFeature.Create("cache")],
            enabledByCode: ["cache"],
            disabledByConfiguration: new Dictionary<string, string>
            {
                ["cache"] = "MicroFx:Features:cache:Enabled",
            });

        var entry = catalog["cache"]!;

        Assert.Multiple(() =>
        {
            Assert.That(entry.IsEnabled, Is.False);
            Assert.That(entry.Reason, Is.EqualTo(DisabledReason.DisabledByConfiguration));
            // An operator must be able to see which key turned it off, not just that it is off.
            Assert.That(entry.ReasonDetail, Is.EqualTo("MicroFx:Features:cache:Enabled"));
        });
    }

    [Test]
    public void Kernel_feature_cannot_be_disabled_by_code()
    {
        var exception = Assert.Throws<FeatureResolutionException>(() => Resolve.Graph(
            [TestFeature.Create("kernel", isKernel: true)],
            disabledByCode: ["kernel"]));

        Assert.That(exception!.Problems.Single(),
            Does.Contain("kernel").And.Contain("cannot be disabled"));
    }

    [Test]
    public void Kernel_feature_cannot_be_disabled_by_configuration()
    {
        var exception = Assert.Throws<FeatureResolutionException>(() => Resolve.Graph(
            [TestFeature.Create("kernel", isKernel: true)],
            disabledByConfiguration: new Dictionary<string, string>
            {
                ["kernel"] = "MicroFx:Features:kernel:Enabled",
            }));

        Assert.That(exception!.Problems.Single(), Does.Contain("MicroFx:Features:kernel:Enabled"));
    }

    [Test]
    public void Opt_in_feature_stays_off_until_enabled()
    {
        var off = Resolve.Graph([TestFeature.Create("optional", enabledByDefault: false)]);
        var on = Resolve.Graph(
            [TestFeature.Create("optional", enabledByDefault: false)],
            enabledByCode: ["optional"]);

        Assert.Multiple(() =>
        {
            Assert.That(off["optional"]!.Reason, Is.EqualTo(DisabledReason.NotEnabledByDefault));
            Assert.That(on.IsEnabled("optional"), Is.True);
        });
    }

    [Test]
    public void Disabled_features_remain_in_the_catalog()
    {
        // "Why is this off?" must be answerable at runtime, and an absent entry cannot answer it.
        var catalog = Resolve.Graph([TestFeature.Create("cache")], disabledByCode: ["cache"]);

        Assert.Multiple(() =>
        {
            Assert.That(catalog.All, Has.Count.EqualTo(1));
            Assert.That(catalog.Enabled, Is.Empty);
            Assert.That(catalog["cache"], Is.Not.Null);
        });
    }

    // ---- Replacement ------------------------------------------------------------------------

    [Test]
    public void Replacement_inherits_the_edges_of_the_feature_it_replaces()
    {
        // The point of edge inheritance: 'downstream' ordered itself against 'cache' and knows
        // nothing about the substitution, yet must still run after whatever plays that role.
        var catalog = Resolve.Graph(
        [
            TestFeature.Create("cache"),
            TestFeature.Create("redis-cache", replaces: "cache"),
            TestFeature.Create("downstream", after: ["cache"]),
        ]);

        var order = Resolve.Order(catalog);

        Assert.Multiple(() =>
        {
            Assert.That(catalog["cache"]!.Reason, Is.EqualTo(DisabledReason.Replaced));
            Assert.That(catalog["cache"]!.ReasonDetail, Does.Contain("redis-cache"));
            Assert.That(catalog.IsEnabled("redis-cache"), Is.True);
            Assert.That(Array.IndexOf(order, "redis-cache"),
                Is.LessThan(Array.IndexOf(order, "downstream")));
        });
    }

    [Test]
    public void A_hard_dependency_on_a_replaced_feature_is_satisfied_by_the_replacement()
    {
        var catalog = Resolve.Graph(
        [
            TestFeature.Create("cache"),
            TestFeature.Create("redis-cache", replaces: "cache"),
            TestFeature.Create("consumer", after: ["cache"]),
        ]);

        Assert.That(catalog.IsEnabled("consumer"), Is.True);
    }

    [Test]
    public void Two_features_replacing_the_same_id_is_an_error_naming_both()
    {
        // Silently picking one would leave a capability nobody can account for.
        var exception = Assert.Throws<FeatureResolutionException>(() => Resolve.Graph(
        [
            TestFeature.Create("cache"),
            TestFeature.Create("redis-cache", replaces: "cache"),
            TestFeature.Create("memcached-cache", replaces: "cache"),
        ]));

        Assert.That(exception!.Problems.Single(),
            Does.Contain("redis-cache").And.Contain("memcached-cache"));
    }

    [Test]
    public void Replacement_chains_resolve_transitively()
    {
        var catalog = Resolve.Graph(
        [
            TestFeature.Create("c"),
            TestFeature.Create("b", replaces: "c"),
            TestFeature.Create("a", replaces: "b"),
            TestFeature.Create("downstream", after: ["c"]),
        ]);

        var order = Resolve.Order(catalog);

        Assert.Multiple(() =>
        {
            Assert.That(catalog.IsEnabled("a"), Is.True);
            Assert.That(catalog.IsEnabled("b"), Is.False);
            Assert.That(catalog.IsEnabled("c"), Is.False);
            Assert.That(Array.IndexOf(order, "a"), Is.LessThan(Array.IndexOf(order, "downstream")));
        });
    }

    [Test]
    public void Kernel_features_cannot_be_replaced()
    {
        var exception = Assert.Throws<FeatureResolutionException>(() => Resolve.Graph(
        [
            TestFeature.Create("kernel", isKernel: true),
            TestFeature.Create("impostor", replaces: "kernel"),
        ]));

        Assert.That(exception!.Problems.Single(), Does.Contain("cannot replace kernel feature"));
    }

    [Test]
    public void Replacing_an_absent_feature_leaves_the_replacement_standing_alone()
    {
        var catalog = Resolve.Graph([TestFeature.Create("redis-cache", replaces: "cache")]);

        Assert.That(catalog.IsEnabled("redis-cache"), Is.True);
    }

    // ---- Identity ----------------------------------------------------------------------------

    [Test]
    public void Reserved_prefix_is_refused_from_a_foreign_assembly()
    {
        // Anti-impersonation: operators trust the microfx. prefix when reading the catalog, so a
        // third-party package must not be able to present itself as a built-in.
        var exception = Assert.Throws<FeatureResolutionException>(() => Resolve.Graph(
            [TestFeature.Create("microfx.caching")],
            platformAssembly: "SomeOtherAssembly"));

        Assert.That(exception!.Problems.Single(),
            Does.Contain("reserved").And.Contain("microfx.caching"));
    }

    [Test]
    public void Reserved_prefix_is_allowed_from_the_platform_assembly()
    {
        var catalog = Resolve.Graph([TestFeature.Create("microfx.caching")]);

        Assert.That(catalog.IsEnabled("microfx.caching"), Is.True);
    }

    [Test]
    public void Later_registration_of_the_same_id_wins()
    {
        // Documented precedence: built-in, then scanned, then explicit.
        var explicitFeature = TestFeature.Create("cache", order: 500);
        var catalog = Resolve.Graph([TestFeature.Create("cache", order: 100), explicitFeature]);

        Assert.That(catalog["cache"]!.Feature, Is.SameAs(explicitFeature));
    }
}
