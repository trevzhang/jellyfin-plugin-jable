const MAX_HTML_BYTES = 4 * 1024 * 1024;

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

  async render(value, signal) {
    if (!isAllowedJableUrl(value)) throw new Error('Jable URL is not allowed.');
    const timeout = new AbortController();
    const timeoutTimer = setTimeout(() => timeout.abort(), this.timeoutMs);
    const combined = signal ? AbortSignal.any([signal, timeout.signal]) : timeout.signal;
    try {
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
        if (message.method === 'Network.loadingFinished') pendingRequests.delete(message.params.requestId);
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
            expression: 'JSON.stringify({readyState:document.readyState,url:location.href,html:document.documentElement.outerHTML})',
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
        void this.fetch(`${this.browserUrl}/json/close/${encodeURIComponent(target.id)}`, {
          signal: AbortSignal.timeout(this.timeoutMs)
        }).catch(() => {});
      }
    } finally {
      clearTimeout(timeoutTimer);
    }
  }
}
