import fs from 'fs';
import { transformItem, type PumpFeedItem } from '../src/transform';
import { postPumpEvent, postStructuredPump } from '../src/client';

const FILE = process.argv[2] ?? 'C:\\Users\\Administrator\\Desktop\\alerts-log-full.jsonl';

const lines = fs.readFileSync(FILE, 'utf8').split('\n').map(l => l.trim()).filter(Boolean);
console.log(`Read ${lines.length} lines from ${FILE}`);

async function run() {
  let transformed = 0, skippedKind = 0, malformed = 0, eventAccepted = 0, eventDuplicate = 0;

  for (const line of lines) {
    let item: PumpFeedItem;
    try {
      item = JSON.parse(line) as PumpFeedItem;
    } catch {
      malformed++;
      continue;
    }

    const result = transformItem(item);
    if (!result) { skippedKind++; continue; }
    transformed++;

    const accepted = await postPumpEvent({ ...item, externalId: result.externalId });
    if (!accepted) { eventDuplicate++; continue; }
    eventAccepted++;

    await postStructuredPump(result.structured);
  }

  console.log(`\nDone.`);
  console.log(`  parsed lines:        ${lines.length}`);
  console.log(`  malformed JSON:      ${malformed}`);
  console.log(`  skipped (update/quote): ${skippedKind}`);
  console.log(`  transformed (callout/repost/reply): ${transformed}`);
  console.log(`  pump-events accepted (new): ${eventAccepted}`);
  console.log(`  pump-events duplicate:      ${eventDuplicate}`);
}

run().catch(console.error);
