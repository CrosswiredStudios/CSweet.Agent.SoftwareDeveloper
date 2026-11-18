import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

// Return the JSON that this page actually emitted, using one browser and one wall-clock deadline.
export async function readBrowserRecord(pageUrl, elementId, timeoutMs = 45000) {
  const executable = process.env.CHROMIUM_PATH || process.env.CHROME_BIN;
  if (!executable) throw new Error('Set CHROMIUM_PATH to the installed Chromium executable.');
  const profile = await mkdtemp(join(tmpdir(), 'csweet-record-'));
  const controller = new AbortController();
  const { signal } = controller;
  let phase = 'launching Chromium';
  const deadline = setTimeout(() => controller.abort(new Error('Browser record deadline while ' + phase)), timeoutMs);
  const browser = spawn(executable, ['--headless', '--remote-debugging-port=0',
    '--disable-dev-shm-usage', '--disable-background-timer-throttling',
    ...(process.platform === 'linux' && process.getuid?.() === 0 ? ['--no-sandbox'] : []),
    '--user-data-dir=' + profile, pageUrl], { stdio: 'ignore', windowsHide: true });
  const closed = new Promise(resolve => browser.once('close', resolve));
  browser.once('error', error => controller.abort(error));
  browser.once('exit', code => controller.abort(new Error('Chromium exited (' + code + ') while ' + phase)));
  let socket;
  try {
    let port;
    while (!port) {
      signal.throwIfAborted();
      try { port = Number((await readFile(join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]); }
      catch (error) { if (error.code !== 'ENOENT') throw error; }
      if (!port) await delay(75, undefined, { signal });
    }
    phase = 'connecting to the scenario page';
    let page;
    while (!page) {
      const response = await fetch('http://127.0.0.1:' + port + '/json/list', { signal });
      page = (await response.json()).find(target => target.type === 'page' && target.url === pageUrl);
      if (!page) await delay(75, undefined, { signal });
    }
    socket = new WebSocket(page.webSocketDebuggerUrl);
    await once(socket, 'open', { signal });
    socket.addEventListener('close', () => controller.abort(new Error('Chromium closed while ' + phase)), { once: true });
    phase = 'waiting for a JSON record in #' + elementId;
    let id = 0;
    for (;;) {
      signal.throwIfAborted();
      const reply = once(socket, 'message', { signal });
      socket.send(JSON.stringify({ id: ++id, method: 'Runtime.evaluate', params: {
        expression: 'document.getElementById(' + JSON.stringify(elementId) + ')?.textContent',
        returnByValue: true } }));
      const [event] = await reply;
      const message = JSON.parse(event.data);
      if (message.id !== id || message.error || message.result?.exceptionDetails)
        throw new Error('Chromium could not read the record element.');
      const value = message.result.result.value;
      if (typeof value === 'string' && value.trim()) return JSON.parse(value);
      await delay(100, undefined, { signal });
    }
  } catch (error) {
    throw signal.aborted ? signal.reason : error;
  } finally {
    clearTimeout(deadline);
    // Chromium owns the profile lock. Give it a bounded graceful exit before reaping it.
    if (socket?.readyState === WebSocket.OPEN) {
      try { socket.send(JSON.stringify({ id: -1, method: 'Browser.close' })); } catch { /* exiting */ }
      await Promise.race([closed, delay(750, undefined, { ref: false })]);
    }
    socket?.close();
    if (browser.exitCode === null && browser.signalCode === null) browser.kill('SIGKILL');
    await Promise.race([closed, delay(1500, undefined, { ref: false })]);
    await rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
  }
}
