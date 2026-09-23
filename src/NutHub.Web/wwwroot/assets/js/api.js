// HTTP client for the NutHub API (docs/API.md). Paths are relative ("api/...") so the panel also works behind a
// reverse proxy that publishes it under a sub-path. Every non-GET request carries the anti-forgery header.
import { t, has } from './i18n.js';

/** An API failure with the server's error code ("validation", "forbidden"...) or "network" / "timeout". */
export class ApiError extends Error {
  constructor(status, code, message, fields = null, retryAfter = null) {
    super(message || code);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
    this.fields = fields;
    this.retryAfter = retryAfter;
  }
}

const problemListeners = new Set();

/**
 * Registers a listener for session problems (401 unauthorized, 403 passwordChangeRequired) seen on any request,
 * so the shell can send the user to the sign-in page from wherever the problem shows up.
 */
export function onSessionProblem(fn) {
  problemListeners.add(fn);
  return () => problemListeners.delete(fn);
}

function defaultCode(status) {
  if (status === 401) return 'unauthorized';
  if (status === 403) return 'forbidden';
  if (status === 404) return 'notFound';
  if (status === 409) return 'conflict';
  if (status === 429) return 'rateLimited';
  if (status >= 500) return 'internal';
  return 'badRequest';
}

/**
 * Sends a request and returns the parsed JSON (null for 204). Throws ApiError, or the AbortError of the caller's
 * signal. `timeout` in ms (0 disables it) guards against a server or proxy that never answers.
 */
export async function request(method, path, { body, signal, timeout = 20000, quiet = false } = {}) {
  const controller = new AbortController();
  let timedOut = false;
  const timer = timeout > 0 ? setTimeout(() => { timedOut = true; controller.abort(); }, timeout) : null;
  const onAbort = () => controller.abort();
  if (signal) {
    if (signal.aborted) controller.abort();
    else signal.addEventListener('abort', onAbort, { once: true });
  }

  const headers = { Accept: 'application/json' };
  if (method !== 'GET' && method !== 'HEAD') headers['X-NutHub-Request'] = '1';
  let payload;
  if (body !== undefined) {
    headers['Content-Type'] = 'application/json';
    payload = JSON.stringify(body);
  }

  let response;
  let text;
  try {
    response = await fetch(path, {
      method,
      headers,
      body: payload,
      credentials: 'same-origin',
      cache: 'no-store',
      redirect: 'follow',
      signal: controller.signal,
    });
    text = response.status === 204 ? '' : await response.text();
  } catch (err) {
    if (signal?.aborted) throw new DOMException('Aborted', 'AbortError');
    if (timedOut) throw new ApiError(0, 'timeout', 'The server did not answer in time.');
    throw new ApiError(0, 'network', err?.message || 'Network error');
  } finally {
    clearTimeout(timer);
    signal?.removeEventListener('abort', onAbort);
  }

  let data = null;
  if (text) {
    try {
      data = JSON.parse(text);
    } catch {
      data = null;
    }
  }
  if (response.ok) return data;

  const code = (data && typeof data.error === 'string' && data.error) || defaultCode(response.status);
  const retryHeader = Number(response.headers.get('Retry-After'));
  const retryAfter = (data && Number.isFinite(data.retryAfterSeconds) ? data.retryAfterSeconds : null)
    ?? (Number.isFinite(retryHeader) && retryHeader > 0 ? retryHeader : null);
  const error = new ApiError(response.status, code, data?.message ?? response.statusText,
    data?.fields ?? null, retryAfter);
  if (!quiet && (code === 'unauthorized' || code === 'passwordChangeRequired')) {
    for (const fn of problemListeners) {
      try {
        fn(error, path);
      } catch (e) {
        console.error(e);
      }
    }
  }
  throw error;
}

export const api = {
  get: (path, options) => request('GET', path, options),
  post: (path, body, options) => request('POST', path, { ...options, body }),
  put: (path, body, options) => request('PUT', path, { ...options, body }),
  del: (path, options) => request('DELETE', path, options),
};

/** True when the error is the caller's own cancellation (navigation away), which is never shown. */
export function isAbort(err) {
  return err?.name === 'AbortError';
}

/** A localised, human message for any error thrown by the API client. */
export function errorText(err) {
  if (!err) return t('error.unknown');
  if (err instanceof ApiError) {
    if (err.code === 'rateLimited' && err.retryAfter) return t('error.rateLimitedFor', { seconds: err.retryAfter });
    const key = `error.${err.code}`;
    if (has(key)) return t(key);
    return err.message || t('error.unknown');
  }
  return err.message || String(err);
}

/** The localised text of a command result status (instant commands, variable writes, notification tests). */
export function commandResultText(result) {
  if (!result) return t('error.unknown');
  const key = `cmdStatus.${result.status}`;
  const base = has(key) ? t(key) : String(result.status ?? '');
  return result.message && !result.ok ? `${base}: ${result.message}` : base;
}
