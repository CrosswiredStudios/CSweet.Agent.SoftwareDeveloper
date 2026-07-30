using CSweet.Agent.SDK;
using CSweet.Agents.SoftwareDeveloper;
using Microsoft.Extensions.Hosting;

if (args.Contains("--self-test", StringComparer.Ordinal))
{
    var manifest = await AgentManifestLoader.LoadAsync("csweet-plugin.json", CancellationToken.None);
    var agent = new SoftwareDeveloperAgent();
    var schema = await new AgentTestRuntime().ExecuteCapabilityAsync(
        agent,
        AgentConfigurationCapabilities.Describe,
        new { });

    var succeeded =
        manifest.Id == agent.AgentId &&
        manifest.Version == agent.Version &&
        manifest.Capabilities.Contains(SoftwareDeveloperProfile.PrimaryCapability) &&
        schema.Succeeded;

    Console.WriteLine(succeeded
        ? "Software Developer manifest and configuration contract are valid."
        : "Software Developer self-test failed.");
    Environment.ExitCode = succeeded ? 0 : 1;
    return;
}

var builder = Host.CreateApplicationBuilder(args);

var runtimeManifest = await AgentManifestLoader.LoadAsync(
    "csweet-plugin.json",
    CancellationToken.None);
if (runtimeManifest.Id != SoftwareDeveloperProfile.AgentId ||
    runtimeManifest.Version != SoftwareDeveloperProfile.Version)
{
    throw new InvalidOperationException(
        "The Software Developer implementation identity does not match csweet-plugin.json.");
}

builder.AddCSweetAgent<SoftwareDeveloperAgent>();
await builder.Build().RunAsync();
