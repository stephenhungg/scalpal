// localStorage can throw (private mode, blocked storage); never let it break the app.
export function load(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

export function save(key: string, value: string) {
  try {
    localStorage.setItem(key, value);
  } catch {
    // ignore
  }
}

export function remove(key: string) {
  try {
    localStorage.removeItem(key);
  } catch {
    // ignore
  }
}
