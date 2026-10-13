using CSweet.Agents.SoftwareDeveloper;
namespace CSweet.Agents.SoftwareDeveloper.Tests;
public sealed class DependencyFailureTests
{
    [Theory]
    [InlineData(127, "node: command not found", "missing-runtime")]
    [InlineData(6, "curl: could not resolve host: registry.npmjs.org", "network-dns-failed")]
    [InlineData(1, "network_transport_failed", "network-transport-failed")]
    [InlineData(1, "internet_permission_denied", "internet-permission-denied")]
    [InlineData(1, "network_destination_denied", "destination-policy-denied")]
    [InlineData(1, "npm ERR! code ETARGET", "package-unavailable")]
    [InlineData(1, "Expected score to increase", "application-validation-failed")]
    public void InfrastructureFailuresRemainDistinctFromApplicationDefects(int exitCode, string diagnostic, string kind)
    {
        Assert.Equal(kind, DevelopmentDiagnostics.FailureKind(new("npm test", false, exitCode, diagnostic)));
    }

    [Theory]
    [InlineData("npm ERR! code ENOTFOUND", "public-internet", "network-dns-failed")]
    [InlineData("npm ERR! getaddrinfo EAI_AGAIN", "public-internet", "network-dns-failed")]
    [InlineData("npm ERR! code ETIMEDOUT", "public-internet", "network-transport-failed")]
    [InlineData("npm ERR! code ENETUNREACH", "restricted", "internet-permission-denied")]
    [InlineData("curl: could not resolve host", "restricted", "internet-permission-denied")]
    [InlineData("No matching distribution found for unavailable-package", "public-internet", "package-unavailable")]
    public void PackageManagerDiagnosticsUseTheEffectiveNetworkPolicy(string detail, string mode, string kind)
    {
        Assert.Equal(kind, DevelopmentDiagnostics.FailureKind(new("npm ci", false, 1, detail), mode));
    }
}
