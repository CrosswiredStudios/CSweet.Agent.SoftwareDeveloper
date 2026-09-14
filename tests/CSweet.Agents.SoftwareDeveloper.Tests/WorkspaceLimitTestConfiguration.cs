using System.Runtime.CompilerServices;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

internal static class WorkspaceLimitTestConfiguration
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Environment.SetEnvironmentVariable("CSWEET_WORKSPACE_MAXIMUM_ARCHIVE_BYTES", "8388608");
        Environment.SetEnvironmentVariable("CSWEET_WORKSPACE_MAXIMUM_EXPANDED_BYTES", "268435456");
        Environment.SetEnvironmentVariable("CSWEET_WORKSPACE_MAXIMUM_FILE_COUNT", "20000");
    }
}
