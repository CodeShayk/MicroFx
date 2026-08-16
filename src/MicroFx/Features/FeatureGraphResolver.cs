namespace MicroFx.Features;

/// <summary>Thrown when the feature graph cannot be resolved. The message lists every problem found.</summary>
public sealed class FeatureResolutionException : Exception
{
    internal FeatureResolutionException(string message, IReadOnlyList<string> problems)
        : base(message) => Problems = problems;

    /// <summary>The individual problems, in the order detected.</summary>
    public IReadOnlyList<string> Problems { get; }
}

/// <summary>Inputs to graph resolution.</summary>
internal sealed class FeatureResolutionRequest
{
    public required IReadOnlyList<IMicroFxFeature> Candidates { get; init; }

    /// <summary>Ids disabled by a <c>Disable</c> call.</summary>
    public required IReadOnlySet<string> DisabledByCode { get; init; }

    /// <summary>Ids explicitly enabled by an <c>Enable</c> call, overriding EnabledByDefault=false.</summary>
    public required IReadOnlySet<string> EnabledByCode { get; init; }

    /// <summary>Ids disabled by configuration, mapped to the configuration path that did it.</summary>
    public required IReadOnlyDictionary<string, string> DisabledByConfiguration { get; init; }

    /// <summary>Assembly whose features may use the reserved <c>microfx.</c> prefix.</summary>
    public required string PlatformAssemblyName { get; init; }
}

/// <summary>
/// Turns a candidate set into a deterministic, acyclic, ordered feature graph — or fails with every
/// problem it found rather than only the first.
/// </summary>
internal static class FeatureGraphResolver
{
    public static FeatureCatalog Resolve(FeatureResolutionRequest request)
    {
        var problems = new List<string>();

        var byId = DeduplicateAndValidateIds(request, problems);
        var replacements = ApplyReplacements(byId, problems);
        var states = DetermineActivation(byId, replacements, request, problems);

        // Only order the features that survived. Ordering disabled features would let a disabled
        // feature's edges influence the order of enabled ones, which is a subtle way to make
        // "disable X" change the behaviour of unrelated code.
        var active = states.Where(s => s.Value == DisabledReason.None)
                           .Select(s => byId[s.Key])
                           .ToList();

        ValidateHardDependencies(active, states, problems);
        var ordered = TopologicalSort(active, replacements, problems);

        if (problems.Count > 0)
        {
            throw new FeatureResolutionException(
                $"MicroFx could not resolve the feature graph. {problems.Count} problem(s):{System.Environment.NewLine}" +
                string.Join(System.Environment.NewLine, problems.Select(p => "  - " + p)),
                problems);
        }

        var entries = new List<FeatureCatalogEntry>(byId.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            entries.Add(new FeatureCatalogEntry(ordered[i], i));
        }

        // Disabled features stay in the catalog. "Why is this off?" must be answerable at runtime,
        // and an absent entry cannot answer it.
        var orderedIds = ordered.Select(f => f.Descriptor.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, feature) in byId.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (orderedIds.Contains(id))
            {
                continue;
            }

            entries.Add(new FeatureCatalogEntry(feature, int.MaxValue)
            {
                Reason = states[id],
                ReasonDetail = DescribeReason(id, states[id], request, replacements),
            });
        }

        return new FeatureCatalog(entries);
    }

    private static Dictionary<string, IMicroFxFeature> DeduplicateAndValidateIds(
        FeatureResolutionRequest request, List<string> problems)
    {
        var byId = new Dictionary<string, IMicroFxFeature>(StringComparer.Ordinal);

        foreach (var feature in request.Candidates)
        {
            var descriptor = feature.Descriptor;
            var id = descriptor.Id;

            if (string.IsNullOrWhiteSpace(id))
            {
                problems.Add($"Feature '{feature.GetType().FullName}' declares an empty id.");
                continue;
            }

            // Anti-impersonation: a third-party package must not be able to present itself as a
            // built-in, because operators trust the microfx. prefix when reading the catalog.
            var assembly = feature.GetType().Assembly.GetName().Name;
            if (id.StartsWith(FeatureDescriptor.ReservedPrefix, StringComparison.Ordinal) &&
                !string.Equals(assembly, request.PlatformAssemblyName, StringComparison.Ordinal))
            {
                problems.Add(
                    $"Feature '{id}' in assembly '{assembly}' uses the reserved '{FeatureDescriptor.ReservedPrefix}' " +
                    "prefix. Prefix custom feature ids with your organisation instead.");
                continue;
            }

            // Later registration wins, matching the documented discovery precedence
            // (built-in, then scanned, then explicit).
            byId[id] = feature;
        }

        return byId;
    }

    private static Dictionary<string, string> ApplyReplacements(
        Dictionary<string, IMicroFxFeature> byId, List<string> problems)
    {
        // replacedId -> replacingId
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (id, feature) in byId.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var replaces = feature.Descriptor.Replaces;
            if (string.IsNullOrEmpty(replaces))
            {
                continue;
            }

            if (string.Equals(replaces, id, StringComparison.Ordinal))
            {
                problems.Add($"Feature '{id}' declares that it replaces itself.");
                continue;
            }

            if (!byId.TryGetValue(replaces, out var target))
            {
                // Replacing something absent is harmless — the replacement simply stands alone.
                continue;
            }

            if (target.Descriptor.IsKernel)
            {
                problems.Add(
                    $"Feature '{id}' cannot replace kernel feature '{replaces}'. Kernel features are a " +
                    "precondition for diagnosing everything else.");
                continue;
            }

            if (replacements.TryGetValue(replaces, out var existing))
            {
                problems.Add(
                    $"Features '{existing}' and '{id}' both replace '{replaces}'. Resolve the conflict " +
                    "explicitly — silently picking one would leave a capability nobody can account for.");
                continue;
            }

            replacements[replaces] = id;
        }

        return replacements;
    }

    private static Dictionary<string, DisabledReason> DetermineActivation(
        Dictionary<string, IMicroFxFeature> byId,
        Dictionary<string, string> replacements,
        FeatureResolutionRequest request,
        List<string> problems)
    {
        var states = new Dictionary<string, DisabledReason>(StringComparer.Ordinal);

        foreach (var (id, feature) in byId)
        {
            var descriptor = feature.Descriptor;

            if (replacements.ContainsKey(id))
            {
                states[id] = DisabledReason.Replaced;
                continue;
            }

            var configDisabled = request.DisabledByConfiguration.ContainsKey(id);
            var codeDisabled = request.DisabledByCode.Contains(id);

            if (descriptor.IsKernel && (configDisabled || codeDisabled))
            {
                var source = configDisabled
                    ? $"configuration ('{request.DisabledByConfiguration[id]}')"
                    : "code";
                problems.Add(
                    $"Kernel feature '{id}' cannot be disabled (attempted via {source}). Without it the " +
                    "service cannot report why anything else failed.");
                states[id] = DisabledReason.None;
                continue;
            }

            // Configuration wins over code, so an operator can kill a capability without a rebuild.
            if (configDisabled)
            {
                states[id] = DisabledReason.DisabledByConfiguration;
            }
            else if (codeDisabled)
            {
                states[id] = DisabledReason.DisabledByCode;
            }
            else if (!descriptor.EnabledByDefault && !request.EnabledByCode.Contains(id))
            {
                states[id] = DisabledReason.NotEnabledByDefault;
            }
            else
            {
                states[id] = DisabledReason.None;
            }
        }

        return states;
    }

    private static void ValidateHardDependencies(
        List<IMicroFxFeature> active,
        Dictionary<string, DisabledReason> states,
        List<string> problems)
    {
        var activeIds = active.Select(f => f.Descriptor.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var feature in active)
        {
            foreach (var dependency in feature.Descriptor.DependsOn)
            {
                if (activeIds.Contains(dependency))
                {
                    continue;
                }

                // "Absent" and "disabled" are different fixes, so they get different messages.
                    var detail = states.TryGetValue(dependency, out var reason) && reason != DisabledReason.None
                    ? $"'{dependency}' is present but {Describe(reason)}"
                    : $"'{dependency}' is not registered";

                problems.Add(
                    $"Feature '{feature.Descriptor.Id}' requires '{dependency}', but {detail}.");
            }
        }
    }

    private static List<IMicroFxFeature> TopologicalSort(
        List<IMicroFxFeature> active,
        Dictionary<string, string> replacements,
        List<string> problems)
    {
        var byId = active.ToDictionary(f => f.Descriptor.Id, StringComparer.Ordinal);

        // Ties break by Order then id, so the resolved order is identical across runs and machines.
        var ordered = active
            .OrderBy(f => f.Descriptor.Order)
            .ThenBy(f => f.Descriptor.Id, StringComparer.Ordinal)
            .ToList();

        var edges = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var feature in ordered)
        {
            edges[feature.Descriptor.Id] = new SortedSet<string>(StringComparer.Ordinal);
            inDegree[feature.Descriptor.Id] = 0;
        }

        void AddEdge(string from, string to)
        {
            // Edges to or from features that are not active are dropped: a disabled feature must not
            // influence the order of the ones that survived.
            if (!edges.TryGetValue(from, out var targets) ||
                !inDegree.TryGetValue(to, out var degree))
            {
                return;
            }

            if (targets.Add(to))
            {
                inDegree[to] = degree + 1;
            }
        }

        // A replaced feature's edges transfer to its replacement, so features that ordered
        // themselves against the original keep working without knowing a substitution happened.
        string Redirect(string id) =>
            replacements.TryGetValue(id, out var replacement) ? Redirect(replacement) : id;

        foreach (var feature in ordered)
        {
            var id = feature.Descriptor.Id;
            var d = feature.Descriptor;

            foreach (var dependency in d.DependsOn.Concat(d.After))
            {
                AddEdge(Redirect(dependency), id);
            }

            foreach (var successor in d.Before)
            {
                AddEdge(id, Redirect(successor));
            }
        }

        // Kahn's algorithm over a deterministic ready-set, so equal candidates always resolve the
        // same way rather than depending on dictionary iteration order.
        var result = new List<IMicroFxFeature>(ordered.Count);
        var ready = new List<string>(ordered.Where(f => inDegree[f.Descriptor.Id] == 0)
                                            .Select(f => f.Descriptor.Id));
        var position = ordered.Select((f, i) => (f.Descriptor.Id, i))
                              .ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);

        while (ready.Count > 0)
        {
            ready.Sort((a, b) => position[a].CompareTo(position[b]));
            var next = ready[0];
            ready.RemoveAt(0);
            result.Add(byId[next]);

            foreach (var target in edges[next])
            {
                if (--inDegree[target] == 0)
                {
                    ready.Add(target);
                }
            }
        }

        if (result.Count != ordered.Count)
        {
            var remaining = ordered.Select(f => f.Descriptor.Id)
                                   .Where(id => inDegree[id] > 0)
                                   .ToHashSet(StringComparer.Ordinal);
            var cycle = FindCycle(remaining, edges);
            problems.Add(
                "Feature dependency cycle detected: " + string.Join(" -> ", cycle) +
                ". Break the cycle by demoting one edge from DependsOn to After, or by extracting the " +
                "shared concern into a third feature.");
        }

        return result;
    }

    /// <summary>
    /// Walks the unresolved subgraph to recover an actual cycle path. Reporting "a cycle exists" is
    /// not actionable; reporting <c>a -&gt; b -&gt; c -&gt; a</c> is.
    /// </summary>
    private static List<string> FindCycle(
        HashSet<string> remaining, Dictionary<string, SortedSet<string>> edges)
    {
        var path = new List<string>();
        var onPath = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        foreach (var start in remaining.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (Walk(start))
            {
                return path;
            }
        }

        return [.. remaining.OrderBy(x => x, StringComparer.Ordinal)];

        bool Walk(string node)
        {
            if (onPath.Contains(node))
            {
                path.Add(node);                       // close the loop for a readable path
                path.RemoveRange(0, path.IndexOf(node));
                return true;
            }

            if (!visited.Add(node) || !remaining.Contains(node))
            {
                return false;
            }

            path.Add(node);
            onPath.Add(node);

            foreach (var next in edges[node].Where(remaining.Contains))
            {
                if (Walk(next))
                {
                    return true;
                }
            }

            onPath.Remove(node);
            path.RemoveAt(path.Count - 1);
            return false;
        }
    }

    private static string Describe(DisabledReason reason) => reason switch
    {
        DisabledReason.DisabledByCode => "disabled by code",
        DisabledReason.DisabledByConfiguration => "disabled by configuration",
        DisabledReason.NotEnabledByDefault => "not enabled by default and was not opted into",
        DisabledReason.Replaced => "replaced by another feature",
        _ => "enabled",
    };

    private static string? DescribeReason(
        string id,
        DisabledReason reason,
        FeatureResolutionRequest request,
        Dictionary<string, string> replacements) => reason switch
        {
            DisabledReason.DisabledByConfiguration => request.DisabledByConfiguration[id],
            DisabledReason.DisabledByCode => "Disable() call during composition",
            DisabledReason.Replaced => $"replaced by '{replacements[id]}'",
            DisabledReason.NotEnabledByDefault => "EnabledByDefault = false",
            _ => null,
        };
}
