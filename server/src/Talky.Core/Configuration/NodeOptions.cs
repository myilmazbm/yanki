using System.ComponentModel.DataAnnotations;

namespace Talky.Core.Configuration;

/// <summary>
/// <c>Talky:Node</c> — identity and roles of this talky.exe process.
/// </summary>
public sealed class NodeOptions
{
    /// <summary>
    /// Pinned node id. When null or empty the host generates
    /// <c>{MachineName}-{4 random base32 characters}</c> at startup.
    /// </summary>
    [RegularExpression(
        "^[A-Za-z0-9._-]{1,64}$",
        ErrorMessage = "Talky:Node:Id must be 1-64 characters of letters, digits, '.', '_' or '-'.")]
    public string? Id { get; set; }

    /// <summary>
    /// Comma separated roles, e.g. <c>all</c> or <c>api,worker</c>.
    /// Valid values: <c>api</c>, <c>worker</c>, <c>search</c>, <c>push-relay</c>, <c>all</c>.
    /// </summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "Talky:Node:Roles must not be empty.")]
    public string Roles { get; set; } = "all";
}
