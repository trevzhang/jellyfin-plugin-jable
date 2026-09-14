import test from 'node:test';
import assert from 'node:assert/strict';
import { once } from 'node:events';
import { createBridgeServer } from '../src/server.mjs';

async function withServer(renderer, run, logger = { info() {}, error() {} }) {
  const server = createBridgeServer({ renderer, token: 'secret', logger });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  try {
    const { port } = server.address();
    await run(`http://127.0.0.1:${port}`);
  } finally {
    server.close();
    server.closeAllConnections();
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

test('request logs contain only a query-free path', async () => {
  const logs = [];
  await withServer({ health: async () => {}, render: async () => assert.fail() }, async base => {
    assert.equal((await fetch(`${base}/healthz?token=secret`)).status, 404);
  }, { info: entry => logs.push(entry), error() {} });
  assert.equal(logs.length, 1);
  assert.equal(logs[0].path, '/healthz');
  assert.doesNotMatch(JSON.stringify(logs[0]), /secret/);
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

test('render requires a JSON object with a URL', async () => {
  let renders = 0;
  await withServer({ health: async () => {}, render: async () => { renders++; } }, async base => {
    for (const body of ['null', '[]', '{}']) {
      const response = await fetch(`${base}/v1/render`, {
        method: 'POST',
        headers: { authorization: 'Bearer secret', 'content-type': 'application/json' },
        body
      });
      assert.equal(response.status, 400);
      assert.equal((await response.json()).code, 'invalid_request');
    }
  });
  assert.equal(renders, 0);
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

test('normal and interrupted requests each log once', async () => {
  const logs = [];
  let beginRender;
  const rendering = new Promise(resolve => { beginRender = resolve; });
  let releaseRender;
  const rendered = new Promise(resolve => { releaseRender = resolve; });
  let resolveInterruptedLog;
  const interruptedLog = new Promise(resolve => { resolveInterruptedLog = resolve; });
  const logger = { info(entry) {
    logs.push(entry);
    if (entry.method === 'POST') resolveInterruptedLog(entry);
  }, error() {} };
  const renderer = {
    health: async () => {},
    render: async () => { beginRender(); return rendered; }
  };
  await withServer(renderer, async base => {
    assert.equal((await fetch(`${base}/healthz`)).status, 200);
    const controller = new AbortController();
    const request = fetch(`${base}/v1/render`, {
      method: 'POST',
      headers: { authorization: 'Bearer secret', 'content-type': 'application/json' },
      body: JSON.stringify({ url: 'https://jable.tv/latest-updates/' }),
      signal: controller.signal
    }).catch(() => {});
    await rendering;
    controller.abort();
    try {
      const entry = await Promise.race([
        interruptedLog,
        new Promise(resolve => setTimeout(() => resolve(), 50))
      ]);
      assert.ok(entry, 'interrupted request must be logged');
      assert.match(entry.requestId, /^[0-9a-f-]{36}$/);
      assert.equal(entry.method, 'POST');
      assert.equal(entry.path, '/v1/render');
      assert.equal(typeof entry.status, 'number');
      assert.equal(typeof entry.durationMs, 'number');
    } finally {
      releaseRender({ url: 'https://jable.tv/latest-updates/', html: '<html></html>' });
      await request;
    }
  }, logger);
  const normal = logs.filter(entry => entry.method === 'GET');
  assert.equal(normal.length, 1);
  assert.match(normal[0].requestId, /^[0-9a-f-]{36}$/);
  assert.equal(normal[0].path, '/healthz');
  assert.equal(typeof normal[0].status, 'number');
  assert.equal(typeof normal[0].durationMs, 'number');
  assert.equal(logs.filter(entry => entry.method === 'POST').length, 1);
});
