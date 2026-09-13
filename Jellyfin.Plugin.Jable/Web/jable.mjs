export function findCurrentServer(credentials, origin) {
  if (!Array.isArray(credentials?.Servers)) return null;
  let current;
  try { current = new URL(origin).origin; } catch { return null; }
  return credentials.Servers.find(server =>
    typeof server?.AccessToken === 'string' && server.AccessToken.trim().length > 0 &&
    ['LocalAddress', 'ManualAddress', 'RemoteAddress'].some(key => {
      try {
        const address = new URL(server[key]);
        return ['http:', 'https:'].includes(address.protocol) && !address.username && !address.password && address.origin === current;
      } catch { return false; }
    })) ?? null;
}

export function buildQuery(filters) {
  const query = new URLSearchParams();
  for (const key of ['Search', 'Source', 'Actress', 'Genre', 'Studio', 'ReleasedFrom', 'ReleasedTo', 'Sort', 'Descending', 'StartIndex', 'Limit']) {
    if (filters[key] !== undefined && filters[key] !== null && filters[key] !== '') query.set(key, String(filters[key]));
  }
  return query.toString();
}

export function createApi({ origin, token, fetch: fetcher = globalThis.fetch, redirect = () => {}, onForbidden = () => {} }) {
  return async (path, { signal, method = 'GET' } = {}) => {
    const url = new URL(path, origin);
    if (typeof path !== 'string' || !path.startsWith('/Jable/') || path.includes('\\') || url.origin !== new URL(origin).origin ||
        !url.pathname.startsWith('/Jable/') || url.hash) throw new Error('API address must be a relative same-origin Jable URL.');
    if (typeof token !== 'string' || !token.trim()) {
      redirect('/web/index.html');
      throw new Error('请先登录 Jellyfin。');
    }
    const response = await fetcher(url.pathname + url.search, {
      method, signal, headers: { 'X-Emby-Token': token }, credentials: 'same-origin', redirect: 'error'
    });
    signal?.throwIfAborted();
    if (response.status === 401) redirect('/web/index.html');
    if (response.status === 403) onForbidden();
    if (!response.ok) throw Object.assign(new Error(`请求失败（HTTP ${response.status}）`), { status: response.status });
    return response;
  };
}

export function cardLink(item, server) {
  if (item.IsLocal && item.JellyfinItemId) return {
    href: `/web/index.html#/details?id=${encodeURIComponent(item.JellyfinItemId)}&serverId=${encodeURIComponent(server.Id ?? '')}`,
    target: '', rel: ''
  };
  try {
    const url = new URL(item.CanonicalUrl);
    if (url.protocol !== 'https:' || url.username || url.password ||
        !(url.hostname === 'jable.tv' || url.hostname.endsWith('.jable.tv'))) return null;
    return { href: url.href, target: '_blank', rel: 'noopener noreferrer' };
  } catch { return null; }
}

export function loadImages(items, { api, onImage, previous, createObjectURL = URL.createObjectURL, revokeObjectURL = URL.revokeObjectURL }) {
  const controller = new AbortController();
  const urls = new Set();
  let next = 0;
  async function worker() {
    while (!controller.signal.aborted && next < items.length) {
      const item = items[next++];
      try {
        const response = await api(`/Jable/Images/${encodeURIComponent(item.Number)}`, { signal: controller.signal });
        const blob = await response.blob();
        if (controller.signal.aborted) return;
        const url = createObjectURL(blob);
        urls.add(url);
        onImage(item, url);
      } catch (error) {
        if (error.status === 401 || error.status === 403) controller.abort();
        // A failed poster keeps its text placeholder; the catalog remains usable.
      }
    }
  }
  previous?.dispose();
  const run = () => Promise.all(Array.from({ length: Math.min(4, items.length) }, worker));
  return {
    done: previous ? previous.done.then(run) : run(),
    dispose() {
      controller.abort();
      for (const url of urls) revokeObjectURL(url);
      urls.clear();
    }
  };
}

function start() {
  const byId = id => document.getElementById(id);
  const permission = byId('permission');
  const catalog = byId('catalog');
  const form = byId('filters');
  const message = byId('message');
  let credentials;
  try { credentials = JSON.parse(localStorage.getItem('jellyfin_credentials')); } catch { credentials = null; }
  const server = findCurrentServer(credentials, location.origin);
  if (!server) { location.replace('/web/index.html'); return; }

  let source = 'All';
  let startIndex = 0;
  let successfulStartIndex = 0;
  let total = 0;
  let pageSize = 24;
  let appliedFilters;
  let request;
  let images;
  let statusRequest;
  let syncRequest;
  let statusTimer;
  let canManage = false;
  let syncRequested = false;
  let syncPosting = false;
  let syncRunning = false;
  let closed = false;
  let denied = false;

  function stopRequests() {
    clearTimeout(statusTimer);
    request?.abort();
    statusRequest?.abort();
    syncRequest?.abort();
    images?.dispose();
  }

  function pollStatus() {
    clearTimeout(statusTimer);
    if (!closed && !denied) statusTimer = setTimeout(() => { void refreshStatus(); }, 2000);
  }

  const api = createApi({
    origin: location.origin, token: server.AccessToken,
    redirect: path => { closed = true; stopRequests(); location.replace(path); },
    onForbidden: () => {
      denied = true;
      stopRequests();
      byId('admin-sync').hidden = true;
      byId('sync-feedback').textContent = '';
      catalog.replaceChildren();
      byId('controls').hidden = true;
      byId('paging').hidden = true;
      byId('sync-status').textContent = '';
      byId('result-count').textContent = '';
      message.textContent = '';
      permission.hidden = false;
      permission.textContent = '无权访问 Jable 目录。请让管理员选择媒体库，并授予当前 Jellyfin 用户该媒体库的访问权限。';
    }
  });

  function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function render(items) {
    images?.dispose();
    const posters = new Map();
    const cards = items.map(item => {
      const card = element('article', 'card');
      const destination = cardLink(item, server);
      const link = element(destination ? 'a' : 'div', 'card-link');
      if (destination) {
        link.href = destination.href;
        if (destination.target) { link.target = destination.target; link.rel = destination.rel; }
        link.setAttribute('aria-label', `${item.Title || item.Number}，${item.IsLocal ? '在 Jellyfin 查看' : '在 Jable 打开新窗口'}`);
      }
      const poster = element('div', 'poster');
      const placeholder = element('span', 'poster-placeholder', item.Number);
      placeholder.setAttribute('aria-hidden', 'true');
      const image = element('img');
      image.alt = '';
      image.decoding = 'async';
      image.hidden = true;
      image.addEventListener('load', () => { image.hidden = false; });
      image.addEventListener('error', () => { image.hidden = true; });
      const badge = element('span', `source-badge${item.IsLocal ? ' local' : ''}`, item.IsLocal ? '本地' : '在线 ↗');
      poster.append(placeholder, image, badge);
      posters.set(item, image);
      link.append(poster, element('h3', 'card-title', item.Title || item.Number));
      card.append(link);
      const date = item.ReleaseDate ? new Date(item.ReleaseDate) : null;
      const info = [item.Number, date && !Number.isNaN(date.getTime()) ? date.toLocaleDateString() : null, item.Studio].filter(Boolean);
      card.append(element('p', 'card-meta', info.join(' · ')));
      if (item.Actresses?.length) card.append(element('p', 'card-meta', item.Actresses.join(' / ')));
      const counts = element('p', 'card-counts');
      for (const [label, value] of [['浏览', item.ViewCount], ['收藏', item.FavoriteCount]]) {
        if (value !== null && value !== undefined) counts.append(element('span', '', `${label} ${Number(value).toLocaleString()}`));
      }
      card.append(counts);
      return card;
    });
    catalog.replaceChildren(...cards);
    images = loadImages(items, { api, previous: images, onImage: (item, url) => { posters.get(item).src = url; } });
  }

  async function refreshStatus() {
    if (denied || closed) return;
    clearTimeout(statusTimer);
    statusRequest?.abort();
    statusRequest = new AbortController();
    const current = statusRequest;
    try {
      const status = await (await api('/Jable/Status', { signal: current.signal })).json();
      if (denied || closed || current !== statusRequest || current.signal.aborted) return;
      const lines = [status.LastSuccessfulSync ? `缓存更新于 ${new Date(status.LastSuccessfulSync).toLocaleString()}` : '尚无成功同步记录'];
      if (status.IsRecovered) lines.push('正在使用恢复的目录缓存');
      if (status.LastError) lines.push(`最近同步或搜索失败：${status.LastError}`);
      byId('sync-status').textContent = lines.join(' · ');
      canManage = status.CanManage === true;
      byId('admin-sync').hidden = !canManage;
      byId('sync-schedule').hidden = !canManage || !status.SyncTaskId;
      byId('sync-schedule').href = canManage && status.SyncTaskId
        ? `/web/index.html#/dashboard/tasks/edit?id=${encodeURIComponent(status.SyncTaskId)}` : '';
      const completed = canManage && (syncRequested || syncRunning) && !status.IsSyncRunning && !syncPosting;
      syncRunning = canManage && status.IsSyncRunning === true;
      if (completed || !canManage) syncRequested = false;
      byId('sync-now').disabled = !canManage || syncRequested || syncPosting || syncRunning;
      if (syncRunning) {
        byId('sync-feedback').textContent = '正在同步…';
        pollStatus();
      } else if (completed) {
        byId('sync-feedback').textContent = status.LastError ? '同步结束，请查看上方错误信息。' : '同步已完成。';
        void loadCatalog(appliedFilters === undefined);
      }
    } catch (error) {
      if (denied || closed || current !== statusRequest || current.signal.aborted || error.name === 'AbortError' || error.status === 401) return;
      byId('sync-status').textContent = '暂时无法读取同步状态；已有目录仍可浏览。';
      if (!syncPosting && (syncRequested || syncRunning)) {
        syncRequested = syncRunning = false;
        byId('sync-now').disabled = !canManage;
        byId('sync-feedback').textContent = '读取同步状态失败，请稍后重试。';
      }
    }
  }

  byId('sync-now').addEventListener('click', async () => {
    if (closed || denied || !canManage || syncPosting || byId('sync-now').disabled) return;
    clearTimeout(statusTimer);
    statusRequest?.abort();
    syncRequested = syncPosting = true;
    byId('sync-now').disabled = true;
    byId('sync-feedback').textContent = '正在提交同步任务…';
    syncRequest = new AbortController();
    const current = syncRequest;
    try {
      await api('/Jable/Sync', { method: 'POST', signal: current.signal });
      if (closed || denied || current !== syncRequest || current.signal.aborted) return;
      syncPosting = false;
      byId('sync-feedback').textContent = '同步任务已排队…';
      pollStatus();
    } catch (error) {
      if (closed || denied || current !== syncRequest || current.signal.aborted || error.name === 'AbortError') return;
      syncRequested = syncPosting = false;
      byId('sync-now').disabled = !canManage;
      byId('sync-feedback').textContent = '提交同步失败，请稍后重试。';
    }
  });

  function readFilters() {
    const filters = Object.fromEntries(new FormData(form));
    filters.Source = source;
    filters.StartIndex = startIndex;
    filters.Descending = filters.Descending !== 'false';
    for (const [key, time] of [['ReleasedFrom', '00:00:00.000'], ['ReleasedTo', '23:59:59.999']]) {
      if (filters[key]) filters[key] = new Date(`${filters[key]}T${time}`).toISOString();
    }
    return filters;
  }

  function updatePaging() {
    byId('previous').disabled = startIndex === 0;
    byId('next').disabled = startIndex + pageSize >= total;
    byId('page-info').textContent = `第 ${Math.floor(startIndex / pageSize) + 1} / ${Math.max(1, Math.ceil(total / pageSize))} 页`;
  }

  async function loadCatalog(applyFilters = true) {
    if (denied || closed) return;
    request?.abort();
    images?.dispose();
    request = new AbortController();
    const current = request;
    catalog.setAttribute('aria-busy', 'true');
    byId('previous').disabled = true;
    byId('next').disabled = true;
    message.textContent = '正在读取目录…';
    message.className = 'notice';
    try {
      const filters = applyFilters ? readFilters() : { ...appliedFilters, Source: source, StartIndex: startIndex };
      const page = await (await api(`/Jable/Catalog?${buildQuery(filters)}`, { signal: current.signal })).json();
      if (current !== request || current.signal.aborted || denied || closed) return;
      pageSize = Number(filters.Limit);
      appliedFilters = filters;
      successfulStartIndex = startIndex;
      total = page.TotalRecordCount;
      render(page.Items);
      byId('result-count').textContent = `${total.toLocaleString()} 部作品`;
      byId('catalog-title').textContent = { All: '全部作品', Local: '本地作品', Online: '在线作品' }[source];
      message.textContent = page.Items.length ? '' : '没有找到符合条件的作品。可调整筛选，或由管理员同步最近目录。';
      updatePaging();
    } catch (error) {
      if (current !== request || current.signal.aborted || denied || error.status === 401) return;
      message.className = 'notice error';
      message.textContent = '暂时无法读取目录，请稍后重试。';
      startIndex = successfulStartIndex;
      updatePaging();
    } finally {
      if (current === request) { catalog.setAttribute('aria-busy', 'false'); void refreshStatus(); }
    }
  }

  form.addEventListener('submit', event => {
    event.preventDefault();
    const from = byId('released-from').value;
    const to = byId('released-to').value;
    if (from && to && from > to) {
      message.className = 'notice error';
      message.textContent = '起始日期不能晚于截止日期。';
      byId('released-to').focus();
      return;
    }
    startIndex = 0;
    void loadCatalog();
  });
  form.addEventListener('reset', () => { queueMicrotask(() => { startIndex = 0; void loadCatalog(); }); });
  document.querySelectorAll('[data-source]').forEach(button => button.addEventListener('click', () => {
    source = button.dataset.source;
    document.querySelectorAll('[data-source]').forEach(tab => tab.setAttribute('aria-pressed', String(tab === button)));
    startIndex = 0;
    void loadCatalog(appliedFilters === undefined);
  }));
  byId('previous').addEventListener('click', () => { startIndex = Math.max(0, startIndex - pageSize); void loadCatalog(false); });
  byId('next').addEventListener('click', () => { startIndex += pageSize; void loadCatalog(false); });
  window.addEventListener('pagehide', () => { closed = true; stopRequests(); });
  window.addEventListener('pageshow', event => {
    if (!event.persisted || !closed) return;
    closed = false;
    canManage = syncRequested = syncPosting = syncRunning = false;
    byId('sync-now').disabled = true;
    byId('sync-feedback').textContent = '';
    void refreshStatus();
    void loadCatalog(appliedFilters === undefined);
  });
  void refreshStatus();
  void loadCatalog();
}

if (typeof window !== 'undefined') start();
