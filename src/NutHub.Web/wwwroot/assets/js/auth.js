// Who is using the panel: the answer of GET /api/auth/state, kept current and shared by every module.
import { api } from './api.js';

const ROLE_RANK = { viewer: 1, operator: 2, admin: 3 };

export const auth = {
  loaded: false,
  authenticated: false,
  user: null,
  anonymousRead: false,
  serverName: 'NutHub',
  version: '',
  signedOutByUser: false,
};

const listeners = new Set();
let pending = null;

function signature(a) {
  return JSON.stringify([a.authenticated, a.user?.name, a.user?.role, a.user?.mustChangePassword,
    a.user?.displayName, a.anonymousRead, a.serverName]);
}

function apply(state) {
  const before = signature(auth);
  auth.loaded = true;
  auth.authenticated = !!state?.authenticated;
  auth.user = state?.authenticated ? state.user ?? null : null;
  auth.anonymousRead = !!state?.anonymousRead;
  if (state?.serverName) auth.serverName = state.serverName;
  if (state?.version) auth.version = state.version;
  if (before !== signature(auth)) {
    for (const fn of listeners) fn(auth);
  }
}

/** Registers a listener for changes of the signed-in user, role or anonymous access; returns unsubscribe. */
export function onAuthChange(fn) {
  listeners.add(fn);
  return () => listeners.delete(fn);
}

/** Reloads the authentication state (concurrent calls share one request). */
export function refreshAuth() {
  if (!pending) {
    pending = api.get('api/auth/state', { quiet: true, timeout: 10000 })
      .then((state) => {
        apply(state);
        return auth;
      })
      .finally(() => {
        pending = null;
      });
  }
  return pending;
}

/** Signs in; throws ApiError (invalidCredentials, rateLimited...). */
export async function login(username, password) {
  await api.post('api/auth/login', { username, password }, { quiet: true });
  return refreshAuth();
}

/** Signs out; the local state is cleared even when the request fails (the cookie may already be gone). */
export async function logout() {
  // Lets listeners tell a deliberate sign-out from an expired session.
  auth.signedOutByUser = true;
  try {
    await api.post('api/auth/logout', undefined, { quiet: true });
  } finally {
    await refreshAuth().catch(() => apply({ authenticated: false, anonymousRead: auth.anonymousRead }));
  }
}

/** Whether the current visitor may use the read-only views. */
export function canRead() {
  return auth.authenticated ? !auth.user?.mustChangePassword : auth.anonymousRead;
}

/** Whether the signed-in user has at least the given role ("viewer", "operator", "admin"). */
export function hasRole(min) {
  if (!auth.authenticated || !auth.user) return min === 'anonymous' ? auth.anonymousRead : false;
  if (auth.user.mustChangePassword) return false;
  return (ROLE_RANK[auth.user.role] ?? 0) >= (ROLE_RANK[min] ?? 99);
}

/** Display name of the signed-in user. */
export function displayName() {
  return auth.user?.displayName || auth.user?.name || '';
}
