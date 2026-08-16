namespace MicroFx.Features;

/// <summary>The kind of host being composed. Features declare which kinds their facets apply to.</summary>
[Flags]
public enum HostKinds
{
    /// <summary>No host kind.</summary>
    None = 0,

    /// <summary>An HTTP host with a request pipeline and endpoints.</summary>
    Web = 1,

    /// <summary>A generic host: worker, consumer, or scheduled process. No HTTP pipeline.</summary>
    Worker = 2,

    /// <summary>A function or serverless host with an externally managed lifecycle.</summary>
    Serverless = 4,

    /// <summary>Applies to every host kind.</summary>
    Any = Web | Worker | Serverless,
}
