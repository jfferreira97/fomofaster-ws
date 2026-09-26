import { appendFile, mkdirSync } from 'fs';
import { join } from 'path';

// Latency trail on disk, one file per UTC day in ./logs: the console window is the only other
// record, and it's gone after a restart. Tab-separated so it greps and pastes cleanly.
const LOG_DIR = join(__dirname, '..', 'logs');
mkdirSync(LOG_DIR, { recursive: true });

export function logTiming(...fields: (string | number | null | undefined)[]): void {
  const now = new Date().toISOString();
  const line = [now, ...fields.map((f) => (f == null ? '' : String(f)))].join('\t') + '\n';
  appendFile(join(LOG_DIR, `timing-${now.slice(0, 10)}.log`), line, () => { /* best effort */ });
}
