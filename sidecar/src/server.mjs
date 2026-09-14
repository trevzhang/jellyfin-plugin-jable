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
