import { readdir, unlink } from 'fs';
import { join } from 'path';

// The daily files in ./logs (timing-YYYY-MM-DD.log, trades-YYYY-MM-DD.jsonl) are ~20 MB a day and nothing reads them
// back: the trades are in the backend's database too. Keep the last 14 days. Other files there are left alone.
const LOG_DIR = join(__dirname, '..', 'logs');
const KEEP_DAYS = 14;
const DAILY = /^(?:timing|trades)-(\d{4}-\d{2}-\d{2})\.(?:log|jsonl)$/;

export function pruneOldLogs(): void {
  const cutoff = new Date(Date.now() - KEEP_DAYS * 86_400_000).toISOString().slice(0, 10);
  readdir(LOG_DIR, (err, names) => {
    if (err) return;
    for (const name of names) {
      const m = DAILY.exec(name);
      if (m && m[1] < cutoff) unlink(join(LOG_DIR, name), () => { /* best effort */ });
    }
  });
}

export function startLogRetention(): void {
  pruneOldLogs();
  setInterval(pruneOldLogs, 6 * 3_600_000).unref();
}
