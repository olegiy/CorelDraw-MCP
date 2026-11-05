import type { components } from './api.generated';
export type Run = components['schemas']['RunSnapshot'];
export type DocumentInfo = components['schemas']['DocumentInfo'];
export type Documents = components['schemas']['DocumentsCache'];
export type Preview = components['schemas']['ImageData'];
let csrf = '';
let session: Promise<void> | null = null;

export function initializeSession(): Promise<void> {
  if (session) return session;
  const fragment = new URLSearchParams(location.hash.slice(1));
  const bootstrap = fragment.get('bootstrap');
  if (location.hash) history.replaceState(null, '', location.pathname + location.search);
  session = (async () => {
    const response = await fetch(`/api/v1/access/${bootstrap ? 'bootstrap' : 'session'}`, {
      method: bootstrap ? 'POST' : 'GET', credentials: 'same-origin',
      headers: bootstrap ? { 'X-Corel-Bootstrap': bootstrap, 'Content-Type': 'application/json' } : {},
      ...(bootstrap ? { body: '{}' } : {}),
    });
    const data = await response.json();
    if (!response.ok) throw new Error(data.message ?? 'Откройте панель через локальный launcher.');
    csrf = typeof data.csrf_token === 'string' ? data.csrf_token : '';
  })();
  return session;
}

export async function api<T>(path: string, options: RequestInit = {}): Promise<T> {
  await initializeSession();
  const response = await fetch(`/api/v1${path}`, { ...options, credentials: 'same-origin',
    headers: { 'Content-Type': 'application/json', ...(options.method && options.method !== 'GET' ? { 'X-Corel-CSRF': csrf } : {}), ...options.headers } });
  const data = await response.json();
  if (!response.ok) throw new Error(data.message ?? 'Запрос панели завершился ошибкой.');
  return data as T;
}

export const terminal = (status: string) => ['completed', 'stopped', 'failed', 'uncertain', 'halted', 'interrupted', 'rejected', 'limit_reached'].includes(status);
