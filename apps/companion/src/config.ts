export const SPACETIMEDB_URI: string = import.meta.env.VITE_SPACETIMEDB_URI ?? 'ws://127.0.0.1:3000';
export const SPACETIMEDB_DB: string = import.meta.env.VITE_SPACETIMEDB_DB ?? 'scalpal';
export const TOKEN_KEY = `scalpal:${SPACETIMEDB_URI}/${SPACETIMEDB_DB}:token`;
export const NAME_KEY = 'scalpal:displayName';
export const VITALS_URL: string = import.meta.env.VITE_VITALS_URL ?? 'http://127.0.0.1:8790';
