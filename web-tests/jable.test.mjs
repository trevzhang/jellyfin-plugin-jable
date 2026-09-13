import test from 'node:test';
import assert from 'node:assert/strict';
import { findCurrentServer, buildQuery, createApi, cardLink, loadImages } from '../Jellyfin.Plugin.Jable/Web/jable.mjs';

const origin = 'http://192.0.2.10:8899';

let pageTestId = 0;
async function withSyncPage(initialStatus, run, post = async () => new Response(null, { status: 202 }), catalogReply = async () => Response.json({ Items: [], TotalRecordCount: 0 })) {
  const keys = ['window', 'document', 'location', 'localStorage', 'FormData', 'fetch', 'setTimeout', 'clearTimeout'];
  const saved = Object.fromEntries(keys.map(key => [key, globalThis[key]]));
  const nodes = new Map();
  const node = id => {
    if (!nodes.has(id)) nodes.set(id, { hidden: id === 'admin-sync', listeners: {}, value: '', textContent: '',
      addEventListener(event, fn) { this.listeners[event] = fn; }, setAttribute() {}, removeAttribute(name) { delete this[name]; }, replaceChildren() {} });
    return nodes.get(id);
  };
  const calls = [], redirects = [], timers = new Map(), events = {};
  let status = initialStatus, timerId = 0;
  const settle = () => new Promise(resolve => setImmediate(resolve));
  try {
    globalThis.window = { addEventListener(event, fn) { events[event] = fn; } };
    globalThis.document = { getElementById: node, querySelectorAll: () => [] };
    globalThis.location = { origin, replace: path => redirects.push(path) };
    globalThis.localStorage = { getItem: () => JSON.stringify({ Servers: [{ ManualAddress: origin, AccessToken: 'synthetic-token' }] }) };
    globalThis.FormData = class { *[Symbol.iterator]() { yield ['Limit', '24']; } };
    globalThis.setTimeout = fn => { timers.set(++timerId, fn); return timerId; };
    globalThis.clearTimeout = id => timers.delete(id);
    globalThis.fetch = async (path, options) => {
      calls.push({ path, options });
      if (path === '/Jable/Sync') return post(options);
      if (path === '/Jable/Status') return typeof status === 'function' ? status(options) : Response.json(status);
      assert.ok(path.startsWith('/Jable/Catalog?'));
      return catalogReply();
    };
    await import(`../Jellyfin.Plugin.Jable/Web/jable.mjs?sync-test=${++pageTestId}`);
    await settle();
    await run({ node, calls, redirects, timers, events, settle, setStatus(value) { status = value; }, async tick() {
      assert.equal(timers.size, 1);
      const [id, fn] = timers.entries().next().value;
      timers.delete(id); fn(); await settle();
    } });
  } finally {
    events.pagehide?.();
    for (const [key, value] of Object.entries(saved)) {
      if (value === undefined) delete globalThis[key]; else globalThis[key] = value;
    }
  }
}

test('admin sync queues once, polls running task, then stops and refreshes catalog', async () => {
  let finishPost;
  const idle = { CanManage: true, IsSyncRunning: false, SyncTaskId: 'task /?&' };
  await withSyncPage(idle, async ({ node, calls, timers, settle, tick, setStatus }) => {
    assert.equal(node('admin-sync').hidden, false);
    assert.equal(node('sync-schedule').href, '/web/index.html#/dashboard/tasks/edit?id=task%20%2F%3F%26');
    assert.equal(node('sync-now').disabled, false);
    const catalogs = calls.filter(call => call.path.startsWith('/Jable/Catalog?')).length;
    node('sync-now').listeners.click();
    node('sync-now').listeners.click();
    assert.equal(node('sync-now').disabled, true);
    assert.match(node('sync-feedback').textContent, /排队|提交/);
    const posts = calls.filter(call => call.path === '/Jable/Sync');
    assert.equal(posts.length, 1);
    assert.equal(posts[0].options.method, 'POST');
    assert.equal(posts[0].options.headers['X-Emby-Token'], 'synthetic-token');
    assert.equal(posts[0].options.redirect, 'error');
    finishPost(); await settle();
    assert.equal(node('sync-now').disabled, true);
    setStatus({ ...idle, IsSyncRunning: true });
    await tick();
    assert.match(node('sync-feedback').textContent, /正在同步/);
    assert.equal(node('sync-now').disabled, true);
    setStatus({ ...idle, LastSuccessfulSync: '2026-09-14T00:00:00Z' });
    await tick();
    assert.equal(timers.size, 0);
    assert.equal(node('sync-now').disabled, false);
    assert.match(node('sync-feedback').textContent, /完成/);
    assert.ok(calls.filter(call => call.path.startsWith('/Jable/Catalog?')).length > catalogs);
  }, () => new Promise(resolve => { finishPost = () => resolve(new Response(null, { status: 202 })); }));
});

test('ordinary users never see management controls or poll task state', async () => {
  await withSyncPage({ CanManage: false, IsSyncRunning: false, SyncTaskId: '' }, async ({ node, timers, calls }) => {
    assert.equal(node('admin-sync').hidden, true);
    node('sync-now').listeners.click?.();
    assert.equal(calls.filter(call => call.path === '/Jable/Sync').length, 0);
    assert.equal(timers.size, 0);
  });
});

test('sync completion retries initial failed catalog with the current filters', async () => {
  const idle = { CanManage: true, IsSyncRunning: false, SyncTaskId: 'task' };
  let attempts = 0;
  await withSyncPage({ ...idle, IsSyncRunning: true }, async ({ calls, tick, setStatus }) => {
    setStatus(idle);
    await tick();
    const catalogs = calls.filter(call => call.path.startsWith('/Jable/Catalog?'));
    assert.equal(new URL(catalogs.at(-1).path, origin).searchParams.get('Limit'), '24');
  }, undefined, async () => ++attempts === 1 ? new Response(null, { status: 502 }) : Response.json({ Items: [], TotalRecordCount: 0 }));
});

test('sync completion reports catalog error and hides unavailable schedule link', async () => {
  await withSyncPage({ CanManage: true, IsSyncRunning: true, SyncTaskId: '' }, async ({ node, tick, setStatus, timers }) => {
    assert.equal(node('sync-schedule').hidden, true);
    setStatus({ CanManage: true, IsSyncRunning: false, SyncTaskId: '', LastError: 'challenge' });
    await tick();
    assert.match(node('sync-feedback').textContent, /错误/);
    assert.equal(timers.size, 0);
    assert.equal(node('sync-now').disabled, false);
  });
});

test('running sync is disabled on entry and pagehide clears polling and aborts requests', async () => {
  await withSyncPage({ CanManage: true, IsSyncRunning: true, SyncTaskId: 'task' }, async ({ node, timers, calls, events }) => {
    assert.equal(node('sync-now').disabled, true);
    assert.equal(timers.size, 1);
    events.pagehide();
    assert.equal(timers.size, 0);
    assert.ok(calls.every(call => call.options.signal.aborted));
  });
});

for (const status of [401, 403, 502]) test(`sync POST ${status} fails safely without credential text`, async () => {
  await withSyncPage({ CanManage: true, IsSyncRunning: false, SyncTaskId: 'task' }, async ({ node, timers, redirects, settle }) => {
    assert.equal(typeof node('sync-now').listeners.click, 'function');
    node('sync-now').listeners.click(); await settle();
    assert.equal(timers.size, 0);
    assert.ok(!node('sync-feedback').textContent.includes('synthetic-token'));
    if (status === 401) assert.deepEqual(redirects, ['/web/index.html']);
    else if (status === 403) assert.equal(node('admin-sync').hidden, true);
    else { assert.equal(node('sync-now').disabled, false); assert.match(node('sync-feedback').textContent, /失败/); }
  }, async () => new Response('synthetic-token', { status }));
});

test('pagehide aborts queued sync POST and ignores its late success', async () => {
  let finishPost;
  await withSyncPage({ CanManage: true, IsSyncRunning: false, SyncTaskId: 'task' }, async ({ node, calls, events, timers, settle }) => {
    assert.equal(typeof node('sync-now').listeners.click, 'function');
    node('sync-now').listeners.click();
    const post = calls.find(call => call.path === '/Jable/Sync');
    events.pagehide();
    assert.equal(post.options.signal.aborted, true);
    finishPost(); await settle();
    assert.equal(timers.size, 0);
  }, () => new Promise(resolve => { finishPost = () => resolve(new Response(null, { status: 202 })); }));
});

for (const lateStatus of [202, 401, 403, 502]) test(`persisted pageshow restores interaction and ignores aborted POST ${lateStatus}`, async () => {
  const finish = [];
  const idle = { CanManage: true, IsSyncRunning: false, SyncTaskId: 'task' };
  await withSyncPage(idle, async ({ node, calls, events, timers, settle, redirects }) => {
    node('sync-now').listeners.click();
    const oldPost = calls.find(call => call.path === '/Jable/Sync');
    events.pagehide({ persisted: true });
    assert.ok(oldPost.options.signal.aborted);
    assert.equal(timers.size, 0);
    const before = calls.length;
    events.pageshow?.({ persisted: true });
    await settle();
    assert.ok(calls.slice(before).some(call => call.path === '/Jable/Status'), 'BFCache restore must reload status');
    assert.ok(calls.slice(before).some(call => call.path.startsWith('/Jable/Catalog?')), 'BFCache restore must reload catalog');
    assert.equal(node('sync-now').disabled, false);
    node('sync-now').listeners.click();
    assert.equal(calls.filter(call => call.path === '/Jable/Sync').length, 2);
    finish[0](lateStatus); await settle();
    assert.deepEqual(redirects, [], 'aborted response must not redirect the restored page');
    assert.equal(node('admin-sync').hidden, false);
    assert.equal(node('sync-now').disabled, true);
    assert.equal(timers.size, 0, 'pre-navigation POST must not schedule polling after restoration');
    finish[1](); await settle();
    assert.equal(timers.size, 1);
    const catalogCount = calls.filter(call => call.path.startsWith('/Jable/Catalog?')).length;
    node('filters').listeners.submit({ preventDefault() {} }); await settle();
    assert.ok(calls.filter(call => call.path.startsWith('/Jable/Catalog?')).length > catalogCount);
    events.pagehide({ persisted: false });
    assert.equal(timers.size, 0);
    const departed = calls.length;
    events.pageshow?.({ persisted: false });
    node('sync-now').listeners.click();
    node('filters').listeners.submit({ preventDefault() {} }); await settle();
    assert.equal(calls.length, departed, 'non-persisted departure stays closed');
  }, () => new Promise(resolve => finish.push((status = 202) => resolve(new Response(null, { status })))));
});

test('persisted pageshow restores ordinary user browsing without management controls', async () => {
  await withSyncPage({ CanManage: false, IsSyncRunning: false, SyncTaskId: '' }, async ({ node, calls, events, settle, timers }) => {
    events.pagehide({ persisted: true });
    const before = calls.length;
    events.pageshow?.({ persisted: true }); await settle();
    node('filters').listeners.submit({ preventDefault() {} }); await settle();
    assert.ok(calls.slice(before).some(call => call.path.startsWith('/Jable/Catalog?')));
    assert.ok(calls.slice(before).some(call => call.path === '/Jable/Status'));
    assert.equal(node('admin-sync').hidden, true);
    assert.equal(timers.size, 0);
    assert.equal(calls.filter(call => call.path === '/Jable/Sync').length, 0);
  });
});

test('concurrent Status failures keep a pending sync POST locked against a second POST', async () => {
  let finishCatalog, finishPost;
  let catalogs = 0;
  await withSyncPage({ CanManage: true, IsSyncRunning: false, SyncTaskId: 'task' }, async ({ node, calls, setStatus, settle, events }) => {
    node('sync-now').listeners.click();
    setStatus(() => new Response(null, { status: 502 }));
    finishCatalog(); await settle();
    assert.equal(node('sync-now').disabled, true, 'Status failure must not unlock the pending POST');
    node('sync-now').listeners.click();
    node('filters').listeners.submit({ preventDefault() {} }); await settle();
    assert.equal(node('sync-now').disabled, true);
    assert.equal(calls.filter(call => call.path === '/Jable/Sync').length, 1);
    // The in-flight lock also guards the handler itself, independent of the DOM disabled flag.
    node('sync-now').disabled = false;
    node('sync-now').listeners.click();
    assert.equal(calls.filter(call => call.path === '/Jable/Sync').length, 1);
    events.pagehide({ persisted: false });
    finishPost(); await settle();
  }, () => new Promise(resolve => { finishPost = () => resolve(new Response(null, { status: 202 })); }),
  () => ++catalogs === 1 ? new Promise(resolve => { finishCatalog = () => resolve(Response.json({ Items: [], TotalRecordCount: 0 })); })
    : Response.json({ Items: [], TotalRecordCount: 0 }));
});

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
