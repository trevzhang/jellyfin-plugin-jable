# Jable Browser Sidecar Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Route Jable HTML requests through a persistent NAS Chromium session so Jellyfin catalog synchronization works without a Mac proxy.

**Architecture:** A dependency-free Node.js bridge controls Chromium through CDP and returns rendered DOM as bounded JSON. `JableHttpClient` uses the bridge when configured and retains its current direct/proxy path as fallback; the existing C# parser remains the only metadata parser.

**Tech Stack:** .NET 8, Jellyfin 10.10.7, C# `HttpClient`, Node.js 22 standard library, Chrome DevTools Protocol, Docker Compose.

**Spec:** `docs/superpowers/specs/2026-09-15-jable-browser-sidecar-design.md`

## Global Constraints

- Chromium handles browser state and rendering; Node must not duplicate Jable metadata parsing.
- Do not bypass CAPTCHA or Cloudflare challenges.
- CDP port `9222` remains Docker-internal in the final deployment.
- Bridge accepts only HTTPS URLs on `jable.tv` or its subdomains and revalidates the final URL.
- Rendered HTML is limited to 4 MiB.
- Bridge requests and responses are JSON and require a bearer token.
- `GetImageAsync` and cover-file synchronization are outside this plan.
- Preserve Jellyfin's current direct/proxy path when no bridge URL is configured.
- Add no Node runtime dependencies.

---

## File Map

- Create `sidecar/package.json`: Node scripts and module mode.
- Create `sidecar/src/renderer.mjs`: CDP target lifecycle, navigation, timeout, final URL validation, and DOM extraction.
- Create `sidecar/src/server.mjs`: authenticated HTTP JSON API and serialized render execution.
- Create `sidecar/test/renderer.test.mjs`: renderer unit tests with fake fetch/WebSocket implementations.
- Create `sidecar/test/server.test.mjs`: HTTP API tests using an ephemeral local port.
- Create `sidecar/Dockerfile`: dependency-free Node 22 image.
- Create `deploy/jable-browser/docker-compose.yml`: Chromium, bridge, persistent profile, static host mappings, and shared network.
- Create `deploy/jable-browser/.env.example`: credentials, token, image names, and validated IP defaults.
- Create `deploy/jable-browser/README.md`: NAS deployment and IP refresh procedure.
- Modify `Jellyfin.Plugin.Jable/Configuration/PluginConfiguration.cs`: bridge URL/token settings and validation.
- Modify `Jellyfin.Plugin.Jable/Plugin.cs`: preserve/replace/clear bridge token during configuration updates.
- Modify `Jellyfin.Plugin.Jable/Services/JableHttpClient.cs`: bridge transport and health test while preserving direct transport.
- Modify `Jellyfin.Plugin.Jable/Api/JableController.cs`: administrator-only bridge test endpoint.
- Modify `Jellyfin.Plugin.Jable/Configuration/configPage.html`: bridge fields and connection test button.
- Modify `Jellyfin.Plugin.Jable.Tests/JableHttpClientTests.cs`: bridge transport tests.
- Modify `Jellyfin.Plugin.Jable.Tests/ConfigurationTests.cs`: bridge defaults, validation, and secret-field markup tests.
- Modify `scripts/smoke_jellyfin.sh`: real configuration secret persistence and bridge endpoint authorization checks.
- Modify `scripts/package_plugin.sh`: run sidecar tests and package version `0.1.6`.
- Modify `README.md`: sidecar installation, security, and troubleshooting.
- Modify `Jellyfin.Plugin.Jable/build.yaml` and `manifest.json`: release `0.1.6`.

---

### Task 1: Dependency-Free Chromium Renderer

**Files:**
- Create: `sidecar/package.json`
- Create: `sidecar/src/renderer.mjs`
- Create: `sidecar/test/renderer.test.mjs`

**Interfaces:**
- Produces: `isAllowedJableUrl(value: string): boolean`
- Produces: `ChromiumRenderer({ browserUrl, fetchImpl, WebSocketImpl, timeoutMs }).health(signal): Promise<void>`
- Produces: `ChromiumRenderer(...).render(url, signal): Promise<{ url: string, html: string }>`

- [ ] **Step 1: Add the Node package metadata**

```json
{
  "name": "jable-browser-bridge",
  "private": true,
  "type": "module",
  "scripts": {
    "start": "node src/server.mjs",
    "test": "node --test test/*.test.mjs"
  },
  "engines": {
    "node": ">=22"
  }
}
```

- [ ] **Step 2: Write failing URL and health tests**

Create `sidecar/test/renderer.test.mjs` with these initial tests:

```js
import test from 'node:test';
import assert from 'node:assert/strict';
import { ChromiumRenderer, isAllowedJableUrl } from '../src/renderer.mjs';

test('URL allowlist accepts only HTTPS Jable hosts without credentials', () => {
  assert.equal(isAllowedJableUrl('https://jable.tv/latest-updates/'), true);
  assert.equal(isAllowedJableUrl('https://assets.jable.tv/a.css'), true);
  for (const value of [
    'http://jable.tv/',
    'https://user:pass@jable.tv/',
    'https://jable.tv.evil.test/',
    'https://example.test/'
  ]) assert.equal(isAllowedJableUrl(value), false, value);
});

test('health requires a browser websocket endpoint', async () => {
  const renderer = new ChromiumRenderer({
    browserUrl: 'http://chromium:9222',
    fetchImpl: async url => {
      assert.equal(url, 'http://chromium:9222/json/version');
      return Response.json({ Browser: 'Chromium' });
    }
  });
  await assert.rejects(renderer.health(), /webSocketDebuggerUrl/);
});
```

- [ ] **Step 3: Run the renderer tests and confirm the missing module failure**

Run: `node --test sidecar/test/renderer.test.mjs`

Expected: FAIL with `ERR_MODULE_NOT_FOUND` for `sidecar/src/renderer.mjs`.

- [ ] **Step 4: Implement URL validation and browser health**

Create the start of `sidecar/src/renderer.mjs`:

```js
const MAX_HTML_BYTES = 4 * 1024 * 1024;

export function isAllowedJableUrl(value) {
  try {
    const url = new URL(value);
    return url.protocol === 'https:' && !url.username && !url.password &&
      (url.hostname === 'jable.tv' || url.hostname.endsWith('.jable.tv'));
  } catch {
    return false;
  }
}

export class ChromiumRenderer {
  constructor({ browserUrl, fetchImpl = fetch, WebSocketImpl = WebSocket, timeoutMs = 60_000 }) {
    this.browserUrl = new URL(browserUrl).origin;
    this.fetch = fetchImpl;
    this.WebSocket = WebSocketImpl;
    this.timeoutMs = timeoutMs;
  }

  async health(signal) {
    const response = await this.fetch(`${this.browserUrl}/json/version`, { signal });
    if (!response.ok) throw new Error(`Chromium health returned ${response.status}.`);
    const version = await response.json();
    if (!version.webSocketDebuggerUrl) throw new Error('Chromium health omitted webSocketDebuggerUrl.');
  }
}
```

- [ ] **Step 5: Run the initial tests**

Run: `node --test sidecar/test/renderer.test.mjs`

Expected: PASS, 2 tests.

- [ ] **Step 6: Add failing render lifecycle tests**

Extend the test file with a reusable fake CDP transport:

```js
function fakeTransport(page) {
  const calls = [];
  const fetchImpl = async (url, options = {}) => {
    calls.push([url, options.method || 'GET']);
    if (url.includes('/json/new?')) return Response.json({
      id: 'target-1',
      webSocketDebuggerUrl: 'ws://chromium/devtools/page/target-1'
    });
    if (url.endsWith('/json/close/target-1')) return new Response('', { status: 200 });
    throw new Error(`unexpected fetch ${url}`);
  };
  class FakeSocket {
    #listeners = new Map();
    constructor() { queueMicrotask(() => this.#emit('open', {})); }
    addEventListener(name, listener) {
      const listeners = this.#listeners.get(name) || [];
      listeners.push(listener);
      this.#listeners.set(name, listeners);
    }
    #emit(name, event) {
      for (const listener of this.#listeners.get(name) || []) listener(event);
    }
    send(value) {
      const request = JSON.parse(value);
      if (request.method === 'Page.navigate' && page.pendingHost) queueMicrotask(() => this.#emit('message', {
        data: JSON.stringify({ method: 'Network.requestWillBeSent', params: {
          requestId: 'pending-1', request: { url: `https://${page.pendingHost}/asset.js` }
        } })
      }));
      if (request.method === 'Runtime.evaluate' && page.pendingHost) return;
      const result = request.method === 'Runtime.evaluate'
        ? { result: { value: JSON.stringify(page) } }
        : {};
      queueMicrotask(() => this.#emit('message', { data: JSON.stringify({ id: request.id, result }) }));
    }
    close() { this.#emit('close', {}); }
  }
  return { calls, fetchImpl, FakeSocket };
}

test('render creates a temporary target, returns complete DOM, and closes it', async () => {
  const page = {
    readyState: 'complete',
    url: 'https://jable.tv/latest-updates/',
    html: '<!doctype html><html><body>ok</body></html>'
  };
  const { calls, fetchImpl, FakeSocket } = fakeTransport(page);
  const renderer = new ChromiumRenderer({
    browserUrl: 'http://chromium:9222', fetchImpl, WebSocketImpl: FakeSocket, timeoutMs: 1000
  });

  assert.deepEqual(await renderer.render('https://jable.tv/latest-updates/'), {
    url: page.url,
    html: page.html
  });
  assert.deepEqual(calls, [
    ['http://chromium:9222/json/new?about%3Ablank', 'PUT'],
    ['http://chromium:9222/json/close/target-1', 'GET']
  ]);
});

test('render rejects an unsafe final redirect and still closes the target', async () => {
  const transport = fakeTransport({
    readyState: 'complete', url: 'https://evil.test/', html: '<html></html>'
  });
  const renderer = new ChromiumRenderer({
    browserUrl: 'http://chromium:9222', fetchImpl: transport.fetchImpl,
    WebSocketImpl: transport.FakeSocket, timeoutMs: 1000
  });
  await assert.rejects(renderer.render('https://jable.tv/latest-updates/'), /final URL/);
  assert.equal(transport.calls.filter(([url]) => url.includes('/json/close/')).length, 1);
});

test('render rejects DOM larger than four MiB', async () => {
  const transport = fakeTransport({
    readyState: 'complete',
    url: 'https://jable.tv/latest-updates/',
    html: 'x'.repeat(4 * 1024 * 1024 + 1)
  });
  const renderer = new ChromiumRenderer({
    browserUrl: 'http://chromium:9222', fetchImpl: transport.fetchImpl,
    WebSocketImpl: transport.FakeSocket, timeoutMs: 1000
  });
  await assert.rejects(renderer.render('https://jable.tv/latest-updates/'), /too large/);
  assert.equal(transport.calls.filter(([url]) => url.includes('/json/close/')).length, 1);
});

test('timeout names the pending Jable resource host', async () => {
  const transport = fakeTransport({ pendingHost: 'assets-cdn.jable.tv' });
  const renderer = new ChromiumRenderer({
    browserUrl: 'http://chromium:9222', fetchImpl: transport.fetchImpl,
    WebSocketImpl: transport.FakeSocket, timeoutMs: 20
  });
  await assert.rejects(renderer.render('https://jable.tv/latest-updates/'), /assets-cdn\.jable\.tv/);
});
```

- [ ] **Step 7: Run the render tests and confirm `render` is missing**

Run: `node --test sidecar/test/renderer.test.mjs`

Expected: FAIL with `renderer.render is not a function`.

- [ ] **Step 8: Implement the minimal CDP request loop**

Add private helpers inside `renderer.mjs`:

```js
function byteLength(value) {
  return new TextEncoder().encode(value).byteLength;
}

function connect(WebSocketImpl, url, signal, onEvent) {
  const socket = new WebSocketImpl(url);
  let nextId = 0;
  let closing = false;
  const pending = new Map();
  const closed = new Promise((resolve, reject) => {
    socket.addEventListener('error', () => reject(new Error('Chromium CDP socket failed.')), { once: true });
    socket.addEventListener('close', () => closing ? resolve() : reject(new Error('Chromium CDP socket closed.')), { once: true });
    signal?.addEventListener('abort', () => {
      reject(signal.reason);
      socket.close();
    }, { once: true });
  });
  socket.addEventListener('message', event => {
    const message = JSON.parse(event.data);
    if (!message.id) {
      onEvent(message);
      return;
    }
    const waiter = pending.get(message.id);
    if (!waiter) return;
    pending.delete(message.id);
    message.error ? waiter.reject(new Error(message.error.message)) : waiter.resolve(message.result);
  });
  const ready = new Promise((resolve, reject) => {
    socket.addEventListener('open', resolve, { once: true });
    socket.addEventListener('error', () => reject(new Error('Chromium CDP socket failed.')), { once: true });
  });
  const command = async (method, params = {}) => {
    await Promise.race([ready, closed]);
    const id = ++nextId;
    const reply = new Promise((resolve, reject) => pending.set(id, { resolve, reject }));
    socket.send(JSON.stringify({ id, method, params }));
    return Promise.race([reply, closed]);
  };
  return { command, close: () => { closing = true; socket.close(); } };
}
```

Implement `render` with one temporary target and polling:

```js
async render(value, signal) {
  if (!isAllowedJableUrl(value)) throw new Error('Jable URL is not allowed.');
  const timeout = AbortSignal.timeout(this.timeoutMs);
  const combined = signal ? AbortSignal.any([signal, timeout]) : timeout;
  const created = await this.fetch(
    `${this.browserUrl}/json/new?${encodeURIComponent('about:blank')}`,
    { method: 'PUT', signal: combined }
  );
  if (!created.ok) throw new Error(`Chromium target creation returned ${created.status}.`);
  const target = await created.json();
  if (!target.id || !target.webSocketDebuggerUrl) throw new Error('Chromium target response is invalid.');
  const pendingRequests = new Map();
  const failedHosts = new Set();
  const cdp = connect(this.WebSocket, target.webSocketDebuggerUrl, combined, message => {
    if (message.method === 'Network.requestWillBeSent') {
      const requestUrl = message.params.request.url;
      if (isAllowedJableUrl(requestUrl)) pendingRequests.set(message.params.requestId, new URL(requestUrl).hostname);
    }
    if (message.method === 'Network.responseReceived') pendingRequests.delete(message.params.requestId);
    if (message.method === 'Network.loadingFailed') {
      const host = pendingRequests.get(message.params.requestId);
      if (host) failedHosts.add(host);
      pendingRequests.delete(message.params.requestId);
    }
  });
  try {
    await cdp.command('Network.enable');
    const navigation = await cdp.command('Page.navigate', { url: value });
    if (navigation.errorText) throw new Error(`Chromium navigation failed: ${navigation.errorText}.`);
    while (true) {
      combined.throwIfAborted();
      const result = await cdp.command('Runtime.evaluate', {
        expression: `JSON.stringify({readyState:document.readyState,url:location.href,html:document.documentElement.outerHTML})`,
        returnByValue: true
      });
      const page = JSON.parse(result.result.value);
      if (page.readyState === 'complete') {
        if (!isAllowedJableUrl(page.url)) throw new Error('Chromium final URL is not allowed.');
        if (byteLength(page.html) > MAX_HTML_BYTES) throw new Error('Rendered Jable HTML is too large.');
        return { url: page.url, html: page.html };
      }
      await new Promise((resolve, reject) => {
        const timer = setTimeout(resolve, 250);
        combined.addEventListener('abort', () => { clearTimeout(timer); reject(combined.reason); }, { once: true });
      });
    }
  } catch (error) {
    if (combined.aborted) {
      const hosts = [...new Set([...failedHosts, ...pendingRequests.values()])].sort();
      throw new DOMException(
        `Jable page did not finish loading${hosts.length ? `; pending hosts: ${hosts.join(', ')}` : ''}.`,
        'TimeoutError'
      );
    }
    throw error;
  } finally {
    cdp.close();
    await this.fetch(`${this.browserUrl}/json/close/${encodeURIComponent(target.id)}`).catch(() => {});
  }
}
```

- [ ] **Step 9: Run all renderer tests**

Run: `node --test sidecar/test/renderer.test.mjs`

Expected: PASS with URL, health, render, unsafe redirect, size, cancellation, and target-close coverage.

- [ ] **Step 10: Commit Task 1**

```bash
git add sidecar/package.json sidecar/src/renderer.mjs sidecar/test/renderer.test.mjs
git commit -m "feat: add Chromium DOM renderer"
```

---

### Task 2: Authenticated JSON Bridge Service

**Files:**
- Create: `sidecar/src/server.mjs`
- Create: `sidecar/test/server.test.mjs`
- Create: `sidecar/Dockerfile`
- Modify: `scripts/package_plugin.sh`

**Interfaces:**
- Consumes: `ChromiumRenderer.health(signal)` and `ChromiumRenderer.render(url, signal)`
- Produces: `createBridgeServer({ renderer, token, logger }): http.Server`
- Produces: `GET /healthz` and `POST /v1/render`

- [ ] **Step 1: Write failing HTTP API tests**

Create `sidecar/test/server.test.mjs`:

```js
import test from 'node:test';
import assert from 'node:assert/strict';
import { once } from 'node:events';
import { createBridgeServer } from '../src/server.mjs';

async function withServer(renderer, run) {
  const server = createBridgeServer({ renderer, token: 'secret', logger: { info() {}, error() {} } });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  try {
    const { port } = server.address();
    await run(`http://127.0.0.1:${port}`);
  } finally {
    server.close();
    await once(server, 'close');
  }
}

test('health reports browser readiness', async () => {
  await withServer({ health: async () => {}, render: async () => assert.fail() }, async base => {
    const response = await fetch(`${base}/healthz`);
    assert.equal(response.status, 200);
    assert.match(response.headers.get('x-request-id'), /^[0-9a-f-]{36}$/);
    assert.deepEqual(await response.json(), { ok: true, browser: 'ready' });
  });
});

test('render requires bearer token and returns JSON DOM', async () => {
  const renderer = { health: async () => {}, render: async url => ({ url, html: '<html>ok</html>' }) };
  await withServer(renderer, async base => {
    assert.equal((await fetch(`${base}/v1/render`, { method: 'POST' })).status, 401);
    const response = await fetch(`${base}/v1/render`, {
      method: 'POST',
      headers: { authorization: 'Bearer secret', 'content-type': 'application/json' },
      body: JSON.stringify({ url: 'https://jable.tv/latest-updates/' })
    });
    assert.equal(response.status, 200);
    assert.deepEqual(await response.json(), {
      url: 'https://jable.tv/latest-updates/', html: '<html>ok</html>'
    });
  });
});

test('invalid and oversized JSON bodies fail closed', async () => {
  await withServer({ health: async () => {}, render: async () => assert.fail() }, async base => {
    const invalid = await fetch(`${base}/v1/render`, {
      method: 'POST', headers: { authorization: 'Bearer secret' }, body: '{'
    });
    assert.equal(invalid.status, 400);
    const oversized = await fetch(`${base}/v1/render`, {
      method: 'POST', headers: { authorization: 'Bearer secret' }, body: 'x'.repeat(64 * 1024 + 1)
    });
    assert.equal(oversized.status, 413);
  });
});

test('renderer failures map to 502 and timeouts map to 504', async () => {
  for (const [error, status] of [[new Error('offline'), 502], [new DOMException('late', 'TimeoutError'), 504]]) {
    await withServer({ health: async () => {}, render: async () => { throw error; } }, async base => {
      const response = await fetch(`${base}/v1/render`, {
        method: 'POST',
        headers: { authorization: 'Bearer secret', 'content-type': 'application/json' },
        body: JSON.stringify({ url: 'https://jable.tv/latest-updates/' })
      });
      assert.equal(response.status, status);
    });
  }
});

test('concurrent renders execute serially', async () => {
  let active = 0;
  let maximum = 0;
  const renderer = { health: async () => {}, render: async url => {
    maximum = Math.max(maximum, ++active);
    await new Promise(resolve => setTimeout(resolve, 10));
    active--;
    return { url, html: '<html></html>' };
  } };
  await withServer(renderer, async base => {
    const options = value => ({
      method: 'POST',
      headers: { authorization: 'Bearer secret', 'content-type': 'application/json' },
      body: JSON.stringify({ url: `https://jable.tv/${value}` })
    });
    await Promise.all([fetch(`${base}/v1/render`, options('one')), fetch(`${base}/v1/render`, options('two'))]);
    assert.equal(maximum, 1);
  });
});
```

- [ ] **Step 2: Run the server tests and confirm the missing module failure**

Run: `node --test sidecar/test/server.test.mjs`

Expected: FAIL with `ERR_MODULE_NOT_FOUND` for `sidecar/src/server.mjs`.

- [ ] **Step 3: Implement the HTTP server with a serialized render tail**

Create `sidecar/src/server.mjs` using only `node:http`:

```js
import http from 'node:http';
import { randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { ChromiumRenderer, isAllowedJableUrl } from './renderer.mjs';

const MAX_REQUEST_BYTES = 64 * 1024;

function json(response, status, value) {
  const body = JSON.stringify(value);
  response.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'content-length': Buffer.byteLength(body),
    'cache-control': 'no-store',
    'x-content-type-options': 'nosniff'
  });
  response.end(body);
}

async function readJson(request) {
  const chunks = [];
  let size = 0;
  for await (const chunk of request) {
    size += chunk.length;
    if (size > MAX_REQUEST_BYTES) throw Object.assign(new Error('Request body is too large.'), { status: 413 });
    chunks.push(chunk);
  }
  try {
    return JSON.parse(Buffer.concat(chunks).toString('utf8'));
  } catch {
    throw Object.assign(new Error('Request body is not valid JSON.'), { status: 400 });
  }
}

function requestSignal(request) {
  const controller = new AbortController();
  request.once('aborted', () => controller.abort());
  request.once('close', () => { if (!request.complete) controller.abort(); });
  return controller.signal;
}

export function createBridgeServer({ renderer, token, logger = console }) {
  if (!token) throw new Error('BRIDGE_TOKEN is required.');
  let tail = Promise.resolve();
  const serialized = operation => {
    const current = tail.then(operation, operation);
    tail = current.catch(() => {});
    return current;
  };
  return http.createServer(async (request, response) => {
    const requestId = randomUUID();
    const startedAt = Date.now();
    response.setHeader('x-request-id', requestId);
    response.once('finish', () => logger.info({
      requestId,
      method: request.method,
      path: request.url,
      status: response.statusCode,
      durationMs: Date.now() - startedAt
    }));
    const signal = requestSignal(request);
    try {
      if (request.method === 'GET' && request.url === '/healthz') {
        await renderer.health(signal);
        return json(response, 200, { ok: true, browser: 'ready' });
      }
      if (request.method !== 'POST' || request.url !== '/v1/render')
        return json(response, 404, { code: 'not_found', message: 'Route not found.' });
      if (request.headers.authorization !== `Bearer ${token}`)
        return json(response, 401, { code: 'unauthorized', message: 'Bearer token is invalid.' });
      const body = await readJson(request);
      if (!isAllowedJableUrl(body.url))
        return json(response, 400, { code: 'invalid_request', message: 'Jable URL is not allowed.' });
      const result = await serialized(() => renderer.render(body.url, signal));
      return json(response, 200, result);
    } catch (error) {
      const timedOut = error?.name === 'TimeoutError';
      const status = error?.status || (timedOut ? 504 : 502);
      logger.error({ status, message: error?.message });
      return json(response, status, {
        code: timedOut ? 'browser_timeout' : status === 400 || status === 413 ? 'invalid_request' : 'browser_error',
        message: error?.message || 'Browser request failed.'
      });
    }
  });
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const renderer = new ChromiumRenderer({
    browserUrl: process.env.CHROMIUM_URL || 'http://chromium:9222',
    timeoutMs: Number(process.env.RENDER_TIMEOUT_MS || 60_000)
  });
  createBridgeServer({ renderer, token: process.env.BRIDGE_TOKEN })
    .listen(Number(process.env.PORT || 3000), '0.0.0.0');
}
```

- [ ] **Step 4: Run sidecar tests**

Run: `npm --prefix sidecar test`

Expected: PASS with no installed packages and no `node_modules` directory.

- [ ] **Step 5: Add the minimal Dockerfile**

```dockerfile
ARG NODE_IMAGE=node:22-alpine
FROM ${NODE_IMAGE}
WORKDIR /app
COPY package.json ./
COPY src ./src
USER node
EXPOSE 3000
CMD ["node", "src/server.mjs"]
```

- [ ] **Step 6: Make packaging run sidecar tests**

Replace the existing host `node --test` line with one Docker-based Node check so packaging still requires only Docker:

```bash
docker run --rm -v "$repo:/src" -w /src node:22-alpine sh -c \
    'node --test web-tests/jable.test.mjs && npm --prefix sidecar test'
```

- [ ] **Step 7: Run the repository's JavaScript checks**

Run:

```bash
node --test web-tests/jable.test.mjs
npm --prefix sidecar test
```

Expected: both commands PASS.

- [ ] **Step 8: Commit Task 2**

```bash
git add sidecar/src/server.mjs sidecar/test/server.test.mjs sidecar/Dockerfile scripts/package_plugin.sh
git commit -m "feat: expose authenticated browser bridge"
```

---

### Task 3: Plugin Bridge Configuration and Secret Handling

**Files:**
- Modify: `Jellyfin.Plugin.Jable/Configuration/PluginConfiguration.cs:9-59`
- Modify: `Jellyfin.Plugin.Jable/Plugin.cs:34-45`
- Modify: `Jellyfin.Plugin.Jable/Configuration/configPage.html:16-75`
- Modify: `Jellyfin.Plugin.Jable.Tests/ConfigurationTests.cs`

**Interfaces:**
- Produces: `PluginConfiguration.BrowserBridgeUrl: string`
- Produces: `PluginConfiguration.BrowserBridgeToken: string` with JSON redaction
- Produces: `NewBrowserBridgeToken` and `ClearBrowserBridgeToken` write-only configuration inputs

- [ ] **Step 1: Write failing configuration behavior tests**

Add to `ConfigurationTests.cs`:

```csharp
[Fact]
public void BrowserBridgeDefaultsAreDisabled()
{
    var config = new PluginConfiguration();
    Assert.Equal(string.Empty, config.BrowserBridgeUrl);
    Assert.Equal(string.Empty, config.BrowserBridgeToken);
}

[Fact]
public void BrowserBridgeTokenIsExcludedFromConfigurationJson()
{
    var json = System.Text.Json.JsonSerializer.Serialize(new PluginConfiguration
    {
        BrowserBridgeUrl = "http://bridge:3000/",
        BrowserBridgeToken = "secret"
    });
    Assert.Contains("BrowserBridgeUrl", json);
    Assert.DoesNotContain("secret", json);
    Assert.DoesNotContain("BrowserBridgeToken", json);
}
```

- [ ] **Step 2: Run configuration tests and confirm missing properties**

Run:

```bash
docker run --rm -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet test Jellyfin.Plugin.Jable.Tests/Jellyfin.Plugin.Jable.Tests.csproj \
  --filter ConfigurationTests
```

Expected: FAIL because bridge properties and fields do not exist.

- [ ] **Step 3: Add bridge settings with the existing secret pattern**

Add to `PluginConfiguration`:

```csharp
internal string? BridgeTokenInput;
internal bool ClearBridgeToken;

public string BrowserBridgeUrl { get; set; } = string.Empty;

[JsonIgnore]
public string BrowserBridgeToken { get; set; } = string.Empty;

[XmlIgnore, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public string? NewBrowserBridgeToken { get => null; set => BridgeTokenInput = value; }

[XmlIgnore, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
public bool ClearBrowserBridgeToken { get => false; set => ClearBridgeToken = value; }
```

Extend `Validate()`:

```csharp
if (BrowserBridgeUrl.Length > 0)
{
    JableHttpClient.ValidateBridgeUri(BrowserBridgeUrl);
    if (string.IsNullOrEmpty(BrowserBridgeToken) && string.IsNullOrEmpty(BridgeTokenInput))
        throw new ArgumentException("Browser bridge token is required when a bridge URL is configured.");
}
```

- [ ] **Step 4: Preserve, replace, and clear the token before validation**

Move `next.Validate()` below bridge-token resolution, then add this immediately before it in `Plugin.UpdateConfiguration`:

```csharp
next.BrowserBridgeToken = next.ClearBridgeToken
    ? string.Empty
    : !string.IsNullOrEmpty(next.BridgeTokenInput)
        ? next.BridgeTokenInput
        : Configuration.BrowserBridgeToken;
next.BridgeTokenInput = null;
next.ClearBridgeToken = false;
next.Validate();
```

Resolving the saved token before validation allows an unchanged blank password field to preserve the existing secret while still rejecting a newly configured bridge with no token.

- [ ] **Step 5: Add configuration page fields**

Place these above the existing proxy fields:

```html
<div class="inputContainer"><label for="browserBridgeUrl">Browser bridge URL</label><input id="browserBridgeUrl" type="url" placeholder="http://jable-browser-bridge:3000/"></div>
<div class="inputContainer"><label for="browserBridgeToken">Browser bridge token</label><input id="browserBridgeToken" type="password" autocomplete="new-password"></div>
<div class="checkboxContainer"><label><input id="clearBrowserBridgeToken" type="checkbox"> Clear saved browser bridge token</label></div>
<button id="testBrowserBridge" is="emby-button" type="button" class="raised block">Test browser bridge</button>
```

Load and save the fields using the existing proxy-password pattern:

```js
form.querySelector('#browserBridgeUrl').value = config.BrowserBridgeUrl || '';
form.querySelector('#browserBridgeToken').value = '';
form.querySelector('#clearBrowserBridgeToken').checked = false;
```

```js
config.BrowserBridgeUrl = form.querySelector('#browserBridgeUrl').value.trim();
config.NewBrowserBridgeToken = form.querySelector('#browserBridgeToken').value;
config.ClearBrowserBridgeToken = form.querySelector('#clearBrowserBridgeToken').checked;
```

- [ ] **Step 6: Run configuration tests**

Run the filtered command from Step 2.

Expected: PASS.

- [ ] **Step 7: Commit Task 3**

```bash
git add Jellyfin.Plugin.Jable/Configuration/PluginConfiguration.cs \
  Jellyfin.Plugin.Jable/Plugin.cs \
  Jellyfin.Plugin.Jable/Configuration/configPage.html \
  Jellyfin.Plugin.Jable.Tests/ConfigurationTests.cs
git commit -m "feat: configure Jable browser bridge"
```

---

### Task 4: Route HTML Through the Bridge

**Files:**
- Modify: `Jellyfin.Plugin.Jable/Services/JableHttpClient.cs:12-314`
- Modify: `Jellyfin.Plugin.Jable.Tests/JableHttpClientTests.cs`

**Interfaces:**
- Consumes: `PluginConfiguration.BrowserBridgeUrl` and `BrowserBridgeToken`
- Produces: `JableHttpClient.ValidateBridgeUri(string): Uri`
- Produces: `JableHttpClient.TestBridgeAsync(CancellationToken): Task`
- Changes: `GetHtmlAsync` prefers `/v1/render` when bridge configuration is present

- [ ] **Step 1: Add failing bridge transport tests**

Add `using System.Net.Http.Json;`, then add tests using a dedicated queue handler for the bridge client:

```csharp
[Fact]
public async Task GetHtmlUsesConfiguredBrowserBridgeWithoutCallingDirectTransport()
{
    var direct = new QueueHandler(() => throw new Xunit.Sdk.XunitException("direct transport used"));
    var bridge = new QueueHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new
        {
            url = "https://jable.tv/latest-updates/",
            html = "<html><body>rendered</body></html>"
        })
    });
    var config = new PluginConfiguration
    {
        BrowserBridgeUrl = "http://bridge:3000/",
        BrowserBridgeToken = "secret"
    };
    using var client = new JableHttpClient(direct, () => config, bridge);

    Assert.Contains("rendered", await client.GetHtmlAsync(
        new Uri("https://jable.tv/latest-updates/"), CancellationToken.None));
    Assert.Empty(direct.RequestUris);
    Assert.Single(bridge.RequestUris);
    Assert.Equal("http://bridge:3000/v1/render", bridge.RequestUris[0].AbsoluteUri);
    Assert.Equal("Bearer", bridge.Requests[0].Headers.Authorization?.Scheme);
    Assert.Equal("secret", bridge.Requests[0].Headers.Authorization?.Parameter);
}
```

Add these failure tests:

```csharp
[Theory]
[InlineData(HttpStatusCode.Unauthorized)]
[InlineData(HttpStatusCode.BadGateway)]
[InlineData(HttpStatusCode.GatewayTimeout)]
public async Task BridgeStatusFailuresRemainNetworkFailures(HttpStatusCode status)
{
    var config = new PluginConfiguration { BrowserBridgeUrl = "http://bridge:3000/", BrowserBridgeToken = "secret" };
    using var client = new JableHttpClient(new QueueHandler(), () => config,
        new QueueHandler(() => new HttpResponseMessage(status)));
    var error = await Assert.ThrowsAsync<JableRequestException>(() =>
        client.GetHtmlAsync(new Uri("https://jable.tv/latest-updates/"), CancellationToken.None));
    Assert.Equal(JableFailureKind.Network, error.Kind);
}

[Fact]
public async Task BridgeRejectsMalformedJsonAndUnsafeFinalUrl()
{
    var config = new PluginConfiguration { BrowserBridgeUrl = "http://bridge:3000/", BrowserBridgeToken = "secret" };
    foreach (var response in new[] {
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{") },
        new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { url = "https://evil.test/", html = "<html></html>" }) }
    })
    {
        using var client = new JableHttpClient(new QueueHandler(), () => config, new QueueHandler(() => response));
        var error = await Assert.ThrowsAsync<JableRequestException>(() =>
            client.GetHtmlAsync(new Uri("https://jable.tv/latest-updates/"), CancellationToken.None));
        Assert.Equal(JableFailureKind.Network, error.Kind);
    }
}

[Fact]
public async Task BridgeChallengeHtmlKeepsChallengeClassification()
{
    var config = new PluginConfiguration { BrowserBridgeUrl = "http://bridge:3000/", BrowserBridgeToken = "secret" };
    using var client = new JableHttpClient(new QueueHandler(), () => config,
        new QueueHandler(() => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = JsonContent.Create(new { url = "https://jable.tv/", html = Fixture("challenge.html") })
        }));
    var error = await Assert.ThrowsAsync<JableRequestException>(() =>
        client.GetHtmlAsync(new Uri("https://jable.tv/"), CancellationToken.None));
    Assert.Equal(JableFailureKind.Challenge, error.Kind);
}

[Fact]
public async Task BridgeRejectsOversizedHtml()
{
    var config = new PluginConfiguration { BrowserBridgeUrl = "http://bridge:3000/", BrowserBridgeToken = "secret" };
    using var client = new JableHttpClient(new QueueHandler(), () => config,
        new QueueHandler(() => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = JsonContent.Create(new { url = "https://jable.tv/", html = new string('x', MaxHtmlBytes + 1) })
        }));
    await Assert.ThrowsAsync<JableRequestException>(() =>
        client.GetHtmlAsync(new Uri("https://jable.tv/"), CancellationToken.None));
}

[Fact]
public async Task BridgePropagatesCallerCancellation()
{
    var config = new PluginConfiguration { BrowserBridgeUrl = "http://bridge:3000/", BrowserBridgeToken = "secret", RequestTimeoutSeconds = 60 };
    using var client = new JableHttpClient(new QueueHandler(), () => config, new NeverEndingHandler());
    using var cancellation = new CancellationTokenSource();
    var request = client.GetHtmlAsync(new Uri("https://jable.tv/"), cancellation.Token);
    cancellation.Cancel();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
}
```

Keep the existing test-class `MaxHtmlBytes` constant and extend `QueueHandler` to capture requests:

```csharp
public List<HttpRequestMessage> Requests { get; } = [];

protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
{
    Requests.Add(request);
    RequestUris.Add(request.RequestUri!);
    return Task.FromResult(_responses.Dequeue().Invoke());
}
```

Do not add a mocking package.

- [ ] **Step 2: Run filtered bridge tests and confirm constructor/method failures**

Run:

```bash
docker run --rm -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet test Jellyfin.Plugin.Jable.Tests/Jellyfin.Plugin.Jable.Tests.csproj \
  --filter "FullyQualifiedName~JableHttpClientTests"
```

Expected: FAIL because the bridge handler constructor and validation do not exist.

- [ ] **Step 3: Add a direct, non-proxied bridge `HttpClient`**

Add `using System.Net.Http.Headers;`, `using System.Net.Http.Json;`, and `using System.Text.Json;`. Extend the existing constructor instead of adding a new service abstraction:

```csharp
private readonly HttpClient _bridgeClient;

public JableHttpClient(HttpMessageHandler handler, Func<PluginConfiguration> configuration, HttpMessageHandler? bridgeHandler = null)
{
    ArgumentNullException.ThrowIfNull(handler);
    ArgumentNullException.ThrowIfNull(configuration);
    _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    _bridgeClient = new HttpClient(bridgeHandler ?? new SocketsHttpHandler { UseProxy = false }, disposeHandler: true)
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    _configuration = configuration;
}
```

Dispose `_bridgeClient` in `Dispose()`.

- [ ] **Step 4: Validate bridge authority URLs**

Add:

```csharp
public static Uri ValidateBridgeUri(string value)
{
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
        || uri.Scheme is not ("http" or "https")
        || string.IsNullOrEmpty(uri.Host)
        || !string.IsNullOrEmpty(uri.UserInfo)
        || uri.AbsolutePath != "/"
        || !string.IsNullOrEmpty(uri.Query)
        || !string.IsNullOrEmpty(uri.Fragment))
        throw new ArgumentException("Browser bridge URL must be an HTTP or HTTPS authority.", nameof(value));
    return uri;
}
```

- [ ] **Step 5: Implement bounded bridge rendering**

Add a response record and helper:

```csharp
private static readonly JsonSerializerOptions BridgeJsonOptions = new(JsonSerializerDefaults.Web);
private sealed record BridgeRenderRequest(string Url);
private sealed record BridgeRenderResponse(string Url, string Html);
```

```csharp
private async Task<string> GetBridgeHtmlAsync(Uri uri, CancellationToken cancellationToken)
{
    var config = _configuration();
    var endpoint = new Uri(ValidateBridgeUri(config.BrowserBridgeUrl), "v1/render");
    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
    {
        Content = JsonContent.Create(new BridgeRenderRequest(uri.AbsoluteUri))
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.BrowserBridgeToken);
    using var response = await _bridgeClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    if (!response.IsSuccessStatusCode)
        throw new JableRequestException(JableFailureKind.Network, $"Jable browser bridge returned {(int)response.StatusCode}.");
    var json = await ReadHtmlAsync(response.Content, cancellationToken).ConfigureAwait(false);
    var rendered = JsonSerializer.Deserialize<BridgeRenderResponse>(json, BridgeJsonOptions)
        ?? throw new JableRequestException(JableFailureKind.Network, "Jable browser bridge returned invalid JSON.");
    if (!Uri.TryCreate(rendered.Url, UriKind.Absolute, out var finalUri) || !IsAllowedJableUri(finalUri))
        throw new JableRequestException(JableFailureKind.Network, "Jable browser bridge returned an unsafe URL.");
    if (string.IsNullOrEmpty(rendered.Html))
        throw new JableRequestException(JableFailureKind.Network, "Jable browser bridge returned empty HTML.");
    if (Encoding.UTF8.GetByteCount(rendered.Html) > MaxHtmlBytes)
        throw new JableRequestException(JableFailureKind.Network, "Jable HTML response is too large.");
    return rendered.Html;
}
```

At the start of `GetHtmlAsync`, after creating the request scope:

```csharp
if (!IsAllowedJableUri(uri))
    throw new JableRequestException(JableFailureKind.Network, "Jable URI is not allowed.");
if (!string.IsNullOrWhiteSpace(_configuration().BrowserBridgeUrl))
{
    var html = await GetBridgeHtmlAsync(uri, scope.Token).ConfigureAwait(false);
    if (JableParser.IsChallengePage(html))
        throw new JableRequestException(JableFailureKind.Challenge, "Jable returned a challenge page.");
    return html;
}
```

Keep all current exception classification and caller-cancellation behavior around this branch.
Add `JsonException` to the existing non-cancellation exception filter so malformed bridge JSON becomes a `JableRequestException` instead of escaping the plugin boundary:

```csharp
catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException or JsonException)
{
    throw new JableRequestException(JableFailureKind.Network, "Jable response could not be read.", exception);
}
```

- [ ] **Step 6: Add bridge health method**

```csharp
public async Task TestBridgeAsync(CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(_configuration().BrowserBridgeUrl))
        throw new JableRequestException(JableFailureKind.Network, "Jable browser bridge is not configured.");
    _ = await GetHtmlAsync(new Uri("https://jable.tv/latest-updates/"), cancellationToken).ConfigureAwait(false);
}
```

- [ ] **Step 7: Run all `JableHttpClientTests`**

Run the filtered command from Step 2.

Expected: PASS, including all existing direct/proxy tests.

- [ ] **Step 8: Commit Task 4**

```bash
git add Jellyfin.Plugin.Jable/Services/JableHttpClient.cs \
  Jellyfin.Plugin.Jable.Tests/JableHttpClientTests.cs
git commit -m "feat: render Jable HTML through browser bridge"
```

---

### Task 5: Administrator Connection Test and Jellyfin Smoke Coverage

**Files:**
- Modify: `Jellyfin.Plugin.Jable/Api/JableController.cs:118-147`
- Modify: `Jellyfin.Plugin.Jable/Configuration/configPage.html:27-75`
- Modify: `Jellyfin.Plugin.Jable.Tests/JableAuthorizationTests.cs`
- Modify: `scripts/smoke_jellyfin.sh`

**Interfaces:**
- Produces: authenticated administrator-only `POST /Jable/Bridge/Test`

- [ ] **Step 1: Write failing controller behavior tests**

Add `using System.Net.Http.Json;`, extend the `JableAuthorizationTests` fixture with:

```csharp
private int _bridgeRequests;
private Func<HttpResponseMessage> _bridgeResponse = () => new(HttpStatusCode.BadGateway);
```

Pass this third handler to the existing `_client` construction:

```csharp
new ImageHandler(request =>
{
    _bridgeRequests++;
    Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
    Assert.Equal("secret", request.Headers.Authorization?.Parameter);
    return _bridgeResponse();
})
```

Add `TestBridge` to the non-anonymous method names in `OnlyStaticResourcesAllowAnonymous`, then add:

```csharp
[Fact]
public async Task OrdinaryUserCannotTestBrowserBridge()
{
    _config.BrowserBridgeUrl = "http://bridge:3000/";
    _config.BrowserBridgeToken = "secret";
    Assert.IsType<ForbidResult>(await _controller.TestBridge(CancellationToken.None));
    Assert.Equal(0, _bridgeRequests);
}

[Fact]
public async Task AdministratorCanTestBrowserBridgeWithoutSelectedLibrary()
{
    _policy.IsAdministrator = true;
    _config.SelectedLibraryId = Guid.Empty;
    _config.BrowserBridgeUrl = "http://bridge:3000/";
    _config.BrowserBridgeToken = "secret";
    _bridgeResponse = () => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new
        {
            url = "https://jable.tv/latest-updates/",
            html = "<html><body>rendered</body></html>"
        })
    };

    var result = Assert.IsType<OkObjectResult>(await _controller.TestBridge(CancellationToken.None));
    Assert.Equal(200, result.StatusCode ?? 200);
    Assert.Equal(1, _bridgeRequests);
}
```

The bridge test handler must increment `_bridgeRequests` and assert the request authorization header is exactly `Bearer secret`; do not assert against source text.

- [ ] **Step 2: Run authorization and configuration tests**

Run:

```bash
docker run --rm -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet test Jellyfin.Plugin.Jable.Tests/Jellyfin.Plugin.Jable.Tests.csproj \
  --filter "FullyQualifiedName~JableAuthorizationTests"
```

Expected: FAIL because the endpoint and button handler do not exist.

- [ ] **Step 3: Add the administrator-only endpoint**

Add to `JableController`:

```csharp
[HttpPost("Bridge/Test")]
public async Task<IActionResult> TestBridge(CancellationToken cancellationToken)
{
    if (AuthorizeUser(out _, administrator: true, requireLibrary: false) is { } rejection) return rejection;
    try
    {
        await client.TestBridgeAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new { Ok = true });
    }
    catch (Exception exception) when (exception is JableRequestException or HttpRequestException)
    {
        return StatusCode(StatusCodes.Status502BadGateway, new { Ok = false, Error = exception.Message });
    }
}
```

Extend the existing helper without changing current call sites:

```csharp
private IActionResult? AuthorizeUser(out User? user, bool administrator = false, bool requireLibrary = true)
{
    Response.Headers.CacheControl = "no-store";
    user = JableAuthorization.GetUserId(User) is { } id ? users.GetUserById(id) : null;
    if (user is null) return Unauthorized();
    if ((requireLibrary && !access.CanAccess(user))
        || (administrator && !JableAuthorization.IsAdministrator(users.GetUserDto(user).Policy))) return Forbid();
    return null;
}
```

- [ ] **Step 4: Wire the configuration-page test button**

Add a click handler that saves no configuration and does not send secrets:

```js
form.querySelector('#testBrowserBridge').addEventListener('click', async () => {
    const button = form.querySelector('#testBrowserBridge');
    button.disabled = true;
    try {
        const response = await ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('Jable/Bridge/Test'),
            dataType: 'json'
        });
        Dashboard.alert(response?.Ok ? 'Browser bridge connection succeeded' : 'Browser bridge connection failed');
    } catch {
        Dashboard.alert('Browser bridge connection failed');
    } finally {
        button.disabled = false;
    }
});
```

The administrator must save a new URL/token before testing it; the test uses the persisted configuration only.

- [ ] **Step 5: Extend real Jellyfin smoke tests for secret handling and authorization**

In `scripts/smoke_jellyfin.sh`:

- Assert configuration GET omits `BrowserBridgeToken` and `NewBrowserBridgeToken`.
- Set `bridge_secret = secrets.token_urlsafe(20)`, then save `BrowserBridgeUrl='http://127.0.0.1:3103/'` and `NewBrowserBridgeToken=bridge_secret`.
- Assert the XML contains the token while configuration JSON does not.
- Before bridge configuration, assert the authenticated administrator receives `502` from `/Jable/Bridge/Test` rather than the library-access `403`; source tests cover the ordinary-user administrator check.
- Clear the bridge URL/token before the smoke run exits.

Use the existing `persisted_secret()` pattern with a second helper:

```python
def persisted_bridge_token():
    return ET.parse(xml_path).getroot().findtext('BrowserBridgeToken') or ''
```

- [ ] **Step 6: Run filtered tests and the full plugin smoke test**

Run:

```bash
docker run --rm -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet test Jellyfin.Plugin.Jable.sln -c Release
node --test web-tests/jable.test.mjs
npm --prefix sidecar test
```

Expected: all commands PASS.

- [ ] **Step 7: Commit Task 5**

```bash
git add Jellyfin.Plugin.Jable/Api/JableController.cs \
  Jellyfin.Plugin.Jable/Configuration/configPage.html \
  Jellyfin.Plugin.Jable.Tests/JableAuthorizationTests.cs \
  scripts/smoke_jellyfin.sh
git commit -m "feat: test browser bridge from Jellyfin"
```

---

### Task 6: NAS Compose Deployment

**Files:**
- Create: `deploy/jable-browser/docker-compose.yml`
- Create: `deploy/jable-browser/.env.example`
- Create: `deploy/jable-browser/README.md`
- Modify: `README.md`

**Interfaces:**
- Produces Docker service names `jable-browser` and `jable-browser-bridge`
- Produces external Docker network `jable-internal`
- Plugin bridge URL: `http://jable-browser-bridge:3000/`

- [ ] **Step 1: Add the Compose environment template**

Create `.env.example`:

```dotenv
CHROMIUM_IMAGE=docker.1ms.run/jlesage/chromium:v26.08.3
NODE_IMAGE=docker.1ms.run/library/node:22-alpine
CHROMIUM_WEB_USER=jable
CHROMIUM_WEB_PASSWORD=replace-with-a-long-random-password
BRIDGE_TOKEN=replace-with-a-different-long-random-token
JABLE_EDGE_IP=104.20.42.172
JABLE_ASSET_CDN_IP=15.235.118.31
```

- [ ] **Step 2: Add the final Compose file**

Create `docker-compose.yml`:

```yaml
services:
  chromium:
    image: ${CHROMIUM_IMAGE}
    container_name: jable-browser
    environment:
      - USER_ID=1000
      - GROUP_ID=10
      - TZ=Asia/Shanghai
      - KEEP_APP_RUNNING=1
      - WEB_AUTHENTICATION=1
      - WEB_AUTHENTICATION_USERNAME=${CHROMIUM_WEB_USER}
      - WEB_AUTHENTICATION_PASSWORD=${CHROMIUM_WEB_PASSWORD}
      - SECURE_CONNECTION=1
      - CHROMIUM_REMOTE_DEBUGGING=1
    volumes:
      - ./config:/config:rw
    ports:
      - 3100:5800
    expose:
      - "9222"
    shm_size: 1gb
    restart: unless-stopped
    extra_hosts:
      - "jable.tv:${JABLE_EDGE_IP}"
      - "www.jable.tv:${JABLE_EDGE_IP}"
      - "assets.jable.tv:${JABLE_EDGE_IP}"
      - "assets-cdn.jable.tv:${JABLE_ASSET_CDN_IP}"
    networks: [jable-internal]

  bridge:
    build:
      context: ../../sidecar
      args:
        NODE_IMAGE: ${NODE_IMAGE}
    container_name: jable-browser-bridge
    environment:
      - CHROMIUM_URL=http://chromium:9222
      - BRIDGE_TOKEN=${BRIDGE_TOKEN}
      - RENDER_TIMEOUT_MS=60000
    depends_on: [chromium]
    restart: unless-stopped
    networks: [jable-internal]

networks:
  jable-internal:
    external: true
```

- [ ] **Step 3: Document deterministic deployment and DNS refresh**

`deploy/jable-browser/README.md` must contain these commands:

```bash
docker network inspect jable-internal >/dev/null 2>&1 || docker network create jable-internal
cp .env.example .env
docker compose up -d --build
docker compose ps
```

Document adding the same external network to Jellyfin's Compose service:

```yaml
services:
  jellyfin:
    networks:
      - default
      - jable-internal
networks:
  jable-internal:
    external: true
```

Document IP refresh using trusted DoH:

```bash
curl -fsS 'https://doh.pub/resolve?name=jable.tv&type=A'
curl -fsS 'https://doh.pub/resolve?name=assets-cdn.jable.tv&type=A'
```

After updating `.env`, recreate only the browser project:

```bash
docker compose up -d --force-recreate chromium bridge
```

- [ ] **Step 4: Add deployment validation commands**

Document:

```bash
curl -kI https://NAS_IP:3100/
docker exec jellyfin-app-1 sh -c 'wget -qO- http://jable-browser-bridge:3000/healthz'
```

The first command must redirect to `/login/`; the second must return `{"ok":true,"browser":"ready"}`. The bridge has no host `ports` entry and Chromium has no host `9222` mapping.

- [ ] **Step 5: Update root README**

Replace proxy-first installation wording with bridge-first instructions, retain proxy compatibility, and add:

- browser GUI URL and manual challenge procedure;
- token/XML plaintext warning;
- static IP refresh troubleshooting;
- explicit statement that the image mirror is only for pulling Docker images, not Jable traffic.

- [ ] **Step 6: Validate Compose interpolation**

Run:

```bash
cp deploy/jable-browser/.env.example deploy/jable-browser/.env
docker compose -f deploy/jable-browser/docker-compose.yml --env-file deploy/jable-browser/.env config >/tmp/jable-browser-compose.yml
rg -n '3100:5800|9222|jable-browser-bridge|extra_hosts' /tmp/jable-browser-compose.yml
rm deploy/jable-browser/.env
```

Expected: config succeeds; only `3100` is published; `9222` is exposed internally.

- [ ] **Step 7: Commit Task 6**

```bash
git add deploy/jable-browser README.md
git commit -m "docs: add NAS browser bridge deployment"
```

---

### Task 7: Prepare Version 0.1.6 Locally

**Files:**
- Modify: `scripts/package_plugin.sh`
- Modify: `scripts/smoke_jellyfin.sh`
- Modify: `Jellyfin.Plugin.Jable/build.yaml`
- Modify: `manifest.json`

**Interfaces:**
- Produces: `dist/jellyfin-plugin-jable-0.1.6.zip`
- Produces: committed release metadata ready for final branch review

- [ ] **Step 1: Update package and smoke versions to 0.1.6**

Change:

```bash
version=0.1.6
```

Update smoke paths from `Jable_0.1.5.0` to `Jable_0.1.6.0`, archive defaults to `jellyfin-plugin-jable-0.1.6.zip`, and client version text to `0.1.6`.

- [ ] **Step 2: Update plugin build metadata**

Set in `Jellyfin.Plugin.Jable/build.yaml`:

```yaml
version: "0.1.6.0"
changelog: "Route Jable synchronization through a persistent Chromium bridge"
```

- [ ] **Step 3: Run the complete verification suite**

Run:

```bash
./scripts/package_plugin.sh
./scripts/smoke_jellyfin.sh dist/jellyfin-plugin-jable-0.1.6.zip
```

Expected:

- all .NET tests pass;
- Jellyfin Web tests pass;
- sidecar tests pass;
- Web config self-test passes;
- disposable Jellyfin 10.10.7 smoke test passes;
- archive contains only `Jellyfin.Plugin.Jable.dll` and `build.yaml`.

- [ ] **Step 4: Update manifest with the built archive checksum**

Calculate and print the Jellyfin repository checksum and timestamp:

```bash
checksum=$(md5 -q dist/jellyfin-plugin-jable-0.1.6.zip)
timestamp=$(date -u +%Y-%m-%dT%H:%M:%SZ)
printf 'checksum=%s\ntimestamp=%s\n' "$checksum" "$timestamp"
```

Use `apply_patch` to prepend a `manifest.json` version object with version `0.1.6.0`, the bridge changelog, target ABI `10.10.7.0`, the `v0.1.6` GitHub source URL, and the exact checksum/timestamp printed above. Run `jq empty manifest.json` immediately after the edit.

- [ ] **Step 5: Commit release metadata**

```bash
git add scripts/package_plugin.sh scripts/smoke_jellyfin.sh Jellyfin.Plugin.Jable/build.yaml manifest.json
git commit -m "release: prepare v0.1.6"
```

- [ ] **Step 6: Re-run verification after release metadata changes**

Run:

```bash
./scripts/package_plugin.sh
./scripts/smoke_jellyfin.sh dist/jellyfin-plugin-jable-0.1.6.zip
git status --short
```

Expected: both scripts PASS; only ignored `dist/` artifacts remain outside Git tracking.

---

## Post-Review Release and NAS Deployment

Execute this section only after all seven tasks pass their task reviews, the whole-branch review is clean, and `feat/browser-sidecar` has been integrated into local `main`.

- [ ] **Step 1: Push code and publish GitHub release**

```bash
git push origin main
gh release create v0.1.6 dist/jellyfin-plugin-jable-0.1.6.zip \
  --repo trevzhang/jellyfin-plugin-jable \
  --title v0.1.6 \
  --notes 'Route Jable synchronization through a persistent Chromium bridge.'
```

Verify:

```bash
gh release view v0.1.6 --repo trevzhang/jellyfin-plugin-jable
curl -fsSI https://github.com/trevzhang/jellyfin-plugin-jable/releases/download/v0.1.6/jellyfin-plugin-jable-0.1.6.zip
```

- [ ] **Step 2: Replace the validation container with the final browser project**

On NAS:

1. Preserve the validation container's `config/` directory.
2. Deploy `deploy/jable-browser/docker-compose.yml` with generated `.env` secrets.
3. Attach `jellyfin-app-1` to `jable-internal` through its Compose definition.
4. Confirm `jable-browser`, `jable-browser-bridge`, and `jellyfin-app-1` are running.
5. Confirm host port `3100` is open and host port `9222` is closed.

- [ ] **Step 3: Install plugin v0.1.6 and configure bridge**

In Jellyfin:

1. Refresh the configured plugin repository.
2. Install Jable `0.1.6.0`.
3. Restart Jellyfin.
4. Set `Browser bridge URL` to `http://jable-browser-bridge:3000/`.
5. Set the same `BRIDGE_TOKEN` used by the bridge container.
6. Save, then click `Test browser bridge`.

Expected: success message without a proxy URL.

- [ ] **Step 4: Run the production synchronization smoke test**

1. Open `/Jable/Page` as an administrator.
2. Click `立即同步`.
3. Wait for the scheduled task to finish.
4. Confirm `LastSuccessfulSync` advances and `LastError` is empty.
5. Confirm the catalog contains at least one recent item with number, title, actresses, view count, and favorite count.
6. Restart the browser project and repeat one synchronization to confirm profile persistence.

- [ ] **Step 5: Final security check**

Verify:

- `3102`/`9222` is not reachable from the LAN;
- Chromium GUI requires HTTPS login;
- bridge token is absent from Jellyfin configuration JSON and logs;
- no proxy is configured in Jellyfin;
- existing Firefox container remains stopped;
- repository working tree is clean.
