using Microsoft.Agents.AI.Tools.Shell;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class PortableToolPolicyTests
{
    [Theory]
    [InlineData("chmod +x /run/csweet/workload/tools/bin/formatter", true)]
    [InlineData("npm install --prefix /run/csweet/workload/tools esbuild@0.25.10", true)]
    [InlineData("npm ci", true)]
    [InlineData("sudo apt-get install nodejs", false)]
    [InlineData("chmod +x /run/csweet/host-file", false)]
    [InlineData("chmod +x /run/csweet/workload/tools/../../host-file", false)]
    [InlineData("chmod 777 /etc/passwd", false)]
    [InlineData("chown root /run/csweet/workload/tools/bin/formatter", false)]
    public void PortableToolSetupKeepsPrivilegedPathsDenied(string command, bool allowed)
    {
        var decision = SoftwareDeveloperHarness.CreateShellPolicy().Evaluate(new ShellRequest(command, Path.GetTempPath()));
        Assert.Equal(allowed, decision.Allowed);
    }
}
