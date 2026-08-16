using System.ComponentModel.DataAnnotations;

namespace MicroFx.Messaging.RabbitMq;

/// <summary>Options for the RabbitMQ transport, bound from <c>MicroFx:Messaging:RabbitMq</c>.</summary>
public sealed class RabbitMqOptions : IValidatableObject
{
    /// <summary>
    /// Broker URI. <c>amqps</c> outside Development; plaintext AMQP is refused because credentials
    /// and every message body would cross the network in the clear.
    /// </summary>
    [Required]
    public string Uri { get; set; } = "amqp://localhost:5672/";

    /// <summary>
    /// Additional cluster endpoints. Amazon MQ returns all nodes; passing them all is what lets the
    /// client fail over rather than pinning itself to the node it first resolved.
    /// </summary>
    public IList<string> AdditionalEndpoints { get; } = [];

    /// <summary>Virtual host. One per environment and domain keeps blast radius bounded.</summary>
    public string VirtualHost { get; set; } = "/";

    /// <summary>Username. Prefer a secret store over configuration.</summary>
    public string? UserName { get; set; }

    /// <summary>Password. Prefer a secret store over configuration.</summary>
    public string? Password { get; set; }

    /// <summary>Prefix applied to every exchange and queue name.</summary>
    [Required]
    [RegularExpression("^[a-zA-Z0-9._-]{1,64}$")]
    public string NamePrefix { get; set; } = "microfx";

    /// <summary>Permits plaintext AMQP. Refused outside Development by startup validation.</summary>
    public bool AllowInsecureTransport { get; set; }

    /// <summary>Heartbeat interval. A dead connection is detected within roughly two of these.</summary>
    public TimeSpan Heartbeat { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Delay between reconnection attempts.</summary>
    public TimeSpan NetworkRecoveryInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a publish waits for the broker's confirmation.</summary>
    public TimeSpan PublishConfirmTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Consumer dispatch concurrency per channel.</summary>
    [Range(1, 64)]
    public int DispatchConcurrency { get; set; } = 4;

    /// <summary>
    /// Whether queues are declared as quorum queues.
    /// </summary>
    /// <remarks>
    /// On by default. Classic mirrored queues are deprecated and removed upstream, and a
    /// non-replicated classic queue loses every message it holds when its node dies.
    /// </remarks>
    public bool UseQuorumQueues { get; set; } = true;

    /// <summary>Replica count for quorum queues.</summary>
    [Range(1, 7)]
    public int QuorumGroupSize { get; set; } = 3;

    /// <summary>Maximum bytes a work queue may hold before publishes are rejected.</summary>
    [Range(1024 * 1024, long.MaxValue)]
    public long MaxQueueLengthBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>
    /// Broker-side redelivery cap, as a backstop against a poison-message loop the platform's own
    /// attempt counter somehow fails to bound.
    /// </summary>
    [Range(1, 1000)]
    public int DeliveryLimit { get; set; } = 20;

    /// <summary>
    /// Delay rungs for the retry ladder, in ascending order.
    /// </summary>
    /// <remarks>
    /// A requested delay is rounded <em>up</em> to the nearest rung, so a retry never fires earlier
    /// than the policy asked for. Amazon MQ forbids the delayed-message plugin, so these TTL
    /// holding queues are how the adapter implements delayed delivery at all.
    /// </remarks>
    public IList<TimeSpan> RetryLadder { get; } =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromHours(1),
    ];

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!System.Uri.TryCreate(Uri, UriKind.Absolute, out var parsed))
        {
            yield return new ValidationResult("MicroFx:Messaging:RabbitMq:Uri is not a valid URI.", [nameof(Uri)]);
            yield break;
        }

        if (!string.Equals(parsed.Scheme, "amqps", StringComparison.OrdinalIgnoreCase) &&
            !AllowInsecureTransport)
        {
            yield return new ValidationResult(
                "Plaintext AMQP is refused: credentials and every message body would cross the " +
                "network in the clear. Use amqps, or set AllowInsecureTransport for local development.",
                [nameof(Uri)]);
        }

        if (RetryLadder.Count == 0)
        {
            yield return new ValidationResult(
                "The retry ladder is empty, so no delayed retry is possible.", [nameof(RetryLadder)]);
        }

        for (var i = 1; i < RetryLadder.Count; i++)
        {
            if (RetryLadder[i] <= RetryLadder[i - 1])
            {
                yield return new ValidationResult(
                    "Retry ladder rungs must ascend; a requested delay is rounded up to the nearest " +
                    "rung and an unordered ladder would round to the wrong one.",
                    [nameof(RetryLadder)]);
                break;
            }
        }
    }
}
