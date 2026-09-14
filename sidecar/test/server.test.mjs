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
