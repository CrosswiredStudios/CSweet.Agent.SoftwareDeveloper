using System.Diagnostics;
using System.Net;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class BrowserDeliveryRecipeTests
{
    [Fact]
    public async Task Standard_server_serves_root_assets_and_safe_error_paths_then_shuts_down()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-browser-recipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "dist"));
        Process? process = null;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "dist", "index.html"), "<canvas id=game></canvas>");
            await File.WriteAllTextAsync(Path.Combine(root, "dist", "app.js"), "export const scene='ready';");
            await File.WriteAllTextAsync(Path.Combine(root, "serve.mjs"), BrowserDeliveryRecipe.Server + "\nserver.on('listening',()=>console.log(server.address().port));");
            var start = new ProcessStartInfo("node") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("serve.mjs"); start.Environment["PORT"] = "0";
            process = Process.Start(start)!;
            var port = int.Parse((await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!);
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(5) };
            Assert.Contains("canvas", await client.GetStringAsync("/"));
            Assert.Contains("ready", await client.GetStringAsync("/app.js"));
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/missing.js")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/%E0%A4")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/%2e%2e%2fserve.mjs")).StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync("/", null)).StatusCode);
            var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/"));
            Assert.Equal(HttpStatusCode.OK, head.StatusCode); Assert.Empty(await head.Content.ReadAsByteArrayAsync());
            Assert.Contains("COPY --from=build /app/dist ./dist", BrowserDeliveryRecipe.Dockerfile);
        }
        finally
        {
            if (process is { HasExited: false }) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            process?.Dispose(); Directory.Delete(root, true);
        }
    }
}
