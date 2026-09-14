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
    browserUrl: 'http://chromium:9222',
    fetchImpl: async url => {
      assert.equal(url, 'http://chromium:9222/json/version');
      return Response.json({ Browser: 'Chromium' });
    }
  });
  await assert.rejects(renderer.health(), /webSocketDebuggerUrl/);
});

test('failed target creation clears the render timeout', async () => {
  await execFile(process.execPath, ['--input-type=module', '--eval', `
    import { ChromiumRenderer } from ${JSON.stringify(new URL('../src/renderer.mjs', import.meta.url).href)};
    const renderer = new ChromiumRenderer({
      browserUrl: 'http://chromium:9222', timeoutMs: 1000,
      fetchImpl: async () => { throw new Error('offline'); }
    });
    await renderer.render('https://jable.tv/latest-updates/').catch(() => {});
  `], { timeout: 300 });
});

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
      if (request.method === 'Page.navigate') for (const method of page.networkEvents || []) queueMicrotask(() => this.#emit('message', {
        data: JSON.stringify({ method, params: { requestId: 'pending-1' } })
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

test('render does not wait for a stalled target close', async () => {
  const transport = fakeTransport({
    readyState: 'complete', url: 'https://jable.tv/latest-updates/', html: '<html></html>'
  });
  let closeSignal;
  const renderer = new ChromiumRenderer({
    browserUrl: 'http://chromium:9222',
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
    browserUrl: 'http://chromium:9222', fetchImpl: transport.fetchImpl,
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
    browserUrl: 'http://chromium:9222', fetchImpl: transport.fetchImpl,
    WebSocketImpl: transport.FakeSocket, timeoutMs: 20
  });
  await assert.rejects(renderer.render('https://jable.tv/latest-updates/'), error => {
    assert.equal(error.name, 'TimeoutError');
    assert.doesNotMatch(error.message, /assets-cdn\.jable\.tv/);
    return true;
  });
});
