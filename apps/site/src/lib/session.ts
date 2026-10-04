// Per-tab session flags. Module state survives client-side navigation but resets on a full
// reload, so a refresh replays the intro while moving between pages does not.
export const session = { introPlayed: false };
