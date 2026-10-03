export function uid(prefix: string): string {
  const raw =
    typeof crypto !== 'undefined' && 'randomUUID' in crypto
      ? crypto.randomUUID().replace(/-/g, '')
      : Math.random().toString(36).slice(2) + Date.now().toString(36);
  return `${prefix}_${raw.slice(0, 24)}`;
}

/** Short human-friendly session id, e.g. ses_k3v9x2q7. */
export function sessionId(): string {
  const alphabet = 'abcdefghjkmnpqrstuvwxyz23456789';
  const bytes = new Uint8Array(10);
  crypto.getRandomValues(bytes);
  return 'ses_' + Array.from(bytes, b => alphabet[b % alphabet.length]).join('');
}
