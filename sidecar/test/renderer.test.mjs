import test from 'node:test';
import assert from 'node:assert/strict';
import { execFile as execFileCallback } from 'node:child_process';
import { promisify } from 'node:util';
import { ChromiumRenderer, isAllowedJableUrl } from '../src/renderer.mjs';

const execFile = promisify(execFileCallback);

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
    browserUrl: 'http://127.0.0.1:9222',
    fetchImpl: async url => {
      assert.equal(url, 'http://127.0.0.1:9222/json/version');
      return Response.json({ Browser: 'Chromium' });
    }
  });
  await assert.rejects(renderer.health(), /webSocketDebuggerUrl/);
});

test('failed target creation clears the render timeout', async () => {
  await execFile(process.execPath, ['--input-type=module', '--eval', `
    import { ChromiumRenderer } from ${JSON.stringify(new URL('../src/renderer.mjs', import.meta.url).href)};
    const renderer = new ChromiumRenderer({
      browserUrl: 'http://127.0.0.1:9222', timeoutMs: 1000,
      fetchImpl: async () => { throw new Error('offline'); }
    });
    await renderer.render('https://jable.tv/latest-updates/').catch(() => {});
  `], { timeout: 300 });
});

function fakeTransport(page) {
  const calls = [];
  const socketUrls = [];
  const commands = [];
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
    constructor(url) { socketUrls.push(url); queueMicrotask(() => this.#emit('open', {})); }
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
      commands.push(request);
      if (request.method === 'Page.navigate') for (const params of page.pausedRequests || []) queueMicrotask(() => this.#emit('message', {
        data: JSON.stringify({ method: 'Fetch.requestPaused', params })
      }));
      if (request.method === 'Page.navigate' && page.pendingHost) queueMicrotask(() => this.#emit('message', {
        data: JSON.stringify({ method: 'Network.requestWillBeSent', params: {
          requestId: 'pending-1', request: { url: `https://${page.pendingHost}/asset.js` }
        } })
      }));
      if (request.method === 'Page.navigate') for (const method of page.networkEvents || []) queueMicrotask(() => this.#emit('message', {
        data: JSON.stringify({ method, params: { requestId: 'pending-1' } })
      }));
      if (request.method === 'Runtime.evaluate' && page.pendingHost) return;
      const result = request.method === 'Runtime.evaluate'
        ? { result: { value: JSON.stringify(page) } }
        : request.method === 'Page.getFrameTree' ? { frameTree: { frame: { id: 'main-frame' } } } : {};
      queueMicrotask(() => this.#emit('message', { data: JSON.stringify({ id: request.id, result }) }));
    }
    close() { this.#emit('close', {}); }
  }
  return { calls, socketUrls, commands, fetchImpl, FakeSocket };
}

test('health and render resolve the current Chromium IP for HTTP and WebSocket endpoints', async () => {
  const page = { readyState: 'complete', url: 'https://jable.tv/', html: '<html>ok</html>' };
  const transport = fakeTransport(page);
  const healthUrls = [];
  const addresses = ['172.18.0.2', '172.18.0.3', '::1'];
  const renderer = new ChromiumRenderer({
    browserUrl: 'https://chromium:9443',
    lookupImpl: async host => {
      assert.equal(host, 'chromium');
      return { address: addresses.shift() };
    },
    fetchImpl: (url, options) => {
      if (url.endsWith('/json/version')) {
        healthUrls.push(url);
        return Response.json({ webSocketDebuggerUrl: 'wss://chromium:9443/devtools/browser/1' });
      }
      return transport.fetchImpl(url, options);
    },
    WebSocketImpl: transport.FakeSocket
  });
  await renderer.health();
  await renderer.render(page.url);
  await renderer.render(page.url);
  assert.deepEqual(healthUrls, ['https://172.18.0.2:9443/json/version']);
  assert.deepEqual(transport.calls, [
    ['https://172.18.0.3:9443/json/new?about%3Ablank', 'PUT'],
    ['https://172.18.0.3:9443/json/close/target-1', 'GET'],
    ['https://[::1]:9443/json/new?about%3Ablank', 'PUT'],
    ['https://[::1]:9443/json/close/target-1', 'GET']
  ]);
  assert.deepEqual(transport.socketUrls, [
    'wss://172.18.0.3:9443/devtools/page/target-1',
    'wss://[::1]:9443/devtools/page/target-1'
  ]);
  assert.equal(addresses.length, 0);
});

test('render creates a temporary target, returns complete DOM, and closes it', async () => {
  const page = {
    readyState: 'complete',
    url: 'https://jable.tv/latest-updates/',
    html: '<!doctype html><html><body>ok</body></html>'
  };
  const { calls, fetchImpl, FakeSocket } = fakeTransport(page);
  const renderer = new ChromiumRenderer({
    browserUrl: 'http://127.0.0.1:9222', fetchImpl, WebSocketImpl: FakeSocket, timeoutMs: 1000
  });

  assert.deepEqual(await renderer.render('https://jable.tv/latest-updates/'), {
    url: page.url,
    html: page.html
  });
  assert.deepEqual(calls, [
    ['http://127.0.0.1:9222/json/new?about%3Ablank', 'PUT'],
    ['http://127.0.0.1:9222/json/close/target-1', 'GET']
  ]);
});

test('render rejects an unsafe final redirect and still closes the target', async () => {
  const transport = fakeTransport({
    readyState: 'complete', url: 'https://evil.test/', html: '<html></html>'
  });
  const renderer = new ChromiumRenderer({
    browserUrl: 'http://127.0.0.1:9222', fetchImpl: transport.fetchImpl,
    WebSocketImpl: transport.FakeSocket, timeoutMs: 1000
  });
  await assert.rejects(renderer.render('https://jable.tv/latest-updates/'), /final URL/);
  assert.equal(transport.calls.filter(([url]) => url.includes('/json/close/')).length, 1);
});

test('a synchronous WebSocket constructor failure still closes the created target', async () => {
  const transport = fakeTransport({});
  const renderer = new ChromiumRenderer({
    browserUrl: 'http://127.0.0.1:9222', fetchImpl: transport.fetchImpl,
    WebSocketImpl: class { constructor() { throw new Error('socket construction failed'); } }
  });
  await assert.rejects(renderer.render('https://jable.tv/'), /socket construction failed/);
  assert.equal(transport.calls.filter(([url]) => url.includes('/json/close/')).length, 1);
});

test('Fetch interception permits Jable HTTPS and local resources but blocks external subresources before navigation', async () => {
  const urls = [
    'https://jable.tv/', 'https://assets.jable.tv/app.js', 'about:blank',
    'data:image/png;base64,AA==', 'blob:https://jable.tv/local',
    'https://external.test/app.js', 'http://jable.tv/insecure.js', 'http://127.0.0.1/private'
  ];
  const transport = fakeTransport({
    readyState: 'complete', url: 'https://jable.tv/', html: '<html>ok</html>',
    pausedRequests: urls.map((url, index) => ({
      requestId: `request-${index}`, frameId: index === 7 ? 'child-frame' : 'main-frame',
      resourceType: index === 0 || index === 7 ? 'Document' : 'Script', request: { url }
    }))
  });
  const renderer = new ChromiumRenderer({
    browserUrl: 'http://127.0.0.1:9222', fetchImpl: transport.fetchImpl,
    WebSocketImpl: transport.FakeSocket
  });
  await renderer.render('https://jable.tv/');
  const methods = transport.commands.map(command => command.method);
  assert.ok(methods.indexOf('Fetch.enable') >= 0, 'interception must be enabled');
  assert.ok(methods.indexOf('Fetch.enable') < methods.indexOf('Page.navigate'));
  assert.deepEqual(transport.commands.find(command => command.method === 'Fetch.enable').params,
    { patterns: [{ urlPattern: '*', requestStage: 'Request' }] });
  assert.deepEqual(transport.commands.filter(command => command.method === 'Fetch.continueRequest').map(command => command.params),
    [0, 1, 2, 3, 4].map(index => ({ requestId: `request-${index}` })));
  assert.deepEqual(transport.commands.filter(command => command.method === 'Fetch.failRequest').map(command => command.params),
    [5, 6, 7].map(index => ({ requestId: `request-${index}`, errorReason: 'BlockedByClient' })));
});

test('an external top-level redirect fails its paused request and returns an unsafe-navigation error', async () => {
  const transport = fakeTransport({
    readyState: 'complete', url: 'https://jable.tv/', html: '<html>old page</html>',
    pausedRequests: [
      { requestId: 'initial', frameId: 'main-frame', resourceType: 'Document', request: { url: 'https://jable.tv/' } },
      { requestId: 'redirect', frameId: 'main-frame', resourceType: 'Document', redirectedRequestId: 'initial', request: { url: 'http://127.0.0.1/private' } }
    ]
  });
  const renderer = new ChromiumRenderer({
    browserUrl: 'http://127.0.0.1:9222', fetchImpl: transport.fetchImpl,
    WebSocketImpl: transport.FakeSocket
  });
  await assert.rejects(renderer.render('https://jable.tv/'), /unsafe navigation/i);
  assert.ok(transport.commands.some(command => command.method === 'Fetch.continueRequest' && command.params.requestId === 'initial'));
  assert.ok(transport.commands.some(command => command.method === 'Fetch.failRequest' && command.params.requestId === 'redirect'));
  assert.equal(transport.calls.filter(([url]) => url.includes('/json/close/')).length, 1);
});

test('render rejects DOM larger than four MiB', async () => {
  const transport = fakeTransport({
    readyState: 'complete',
    url: 'https://jable.tv/latest-updates/',
    html: 'x'.repeat(4 * 1024 * 1024 + 1)
  });
  const renderer = new ChromiumRenderer({
    browserUrl: 'http://127.0.0.1:9222', fetchImpl: transport.fetchImpl,
    WebSocketImpl: transport.FakeSocket, timeoutMs: 1000
  });
  await assert.rejects(renderer.render('https://jable.tv/latest-updates/'), /too large/);
  assert.equal(transport.calls.filter(([url]) => url.includes('/json/close/')).length, 1);
});

test('timeout names the pending Jable resource host', async () => {
  const transport = fakeTransport({ pendingHost: 'assets-cdn.jable.tv' });
  const renderer = new ChromiumRenderer({
    browserUrl: 'http://127.0.0.1:9222', fetchImpl: transport.fetchImpl,
    WebSocketImpl: transport.FakeSocket, timeoutMs: 20
  });
  await assert.rejects(renderer.render('https://jable.tv/latest-updates/'), /assets-cdn\.jable\.tv/);
});

test('render does not wait for a stalled target close', async () => {
  const transport = fakeTransport({
    readyState: 'complete', url: 'https://jable.tv/latest-updates/', html: '<html></html>'
  });
  let closeSignal;
  const renderer = new ChromiumRenderer({
    browserUrl: 'http://127.0.0.1:9222',
    fetchImpl: (url, options = {}) => {
      if (url.includes('/json/close/')) {
        closeSignal = options.signal;
        return new Promise(() => {});
      }
      return transport.fetchImpl(url, options);
    },
    WebSocketImpl: transport.FakeSocket,
    timeoutMs: 1000
  });
  const result = await Promise.race([
    renderer.render('https://jable.tv/latest-updates/'),
    new Promise((_, reject) => setTimeout(() => reject(new Error('target close blocked render')), 50))
  ]);
  assert.deepEqual(result, { url: 'https://jable.tv/latest-updates/', html: '<html></html>' });
  assert.ok(closeSignal);
});

test('timeout keeps a host pending after response headers', async () => {
  const transport = fakeTransport({
    pendingHost: 'assets-cdn.jable.tv', networkEvents: ['Network.responseReceived']
  });
  const renderer = new ChromiumRenderer({
    browserUrl: 'http://127.0.0.1:9222', fetchImpl: transport.fetchImpl,
    WebSocketImpl: transport.FakeSocket, timeoutMs: 20
  });
  await assert.rejects(renderer.render('https://jable.tv/latest-updates/'), /assets-cdn\.jable\.tv/);
});

test('timeout drops a host after loading finishes', async () => {
  const transport = fakeTransport({
    pendingHost: 'assets-cdn.jable.tv',
    networkEvents: ['Network.responseReceived', 'Network.loadingFinished']
  });
  const renderer = new ChromiumRenderer({
    browserUrl: 'http://127.0.0.1:9222', fetchImpl: transport.fetchImpl,
    WebSocketImpl: transport.FakeSocket, timeoutMs: 20
  });
  await assert.rejects(renderer.render('https://jable.tv/latest-updates/'), error => {
    assert.equal(error.name, 'TimeoutError');
    assert.doesNotMatch(error.message, /assets-cdn\.jable\.tv/);
    return true;
  });
});
