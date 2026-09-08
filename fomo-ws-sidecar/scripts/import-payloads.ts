import fs from 'fs';

const FILE = 'C:\\Users\\Administrator\\Desktop\\ws-payloads.txt';
const BACKEND_URL = process.env.BACKEND_URL ?? 'http://127.0.0.1:8000';
const SEPARATOR = '─'.repeat(80);
const CONCURRENCY = 10;

const raw = fs.readFileSync(FILE, 'utf8');
const chunks = raw.split(SEPARATOR).map(s => s.trim()).filter(Boolean);

console.log(`Parsed ${chunks.length} events from file`);

function parseChunk(chunk: string): Record<string, unknown> | null {
  // Strip leading [ISO_TIMESTAMP] prefix from first line
  const stripped = chunk.replace(/^\[\d{4}-\d{2}-\d{2}T[\d:.Z]+\]\s*/, '');
  try {
    return JSON.parse(stripped) as Record<string, unknown>;
  } catch {
    return null;
  }
}

async function post(payload: Record<string, unknown>): Promise<string> {
  const res = await fetch(`${BACKEND_URL}/api/ws-events`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  });
  const body = await res.json() as { accepted?: boolean; reason?: string; error?: string };
  return body.accepted ? 'inserted' : (body.reason ?? body.error ?? 'skipped');
}

async function run() {
  let inserted = 0, skipped = 0, failed = 0;

  for (let i = 0; i < chunks.length; i += CONCURRENCY) {
    const batch = chunks.slice(i, i + CONCURRENCY);
    await Promise.all(batch.map(async (chunk) => {
      const payload = parseChunk(chunk);
      if (!payload) { failed++; return; }
      try {
        const result = await post(payload);
        if (result === 'inserted') inserted++;
        else skipped++;
      } catch {
        failed++;
      }
    }));
    process.stdout.write(`\r${i + batch.length}/${chunks.length} processed — ${inserted} inserted, ${skipped} skipped, ${failed} failed`);
  }

  console.log(`\nDone — ${inserted} inserted, ${skipped} skipped, ${failed} failed`);
}

run().catch(console.error);
