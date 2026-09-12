import test from 'node:test';
import assert from 'node:assert/strict';
import { findCurrentServer, buildQuery, createApi, cardLink, loadImages } from '../Jellyfin.Plugin.Jable/Web/jable.mjs';

const origin = 'http://192.0.2.10:8899';

test('failed navigation restores the successful page and retries the same offset', async () => {
  const saved = Object.fromEntries(['window', 'document', 'location', 'localStorage', 'FormData', 'fetch'].map(key => [key, globalThis[key]]));
  const nodes = new Map();
  const node = id => {
    if (!nodes.has(id)) nodes.set(id, { listeners: {}, value: '', addEventListener(event, fn) { this.listeners[event] = fn; }, setAttribute() {}, replaceChildren() {} });
    return nodes.get(id);
  };
  const offsets = [];
  const settle = () => new Promise(resolve => setImmediate(resolve));
  try {
    globalThis.window = { addEventListener() {} };
    globalThis.document = { getElementById: node, querySelectorAll: () => [] };
    globalThis.location = { origin, replace: () => assert.fail('unexpected login') };
    globalThis.localStorage = { getItem: () => JSON.stringify({ Servers: [{ ManualAddress: origin, AccessToken: 'token' }] }) };
    globalThis.FormData = class { *[Symbol.iterator]() { yield ['Limit', '24']; } };
    globalThis.fetch = async path => {
      if (!path.startsWith('/Jable/Catalog?')) return new Response('{}');
      offsets.push(Number(new URL(path, origin).searchParams.get('StartIndex')));
      return offsets.length === 2 ? new Response('', { status: 502 }) : Response.json({ Items: [], TotalRecordCount: 72 });
    };
    await import('../Jellyfin.Plugin.Jable/Web/jable.mjs?paging-regression');
    await settle();
    node('next').listeners.click();
    await settle();
    assert.equal(node('previous').disabled, true);
    assert.equal(node('next').disabled, false);
    assert.equal(node('page-info').textContent, '第 1 / 3 页');
    node('next').listeners.click();
    await settle();
    node('next').listeners.click();
    await settle();
    assert.deepEqual(offsets, [0, 24, 24, 48]);
  } finally {
    for (const [key, value] of Object.entries(saved)) {
      if (value === undefined) delete globalThis[key]; else globalThis[key] = value;
    }
  }
});

test('findCurrentServer selects a same-origin authenticated server, including normalized addresses', () => {
  const credentials = { Servers: [
    { Id: 'other', ManualAddress: 'http://other:8096', AccessToken: 'foreign' },
    { Id: 'missing-token', LocalAddress: origin },
    { Id: 'local', RemoteAddress: `${origin}/`, AccessToken: 'token' }
  ] };
  assert.equal(findCurrentServer(credentials, origin).Id, 'local');
  assert.equal(findCurrentServer({ Servers: [{ LocalAddress: 'HTTPS://LOCAL:443/jellyfin', AccessToken: 'x' }] }, 'https://local').AccessToken, 'x');
});

test('credentials fail closed for malformed, missing, remote or credentialed addresses', () => {
  for (const credentials of [null, {}, { Servers: null }, { Servers: {} }, { Servers: [null] },
    { Servers: [{ ManualAddress: `${origin}.evil`, AccessToken: 'x' }] },
    { Servers: [{ ManualAddress: '//192.0.2.10:8899', AccessToken: 'x' }] },
    { Servers: [{ ManualAddress: 'http://user:pass@192.0.2.10:8899', AccessToken: 'x' }] },
    { Servers: [{ ManualAddress: origin, AccessToken: ' ' }] }]) {
    assert.equal(findCurrentServer(credentials, origin), null);
  }
});

test('buildQuery encodes filters, boolean order and paging without forwarding arbitrary fields', () => {
  const query = new URLSearchParams(buildQuery({ Search: '演员 / &x=?', Source: 'Local', Actress: '甲', Genre: '剧情', Studio: 'Studio',
    ReleasedFrom: '2025-01-01', ReleasedTo: '2025-02-01', Sort: 'ViewCount', Descending: false, StartIndex: 50, Limit: 25, AccessToken: 'secret' }));
  assert.equal(query.get('Search'), '演员 / &x=?');
  assert.equal(query.get('Source'), 'Local');
  assert.equal(query.get('Actress'), '甲');
  assert.equal(query.get('Genre'), '剧情');
  assert.equal(query.get('Studio'), 'Studio');
  assert.equal(query.get('ReleasedFrom'), '2025-01-01');
  assert.equal(query.get('ReleasedTo'), '2025-02-01');
  assert.equal(query.get('Sort'), 'ViewCount');
  assert.equal(query.get('Descending'), 'false');
  assert.equal(query.get('StartIndex'), '50');
  assert.equal(query.get('Limit'), '25');
  assert.equal(query.has('AccessToken'), false);
});

test('API sends a token only to same-origin Jable paths and refuses redirect following', async () => {
  const calls = [];
  const api = createApi({ origin, token: 'secret', fetch: async (...args) => { calls.push(args); return new Response('{}'); } });
  await api('/Jable/Catalog?Search=hello');
  const [url, options] = calls[0];
  assert.equal(url, '/Jable/Catalog?Search=hello');
  assert.equal(options.headers['X-Emby-Token'], 'secret');
  assert.equal(options.redirect, 'error');
  for (const path of ['https://jable.tv/x', `${origin}/Jable/Status`, '//evil.test/Jable/Status', '/\\evil.test/Jable/Status',
    '/Jable/../Users/Me', '/Jable/%2e%2e/Users/Me', '/Users/Me', '/Jable/Status#fragment']) {
    await assert.rejects(api(path));
  }
  assert.equal(calls.length, 1);
});

test('missing token and 401 redirect to login while 403 reports permission without redirecting', async () => {
  const redirects = [];
  const denied = [];
  const redirect = path => redirects.push(path);
  const onForbidden = () => denied.push(true);
  const noToken = createApi({ origin, token: '', redirect, fetch: () => assert.fail('unexpected fetch') });
  await assert.rejects(noToken('/Jable/Status'));
  assert.deepEqual(redirects, ['/web/index.html']);
  redirects.length = 0;
  const unauthorized = createApi({ origin, token: 'expired', redirect, fetch: async () => new Response('', { status: 401 }) });
  await assert.rejects(unauthorized('/Jable/Status'));
  assert.deepEqual(redirects, ['/web/index.html']);
  redirects.length = 0;
  const forbidden = createApi({ origin, token: 'token', redirect, onForbidden, fetch: async () => new Response('', { status: 403 }) });
  await assert.rejects(forbidden('/Jable/Status'));
  assert.deepEqual(redirects, []);
  assert.deepEqual(denied, [true]);
});

test('cards use Jellyfin details for local items and trusted token-free canonical URLs for online items', () => {
  const server = { Id: 'server 1', AccessToken: 'secret' };
  assert.deepEqual(cardLink({ IsLocal: true, JellyfinItemId: 'item/1' }, server), {
    href: '/web/index.html#/details?id=item%2F1&serverId=server%201', target: '', rel: ''
  });
  assert.deepEqual(cardLink({ CanonicalUrl: 'https://jable.tv/videos/ssis-123/' }, server), {
    href: 'https://jable.tv/videos/ssis-123/', target: '_blank', rel: 'noopener noreferrer'
  });
  for (const CanonicalUrl of ['javascript:alert(1)', 'http://jable.tv/videos/x/', 'https://jable.tv.evil/videos/x/', 'https://token@jable.tv/videos/x/'])
    assert.equal(cardLink({ CanonicalUrl }, server), null);
});

test('image loader caps concurrency at four, authenticates by number and revokes replaced blobs', async () => {
  let active = 0;
  let maximum = 0;
  const fetched = [];
  const published = [];
  const revoked = [];
  const loader = loadImages(Array.from({ length: 9 }, (_, i) => ({ Number: `ABP-${i}`, ImagePath: 'https://evil.test/' })), {
    api: async path => {
      fetched.push(path);
      maximum = Math.max(maximum, ++active);
      await new Promise(resolve => setImmediate(resolve));
      active--;
      return new Response(new Blob(['image']));
    },
    createObjectURL: () => `blob:local-${published.length}`,
    revokeObjectURL: url => revoked.push(url),
    onImage: (item, url) => published.push([item.Number, url])
  });
  await loader.done;
  assert.equal(maximum, 4);
  assert.equal(published.length, 9);
  assert.equal(fetched[0], '/Jable/Images/ABP-0');
  assert.ok(fetched.every(path => path.startsWith('/Jable/Images/')));
  loader.dispose();
  assert.deepEqual(revoked, published.map(([, url]) => url));
  loader.dispose();
  assert.equal(revoked.length, 9);
});

test('replaced image batch aborts in-flight work and never publishes stale blobs', async () => {
  const resolvers = [];
  const signals = [];
  const published = [];
  const loader = loadImages(Array.from({ length: 12 }, (_, i) => ({ Number: `ABP-${i}` })), {
    api: (_path, options) => { signals.push(options.signal); return new Promise(resolve => resolvers.push(resolve)); },
    createObjectURL: () => assert.fail('stale blob created'), revokeObjectURL: () => {},
    onImage: (...args) => published.push(args)
  });
  assert.equal(signals.length, 4);
  loader.dispose();
  assert.ok(signals.every(signal => signal.aborted));
  resolvers.forEach(resolve => resolve(new Response(new Blob(['image']))));
  await loader.done;
  assert.equal(signals.length, 4);
  assert.deepEqual(published, []);
});

test('successive renders share the four-request limit while an aborted batch settles', async () => {
  let active = 0;
  let maximum = 0;
  const finish = [];
  const api = () => new Promise(resolve => {
    maximum = Math.max(maximum, ++active);
    finish.push(() => { active--; resolve(new Response(new Blob(['image']))); });
  });
  const options = { api, createObjectURL: () => 'blob:local', revokeObjectURL: () => {}, onImage: () => {} };
  const first = loadImages(Array.from({ length: 8 }, (_, i) => ({ Number: `ABP-${i}` })), options);
  const second = loadImages([{ Number: 'NEW-001' }], { ...options, previous: first });
  assert.equal(maximum, 4);
  finish.splice(0).forEach(resolve => resolve());
  await first.done;
  await new Promise(resolve => setImmediate(resolve));
  finish.splice(0).forEach(resolve => resolve());
  await second.done;
  assert.equal(maximum, 4);
  first.dispose();
  second.dispose();
});
