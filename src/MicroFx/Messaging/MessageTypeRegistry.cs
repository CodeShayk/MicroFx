using System.Diagnostics.CodeAnalysis;

namespace MicroFx.Messaging;

/// <summary>
/// Maps between a CLR message type and the logical type name that travels on the wire.
/// </summary>
/// <remarks>
/// <para>
/// <b>This registry is a security boundary, not a convenience.</b> The obvious implementation of
/// polymorphic messaging is to put an assembly-qualified type name in a header and call
/// <c>Type.GetType</c> on receipt. That hands an attacker who can publish to the broker the ability
/// to instantiate arbitrary types in the consumer — a well-known remote-code-execution pattern.
/// </para>
/// <para>
/// Here, wire names resolve only to types the service <em>registered at composition time</em>. An
/// unknown name is a dead-letter, never a load. The registry is immutable once composition finishes.
/// </para>
/// </remarks>
public sealed class MessageTypeRegistry
{
    private readonly Dictionary<string, Type> _byName;
    private readonly Dictionary<Type, string> _byType;

    internal MessageTypeRegistry(IReadOnlyDictionary<string, Type> registrations)
    {
        _byName = new Dictionary<string, Type>(registrations, StringComparer.Ordinal);
        _byType = registrations.ToDictionary(pair => pair.Value, pair => pair.Key);
    }

    /// <summary>Registered wire names.</summary>
    public IReadOnlyCollection<string> Names => _byName.Keys;

    /// <summary>Number of registered types.</summary>
    public int Count => _byName.Count;

    /// <summary>
    /// Resolves a wire name to its registered CLR type. Returns false for anything not registered.
    /// </summary>
    public bool TryResolve(string wireName, [NotNullWhen(true)] out Type? type)
    {
        type = null;
        return !string.IsNullOrEmpty(wireName) && _byName.TryGetValue(wireName, out type);
    }

    /// <summary>Returns the wire name for a CLR type, or null when it is not registered.</summary>
    public string? GetWireName(Type type) => _byType.GetValueOrDefault(type);

    /// <summary>Returns the wire name for a CLR type, or throws when it is not registered.</summary>
    public string RequireWireName(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return _byType.TryGetValue(type, out var name)
            ? name
            : throw new InvalidOperationException(
                $"Message type '{type.FullName}' is not registered. Declare it during composition " +
                "with PublishesEvent, HandlesCommand, or SubscribesToEvent — the registry is what " +
                "prevents an inbound type name from resolving to an arbitrary type.");
    }
}

/// <summary>Collects type registrations during composition.</summary>
internal sealed class MessageTypeRegistryBuilder
{
    private readonly Dictionary<string, Type> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, string> _byType = [];

    /// <summary>
    /// Registers a message type under a wire name, deriving a conventional name when none is given.
    /// </summary>
    public string Register(Type type, string? wireName = null)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (_byType.TryGetValue(type, out var existing))
        {
            return existing;
        }

        var name = wireName ?? Conventional(type);

        if (_byName.TryGetValue(name, out var claimed) && claimed != type)
        {
            // Two types sharing a wire name means one silently deserializes as the other.
            throw new InvalidOperationException(
                $"Message wire name '{name}' is claimed by both '{claimed.FullName}' and " +
                $"'{type.FullName}'. Give one an explicit name.");
        }

        _byName[name] = type;
        _byType[type] = name;
        return name;
    }

    public MessageTypeRegistry Build() => new(_byName);

    /// <summary>
    /// Derives a stable wire name from the CLR type: <c>namespace-tail.type-name</c>, lowercased.
    /// </summary>
    /// <remarks>
    /// Deliberately not the assembly-qualified name. The wire name is a published contract, so it
    /// must survive an assembly rename or a namespace move — and must carry nothing a consumer could
    /// use to locate a type by reflection.
    /// </remarks>
    private static string Conventional(Type type)
    {
        var name = type.Name;

        // Trim a trailing version suffix so OrderPlacedV1 becomes order-placed.v1 rather than
        // order-placed-v-1, keeping the version legible in the wire name.
        var version = string.Empty;
        var versionIndex = name.LastIndexOf('V');
        if (versionIndex > 0 && versionIndex < name.Length - 1 &&
            name[(versionIndex + 1)..].All(char.IsAsciiDigit))
        {
            version = "." + name[versionIndex..].ToLowerInvariant();
            name = name[..versionIndex];
        }

        var segment = type.Namespace?.Split('.').LastOrDefault() ?? "message";

        return $"{Kebab(segment)}.{Kebab(name)}{version}";
    }

    private static string Kebab(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 8);

        for (var i = 0; i < value.Length; i++)
        {
            var character = value[i];

            if (char.IsAsciiLetterOrDigit(character))
            {
                if (char.IsAsciiLetterUpper(character) && i > 0)
                {
                    builder.Append('-');
                }

                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }
}
