namespace CSweet.Agents.SoftwareDeveloper;

/// <summary>Discover the browser already provisioned in the certified development image, without downloads.</summary>
internal sealed record PreparedBrowserRuntime(string CachePath, string Executable)
{
    internal const string ImageRoot = "/usr/lib/csweet/toolchains/node-typescript";
    private static readonly Lazy<PreparedBrowserRuntime?> Prepared = new(() => Discover(ImageRoot));

    internal static void Configure(IDictionary<string, string?> environment)
    {
        if (SelectedExecutable() is not { } path) return;
        environment["CHROMIUM_PATH"] = environment["CHROME_BIN"] = path;
        if (Prepared.Value is { } image) environment["PLAYWRIGHT_BROWSERS_PATH"] = image.CachePath;
    }

    private static string? SelectedExecutable()
    {
        foreach (var key in new[] { "CHROMIUM_PATH", "CHROME_BIN" })
            if (Environment.GetEnvironmentVariable(key) is { } path && Path.IsPathFullyQualified(path) && File.Exists(path))
                return path;
        return Prepared.Value?.Executable;
    }

    internal static string Guidance => SelectedExecutable() is { } executable
        ? $"The assigned worker provides Chromium at {executable}. Use the executable exported in CHROMIUM_PATH and CHROME_BIN for browser checks."
        : "Check the assigned worker browser probe or the explicit CHROMIUM_PATH/CHROME_BIN before assuming Chromium is missing. Check approved workload tools once; if unavailable after one permitted setup attempt, retain the exact infrastructure gap. Never claim a skipped browser check passed.";

    internal static PreparedBrowserRuntime? Discover(string imageRoot)
    {
        try
        {
            var cache = Path.Combine(imageRoot, "browsers");
            var metadata = Path.Combine(imageRoot, "versions.json");
            if (!Directory.Exists(imageRoot) || !File.Exists(metadata) || !Directory.Exists(cache) ||
                Linked(imageRoot) || Linked(metadata) || Linked(cache)) return null;
            foreach (var revision in Directory.EnumerateDirectories(cache).Order(StringComparer.Ordinal).Take(32))
            {
                if (Linked(revision)) continue;
                foreach (var relative in new[] { "chrome-headless-shell-linux64/chrome-headless-shell", "chrome-linux/headless_shell",
                    "chrome-linux64/chrome", "chrome-linux/chrome" })
                {
                    var executable = Path.Combine(revision, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(executable) && !Linked(Path.GetDirectoryName(executable)!) && !Linked(executable))
                        return new(cache, executable);
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    private static bool Linked(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
