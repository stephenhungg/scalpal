// Local ledger of live Presage minutes (the key has a fixed budget). Written to
// .presage-usage.json (gitignored) every few seconds while live, so a crash still counts.
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

const FILE = fileURLToPath(new URL("../.presage-usage.json", import.meta.url));

export function budget(totalMinutes) {
  const read = () => (existsSync(FILE) ? JSON.parse(readFileSync(FILE, "utf8")) : { usedSeconds: 0, sessions: [] });
  const write = (d) => writeFileSync(FILE, JSON.stringify(d, null, 2));
  const usedMinutes = () => read().usedSeconds / 60;
  return {
    totalMinutes,
    usedMinutes,
    remainingMinutes: () => totalMinutes - usedMinutes(),
    begin() {
      const start = Date.now();
      const base = read();
      let last = 0;
      const flush = () => {
        const secs = (Date.now() - start) / 1000;
        const d = read();
        d.usedSeconds += secs - last;
        last = secs;
        write(d);
      };
      const tick = setInterval(flush, 5000);
      base.sessions.push({ startedAt: new Date(start).toISOString() });
      write(base);
      return {
        end() {
          clearInterval(tick);
          flush();
          return last / 60;
        },
      };
    },
  };
}
