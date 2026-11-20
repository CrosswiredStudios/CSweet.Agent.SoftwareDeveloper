using System.Diagnostics;
using System.Net;

namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class BrowserDeliveryRecipeTests
{
    internal sealed class ChromiumFactAttribute : FactAttribute
    {
        internal static string? Executable => new[] { Environment.GetEnvironmentVariable("CHROMIUM_PATH"),
            Environment.GetEnvironmentVariable("CHROME_BIN"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Google", "Chrome", "Application", "chrome.exe"), "/usr/bin/google-chrome", "/usr/bin/chromium" }.FirstOrDefault(File.Exists);
        public ChromiumFactAttribute()
        {
            if (Executable is null) Skip = "Set CHROMIUM_PATH to run the real browser export regression.";
        }
    }

    [ChromiumFact]
    public async Task Animated_page_exports_its_actual_record_and_missing_record_has_a_deadline()
    {
        var root = Directory.CreateTempSubdirectory("csweet-export-test-");
        try
        {
            await BrowserDeliveryRecipe.PrepareGuidanceAsync(root.FullName, CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "index.html"), """
                <pre id="record"></pre><script>
                let frames = 0;
                requestAnimationFrame(function step() {
                    if (++frames === 12) document.getElementById('record').textContent = JSON.stringify({frames});
                    requestAnimationFrame(step);
                });
                </script>
                """);
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "test.mjs"), """
                import assert from 'node:assert/strict';
                import fs from 'node:fs/promises';
                import {syncBuiltinESMExports} from 'node:module';
                import {readBrowserRecord} from './.csweet/browser-record.mjs';
                if (process.platform === 'win32') {
                  // Reproduce Chrome's transient sharing violation once before the real startup-file read.
                  const read = fs.readFile;
                  fs.readFile = (file, ...args) => {
                    if (!file.endsWith('DevToolsActivePort')) return read(file, ...args);
                    fs.readFile = read;
                    syncBuiltinESMExports();
                    throw Object.assign(new Error('Chromium is writing the port'), {code: 'EBUSY'});
                  };
                  syncBuiltinESMExports();
                }
                // Chromium normalizes this URL. The returned page must still be our target.
                const page = new URL('./index.html', import.meta.url).href.replace('/index.html', '/./index.html');
                assert.deepEqual(await readBrowserRecord(page, 'record', 15000), {frames: 12});
                await assert.rejects(readBrowserRecord(page, 'missing', 5000), /Browser record deadline while waiting for a JSON record in #missing/);
                """);
            var start = new ProcessStartInfo("node") { WorkingDirectory = root.FullName, UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardError = true };
            start.ArgumentList.Add("test.mjs");
            start.Environment["CHROMIUM_PATH"] = ChromiumFactAttribute.Executable;
            using var process = Process.Start(start)!;
            try
            {
                var errors = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(process.ExitCode == 0, await errors);
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            }
        }
        finally { root.Delete(recursive: true); }
    }

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
