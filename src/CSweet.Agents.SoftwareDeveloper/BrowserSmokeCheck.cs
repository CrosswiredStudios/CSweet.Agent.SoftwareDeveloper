using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agents.SoftwareDeveloper;

/// <summary>One reusable real-browser check, using the image's Chromium and Node's built-in CDP client.</summary>
internal static class BrowserSmokeCheck
{
    internal const string Guidance = "For browser-visible work, build the smallest working page and call check_browser_build early, before writing extra validators. " +
        "Supply its workspace-relative static build directory and requireCanvas=true for a game canvas. The tool launches real Chromium, " +
        "records page exceptions and failed assets, and retains a screenshot under test-results/browser-smoke. " +
        "Do not build a PNG decoder or another browser-test framework. A smoke pass proves loading and an observable surface, " +
        "not gameplay, visual quality or independent QA. Inspect retained screenshots where image tools are available; otherwise report visual inspection unverified. " +
        "Keep test-results/ ignored by source control; checkpoints retain it. Reuse the project's build tool or a minimal static-copy recipe; " +
        "do not write a module-graph packager for a placeholder.";

    /// <summary>
    /// Whether this ticket needs a bound browser receipt. A planning declaration is authoritative, so the engineer,
    /// the technical reviewer and QA apply one rule; the text heuristics only cover tickets planned before it existed.
    /// </summary>
    internal static bool Required(WorkItem item) =>
        WorkItemVerifications.IsDeclared(item.Planning)
            ? WorkItemVerifications.BrowserSmoke(item.Planning) is not null
            : item.Development is { } development && Required(development.AcceptanceCriteria,
                string.Join("\n", new[] { item.Description ?? "" }.Concat(development.Constraints ?? [])));

    internal static bool CanvasRequired(WorkItem item) =>
        WorkItemVerifications.IsDeclared(item.Planning)
            ? WorkItemVerifications.BrowserSmoke(item.Planning)?.RequireCanvas == true
            : Required(item) && System.Text.RegularExpressions.Regex.IsMatch(string.Join("\n", item.Development!.AcceptanceCriteria),
                @"\b(Phaser|canvas)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Tells the model up front what this ticket's declaration requires. Undeclared tickets get no note.</summary>
    internal static string AssignmentNote(WorkItem item)
    {
        if (!WorkItemVerifications.IsDeclared(item.Planning)) return "";
        if (WorkItemVerifications.BrowserSmoke(item.Planning) is not { } browser)
            return "This ticket declares no browser verification. A browser check is not required for publication or review.";
        var canvas = browser.RequireCanvas;
        return $"This ticket declares browser verification{(canvas ? " with a visible canvas" : "")}. After your last source edit, build the static output " +
            $"and call check_browser_build{(canvas ? " with requireCanvas=true" : "")}. Its receipt (`{WorkItemVerificationKinds.BrowserSmokeReceiptCommand} <build directory>{(canvas ? " --canvas" : "")}`) is added to your " +
            "validations automatically; the technical reviewer and QA check that receipt. Do not record the tool call as a validation, commit screenshots, " +
            "or write extra verifiers for it.";
    }

    internal static bool Required(IEnumerable<string> criteria, string context)
    {
        var text = string.Join("\n", criteria);
        return System.Text.RegularExpressions.Regex.IsMatch(text + "\n" + context, @"\b(browser|Chromium|Phaser|HTML|web)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
            System.Text.RegularExpressions.Regex.IsMatch(text, @"\b(render\w*|display\w*|visible|canvas|loads?|opens?)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    internal static async Task PrepareAsync(string root, CancellationToken token)
    {
        Directory.CreateDirectory(Path.Combine(root, ".csweet"));
        await File.WriteAllTextAsync(Path.Combine(root, ".csweet", "browser-smoke.cjs"), Script, new System.Text.UTF8Encoding(false), token);
    }

    internal static string Command(string directory, bool requireCanvas)
    {
        // Keep tool-selected paths out of shell syntax and inside the assignment.
        if (string.IsNullOrWhiteSpace(directory) || directory.Length > 160 || Path.IsPathRooted(directory) ||
            directory.Replace('\\', '/').Split('/').Any(x => x is ".." or "" || x.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))))
            throw new ArgumentException("Supply a workspace-relative build directory such as dist.");
        return $"{WorkItemVerificationKinds.BrowserSmokeReceiptCommand} {directory.Replace('\\', '/')}" + (requireCanvas ? " --canvas" : "");
    }

    internal sealed class Session(string root, WorkspaceProgress progress, BoundedWorkspaceShell shell,
        Func<CancellationToken, Task> checkpoint, bool canvasRequired = false)
    {
        private string? successfulSource;
        private long successfulInputs;
        private string? successfulCommand;
        internal bool Passed => successfulSource is not null && successfulInputs == progress.Validations.InputRevision &&
            successfulSource == WorkspaceValidation.SourceFingerprint(root);
        internal AIFunction Tool() => AIFunctionFactory.Create(async (string buildDirectory, bool requireCanvas, CancellationToken token) =>
        {
            successfulSource = null;
            await PrepareAsync(root, token); // Restore trusted helper if project content changed it.
            var source = WorkspaceValidation.SourceFingerprint(root);
            var result = await progress.RunShellCoreAsync(shell, Command(buildDirectory, requireCanvas || canvasRequired), token);
            if (result.Succeeded && source == WorkspaceValidation.SourceFingerprint(root))
            { successfulSource = source; successfulInputs = progress.Validations.InputRevision; successfulCommand = Command(buildDirectory, requireCanvas || canvasRequired); }
            await WorkspaceCheckpoint.SaveAsync(checkpoint, token);
            return result.Output;
        }, "check_browser_build", "Launch the static build in real prepared Chromium; record JS/asset failures, visible surface, and screenshot. No downloads or custom test scripts. Use requireCanvas=true for game canvas criteria. Returns actual command evidence; failed/missing browser is never a pass.");

        internal SoftwareDevelopmentOutcome RequireEvidence(SoftwareDevelopmentOutcome outcome) => BindEvidence(outcome, required: true);

        /// <summary>
        /// Always replaces tool-call notation with the trusted receipt when the check passed on current source, so a
        /// passing check is never lost. Only a required check adds the failing "missing receipt" entry.
        /// </summary>
        internal SoftwareDevelopmentOutcome BindEvidence(SoftwareDevelopmentOutcome outcome, bool required)
        {
            const string missing = "check_browser_build";
            // Model tool-call notation is not a shell command. Replace it with the trusted
            // current-source receipt; retain every unrelated validation and its failure.
            var validations = outcome.Validations.Where(x => !System.Text.RegularExpressions.Regex.IsMatch(
                x.Command.TrimStart(), @"^check_browser_build(?:\s|\(|$)")).ToList();
            if (Passed && !validations.Any(x => x.Command == successfulCommand))
                validations.Add(new(successfulCommand!, true, 0, "Observed current-source Chromium loading and surface check; independent QA remains required."));
            if (required && !Passed) validations.Add(new(missing, false, -1,
                "No current-source real-browser receipt. Call check_browser_build for the static output, fix observed errors, and retain its actual result; static checks do not replace browser evidence."));
            return outcome with { Validations = validations };
        }
    }

    // No npm dependency, browser download, application instrumentation or pixel decoder.
    internal const string Script = """
        const fs = require('node:fs/promises');
        const path = require('node:path');
        const os = require('node:os');
        const http = require('node:http');
        const {spawn} = require('node:child_process');
        const {createHash} = require('node:crypto');
        async function main() {
          const workspace = await fs.realpath(process.cwd());
          const root = await fs.realpath(path.resolve(process.argv[2] || 'dist'));
          if (!root.startsWith(workspace + path.sep)) throw Error('Build must be inside this workspace');
          const executable = process.env.CHROMIUM_PATH || process.env.CHROME_BIN;
          if (!executable) throw Error('Prepared Chromium unavailable; report an infrastructure blocker, not a skipped pass');
          const output = path.join(workspace, 'test-results/browser-smoke');
          await fs.mkdir(output, {recursive:true});
          const profile = await fs.mkdtemp(path.join(os.tmpdir(), 'csweet-browser-smoke-'));
          const errors = []; const pending = new Map(); let sequence = 0; let socket; let browser;
          const recordError = value => {if(errors.length<20) errors.push(String(value).slice(0,2000));};
          const types = {'.html':'text/html','.js':'text/javascript','.mjs':'text/javascript','.css':'text/css','.json':'application/json','.wasm':'application/wasm','.svg':'image/svg+xml','.png':'image/png'};
          const server = http.createServer(async (req,res) => {
            try {
              const pathname = decodeURIComponent(new URL(req.url,'http://localhost').pathname);
              if (pathname === '/favicon.ico') {res.writeHead(204);res.end();return;}
              const file = await fs.realpath(path.resolve(root,'.'+(pathname==='/'?'/index.html':pathname)));
              if (!file.startsWith(root+path.sep)) throw Error('Outside build');
              res.writeHead(200,{'Content-Type':types[path.extname(file)]||'application/octet-stream'});
              res.end(await fs.readFile(file));
            } catch {res.writeHead(404);res.end();}
          });
          try {
            await new Promise((resolve,reject)=>{server.once('error',reject);server.listen(0,'127.0.0.1',resolve);});
            browser = spawn(executable,['--headless',...(process.platform==='linux'?['--no-sandbox']:[]),'--disable-dev-shm-usage','--use-angle=swiftshader','--enable-unsafe-swiftshader','--remote-debugging-port=0','--user-data-dir='+profile,'about:blank'],{stdio:['ignore','ignore','pipe'],windowsHide:true});
            const endpoint = await new Promise((resolve,reject)=>{
              const timer=setTimeout(()=>reject(Error('Chromium startup timed out')),15000);
              browser.once('error',error=>{clearTimeout(timer);reject(error);});
              browser.once('exit',()=>{clearTimeout(timer);reject(Error('Chromium exited before CDP became ready'));});
              let text=''; browser.stderr.on('data',chunk=>{text=(text+chunk).slice(-4096);const match=text.match(/DevTools listening on (ws:\/\/[^\s]+)/);if(match){clearTimeout(timer);resolve(match[1]);}});
            });
            socket = new WebSocket(endpoint);
            await new Promise((resolve,reject)=>{socket.addEventListener('open',resolve,{once:true});socket.addEventListener('error',reject,{once:true});});
            socket.addEventListener('message',event=>{
              const data=JSON.parse(String(event.data)); const wait=pending.get(data.id);
              if(wait){pending.delete(data.id);clearTimeout(wait.timer);data.error?wait.reject(Error(data.error.message)):wait.resolve(data.result);}
              if(data.method==='Runtime.exceptionThrown') recordError(data.params.exceptionDetails.exception?.description || data.params.exceptionDetails.text);
              if(data.method==='Network.loadingFailed' && !data.params.canceled) recordError('Asset request failed: '+data.params.errorText);
              if(data.method==='Network.responseReceived' && data.params.response.status>=400) recordError('Asset HTTP '+data.params.response.status+': '+new URL(data.params.response.url).pathname);
            });
            const send=(method,params={},sessionId)=>new Promise((resolve,reject)=>{
              const id=++sequence; const timer=setTimeout(()=>{pending.delete(id);reject(Error('Browser command timed out: '+method));},15000);
              pending.set(id,{resolve,reject,timer});socket.send(JSON.stringify({id,method,params,...(sessionId?{sessionId}:{})}));
            });
            const {targetId}=await send('Target.createTarget',{url:'about:blank'});
            const {sessionId}=await send('Target.attachToTarget',{targetId,flatten:true});
            for(const method of ['Runtime.enable','Page.enable','Network.enable']) await send(method,{},sessionId);
            await send('Emulation.setDeviceMetricsOverride',{width:1280,height:720,deviceScaleFactor:1,mobile:false},sessionId);
            const navigation=await send('Page.navigate',{url:'http://127.0.0.1:'+server.address().port+'/'},sessionId);
            if(navigation.errorText) throw Error('Navigation failed: '+navigation.errorText);
            const requireCanvas=process.argv.includes('--canvas');
            const probe=`JSON.stringify({ready:document.readyState,canvas:[...document.querySelectorAll('canvas')].some(x=>{const r=x.getBoundingClientRect();return r.width>0&&r.height>0}),body:!!document.body&&document.body.innerText.trim().length>0})`;
            let surface;
            for(let attempt=0;attempt<50;attempt++) {
              const result=await send('Runtime.evaluate',{expression:probe,returnByValue:true},sessionId);
              surface=JSON.parse(result.result.value);
              if(surface.ready==='complete'&&(surface.canvas||(!requireCanvas&&surface.body))) break;
              await new Promise(resolve=>setTimeout(resolve,100));
            }
            await send('Runtime.evaluate',{expression:'new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))',awaitPromise:true},sessionId);
            const capture=await send('Page.captureScreenshot',{format:'png'},sessionId);
            const screenshot=Buffer.from(capture.data,'base64');
            await fs.writeFile(path.join(output,'screenshot.png'),screenshot);
            const passed=surface.ready==='complete'&&(requireCanvas?surface.canvas:surface.canvas||surface.body)&&errors.length===0;
            const report={passed,browser:'Chromium',viewport:{width:1280,height:720},surface,errors:[...new Set(errors)].slice(0,20),screenshot:'test-results/browser-smoke/screenshot.png',screenshotSha256:createHash('sha256').update(screenshot).digest('hex'),limitation:'Loading/surface evidence only; visual inspection and gameplay remain separate.'};
            await fs.writeFile(path.join(output,'report.json'),JSON.stringify(report,null,2));
            console.log(JSON.stringify(report));process.exitCode=passed?0:1;
          } finally {
            for(const wait of pending.values()){clearTimeout(wait.timer);wait.reject(Error('Browser check closed'));}
            socket?.close(); browser?.kill(); await new Promise(resolve=>server.close(resolve));
            if(browser&&browser.exitCode===null) await Promise.race([new Promise(resolve=>browser.once('exit',resolve)),new Promise(resolve=>setTimeout(resolve,3000))]);
            await fs.rm(profile,{recursive:true,force:true,maxRetries:5,retryDelay:100});
          }
        }
        main().catch(error=>{console.error('BROWSER_CHECK_FAILED: '+error.message);process.exitCode=1;});
        """;
}
