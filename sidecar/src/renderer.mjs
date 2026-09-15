import { lookup } from 'node:dns/promises';
import { isIP } from 'node:net';

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
  constructor({ browserUrl, fetchImpl = fetch, WebSocketImpl = WebSocket, lookupImpl = lookup, timeoutMs = 60_000 }) {
    this.browserUrl = new URL(browserUrl).origin;
    this.fetch = fetchImpl;
    this.WebSocket = WebSocketImpl;
    this.lookup = lookupImpl;
    this.timeoutMs = timeoutMs;
  }

  async resolveBrowserUrl(signal) {
    signal?.throwIfAborted();
    const url = new URL(this.browserUrl);
    const hostname = url.hostname.replace(/^\[|\]$/g, '');
    if (!isIP(hostname)) {
      let abort;
      const { address } = await new Promise((resolve, reject) => {
        abort = () => reject(signal.reason);
        signal?.addEventListener('abort', abort, { once: true });
        Promise.resolve(this.lookup(hostname)).then(resolve, reject);
      }).finally(() => signal?.removeEventListener('abort', abort));
      url.hostname = isIP(address) === 6 ? `[${address}]` : address;
    }
    signal?.throwIfAborted();
    return url;
  }

  async health(signal) {
    const browserUrl = await this.resolveBrowserUrl(signal);
    const response = await this.fetch(`${browserUrl.origin}/json/version`, { signal });
    if (!response.ok) throw new Error(`Chromium health returned ${response.status}.`);
    const version = await response.json();
    if (!version.webSocketDebuggerUrl) throw new Error('Chromium health omitted webSocketDebuggerUrl.');
  }

  async render(value, signal) {
    if (!isAllowedJableUrl(value)) throw new Error('Jable URL is not allowed.');
    const timeout = new AbortController();
    const interceptionFailure = new AbortController();
    const timeoutTimer = setTimeout(() => timeout.abort(new DOMException('Jable page did not finish loading.', 'TimeoutError')), this.timeoutMs);
    const combined = AbortSignal.any([timeout.signal, interceptionFailure.signal, ...(signal ? [signal] : [])]);
    try {
      const browserUrl = await this.resolveBrowserUrl(combined);
      const created = await this.fetch(
        `${browserUrl.origin}/json/new?${encodeURIComponent('about:blank')}`,
        { method: 'PUT', signal: combined }
      );
      if (!created.ok) throw new Error(`Chromium target creation returned ${created.status}.`);
      const target = await created.json();
      if (!target.id || !target.webSocketDebuggerUrl) throw new Error('Chromium target response is invalid.');
      const pendingRequests = new Map();
      const failedHosts = new Set();
      let cdp;
      let mainFrameId;
      let unsafeNavigation;
      try {
        const socketUrl = new URL(target.webSocketDebuggerUrl);
        socketUrl.protocol = browserUrl.protocol === 'https:' ? 'wss:' : 'ws:';
        socketUrl.hostname = browserUrl.hostname;
        socketUrl.port = browserUrl.port;
        cdp = connect(this.WebSocket, socketUrl.href, combined, message => {
          if (message.method === 'Fetch.requestPaused') {
            const { requestId, request, resourceType, frameId } = message.params;
            const topLevel = resourceType === 'Document' && frameId === mainFrameId;
            const allowed = isAllowedJableUrl(request.url)
              || (!topLevel && /^(about:|data:|blob:)/.test(request.url));
            if (topLevel && !allowed) unsafeNavigation = new Error('Chromium unsafe navigation: URL is not allowed.');
            void cdp.command(allowed ? 'Fetch.continueRequest' : 'Fetch.failRequest',
              allowed ? { requestId } : { requestId, errorReason: 'BlockedByClient' }
            ).then(() => {
              if (unsafeNavigation) interceptionFailure.abort(unsafeNavigation);
            }).catch(error => interceptionFailure.abort(error));
          }
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
        await cdp.command('Network.enable');
        const { frameTree } = await cdp.command('Page.getFrameTree');
        mainFrameId = frameTree.frame.id;
        await cdp.command('Fetch.enable', { patterns: [{ urlPattern: '*', requestStage: 'Request' }] });
        const navigation = await cdp.command('Page.navigate', { url: value });
        if (unsafeNavigation) throw unsafeNavigation;
        if (navigation.errorText) throw new Error(`Chromium navigation failed: ${navigation.errorText}.`);
        while (true) {
          combined.throwIfAborted();
          const result = await cdp.command('Runtime.evaluate', {
            expression: 'JSON.stringify({readyState:document.readyState,url:location.href,html:document.documentElement.outerHTML})',
            returnByValue: true
          });
          if (unsafeNavigation) throw unsafeNavigation;
          combined.throwIfAborted();
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
        if (unsafeNavigation) throw unsafeNavigation;
        if (interceptionFailure.signal.aborted) throw interceptionFailure.signal.reason;
        if (combined.aborted) {
          const hosts = [...new Set([...failedHosts, ...pendingRequests.values()])].sort();
          throw new DOMException(
            `Jable page did not finish loading${hosts.length ? `; pending hosts: ${hosts.join(', ')}` : ''}.`,
            'TimeoutError'
          );
        }
        throw error;
      } finally {
        cdp?.close();
        void this.fetch(`${browserUrl.origin}/json/close/${encodeURIComponent(target.id)}`, {
          signal: AbortSignal.timeout(this.timeoutMs)
        }).catch(() => {});
      }
    } finally {
      clearTimeout(timeoutTimer);
    }
  }
}
