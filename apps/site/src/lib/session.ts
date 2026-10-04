// Per-tab session flags. Module state survives client-side navigation but resets on a full
// reload, so a refresh replays the intro while moving between pages does not.
// Only write these in the browser (effects / event handlers): on the server this module is
// shared by every request.
export const session = { introPlayed: false };
