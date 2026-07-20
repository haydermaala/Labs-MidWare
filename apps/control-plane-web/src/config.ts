// Runtime configuration. The API origin is a build-time variable so the console
// can be pointed at development, staging, or production without code changes.
// At launch (Phase H) the console is served same-origin with the API, and this
// falls back to the current origin.

const fromEnv = import.meta.env.VITE_API_BASE_URL as string | undefined;

export const API_BASE: string =
  fromEnv && fromEnv.length > 0
    ? fromEnv
    : typeof window !== 'undefined'
      ? window.location.origin
      : 'http://localhost:8080';
