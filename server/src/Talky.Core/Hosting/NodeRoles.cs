namespace Talky.Core.Hosting;

/// <summary>
/// Parsing and formatting of <see cref="NodeRole"/> values as they appear on the command line
/// (<c>--role api,worker</c>) and in configuration (<c>Talky:Node:Roles</c>).
/// </summary>
public static class NodeRoles
{
    /// <summary>Canonical name of the alias that selects every role.</summary>
    public const string AllName = "all";

    private static readonly string[] NameList = ["api", "worker", "search", "push-relay"];

    private static readonly NodeRole[] SingleRoles =
    [
        NodeRole.Api,
        NodeRole.Worker,
        NodeRole.Search,
        NodeRole.PushRelay,
    ];

    private static readonly char[] Separators = [',', ';', ' ', '\t'];

    /// <summary>Canonical, kebab-cased names of the individual roles, in flag order.</summary>
    public static IReadOnlyList<string> Names => NameList;

    /// <summary>
    /// Parses a comma (or semicolon, or whitespace) separated role list such as
    /// <c>api,worker</c> or <c>all</c>.
    /// </summary>
    /// <param name="value">Raw configuration or command line value.</param>
    /// <param name="roles">Parsed roles; <see cref="NodeRole.None"/> when parsing fails.</param>
    /// <param name="error">Human readable, English error message; null on success.</param>
    /// <returns>True when <paramref name="value"/> named at least one valid role.</returns>
    public static bool TryParse(string? value, out NodeRole roles, [NotNullWhen(false)] out string? error)
    {
        roles = NodeRole.None;

        var tokens = value?.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        if (tokens.Length == 0)
        {
            error = $"No role specified. Use '{AllName}' or a comma separated list of: {string.Join(", ", NameList)}.";
            return false;
        }

        var parsed = NodeRole.None;
        foreach (var token in tokens)
        {
            if (!TryParseSingle(token, out var role))
            {
                error = $"Unknown role '{token}'. Valid roles: {string.Join(", ", NameList)}, {AllName}.";
                return false;
            }

            parsed |= role;
        }

        roles = parsed;
        error = null;
        return true;
    }

    /// <summary>Formats roles the way they are written in configuration, e.g. <c>api,worker</c>.</summary>
    public static string Format(NodeRole roles) => string.Join(",", Split(roles));

    /// <summary>Expands a role flag set into its canonical individual role names.</summary>
    public static IReadOnlyList<string> Split(NodeRole roles)
    {
        var names = new List<string>(SingleRoles.Length);
        for (var i = 0; i < SingleRoles.Length; i++)
        {
            if ((roles & SingleRoles[i]) == SingleRoles[i])
            {
                names.Add(NameList[i]);
            }
        }

        return names;
    }

    private static bool TryParseSingle(string token, out NodeRole role)
    {
        // Accept the canonical kebab-case name, the enum name and a few forgiving spellings.
        var normalized = token
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        switch (normalized)
        {
            case "api":
                role = NodeRole.Api;
                return true;
            case "worker":
                role = NodeRole.Worker;
                return true;
            case "search":
                role = NodeRole.Search;
                return true;
            case "pushrelay":
            case "push":
                role = NodeRole.PushRelay;
                return true;
            case AllName:
                role = NodeRole.All;
                return true;
            default:
                role = NodeRole.None;
                return false;
        }
    }
}
