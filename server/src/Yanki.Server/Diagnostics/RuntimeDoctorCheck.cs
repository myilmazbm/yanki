using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using Talky.Core.Diagnostics;
using Talky.Core.Hosting;

namespace Talky.Server.Diagnostics;

/// <summary>
/// Reports the .NET runtime talky.exe is running on, including whether server garbage collection is
/// enabled — workstation GC on an API node shows up as latency long before it shows up as an error.
/// </summary>
internal sealed class RuntimeDoctorCheck(INodeInfo node) : IDoctorCheck
{
    /// <inheritdoc />
    public string Name => "runtime";

    /// <inheritdoc />
    public Task<DoctorResult> RunAsync(CancellationToken cancellationToken)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["talkyVersion"] = node.Version,
            ["framework"] = RuntimeInformation.FrameworkDescription,
            ["runtimeIdentifier"] = RuntimeInformation.RuntimeIdentifier,
            ["serverGc"] = GCSettings.IsServerGC ? "yes" : "no",
            ["is64Bit"] = Environment.Is64BitProcess ? "yes" : "no",
            ["nodeId"] = node.NodeId,
            ["roles"] = NodeRoles.Format(node.Roles),
        };

        details["runtimeVersion"] = Environment.Version.ToString();

        if (!GCSettings.IsServerGC)
        {
            return Task.FromResult(DoctorResult.Warning(
                "Workstation garbage collection is active; set ServerGarbageCollection for production nodes.",
                details));
        }

        if (!Environment.Is64BitProcess)
        {
            return Task.FromResult(DoctorResult.Warning("Running as a 32-bit process.", details));
        }

        return Task.FromResult(DoctorResult.Ok(
            string.Create(CultureInfo.InvariantCulture, $"talky {node.Version} on {RuntimeInformation.FrameworkDescription}."),
            details));
    }
}
