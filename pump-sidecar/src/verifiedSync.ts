import type { Page } from 'playwright';

const ts = () => { const d = new Date(); return `[${new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().replace('T', ' ').slice(0, 19)}]`; };

const BACKEND_URL = process.env.BACKEND_URL ?? 'http://127.0.0.1:8000';
const LEADERBOARD_API = 'https://frontend-api-v3.pump.fun';

// Empirically confirmed valid combos (2026-08-30): allTime period and sort=pnl both 400.
// Server hard-caps at 100 results per call regardless of limit/offset — sweeping every
// valid period×sort combo is the only way to get broader coverage than one top-100 slice.
const PERIODS = ['daily', 'weekly', 'monthly'];
const SORTS = ['combined', 'realized', 'unrealized'];

// Be polite to pump.fun's API between individual follow calls.
const FOLLOW_PACING_MS = 600;

interface LeaderboardEntry {
  walletAddress: string;
  username: string;
  isVerified: boolean;
}

async function fetchJson(page: Page, url: string, init?: { method?: string }): Promise<any> {
  return page.evaluate(async ({ u, opts }) => {
    const res = await fetch(u, { credentials: 'include', ...(opts ?? {}) });
    if (!res.ok) return { __status: res.status, __ok: false };
    try {
      return await res.json();
    } catch {
      return { __status: res.status, __ok: true };
    }
  }, { u: url, opts: init });
}

async function sweepVerifiedTraders(page: Page): Promise<LeaderboardEntry[]> {
  const byWallet = new Map<string, LeaderboardEntry>();
  for (const period of PERIODS) {
    for (const sort of SORTS) {
      const url = `${LEADERBOARD_API}/pnl-leaderboard?period=${period}&sort=${sort}&limit=100`;
      const data = await fetchJson(page, url).catch(() => null);
      const entries = Array.isArray(data?.entries) ? data.entries : [];
      for (const e of entries) {
        if (!e.walletAddress || !e.username) continue;
        byWallet.set(e.walletAddress, {
          walletAddress: e.walletAddress,
          username: e.username,
          isVerified: e.isVerified === true,
        });
      }
    }
  }
  return [...byWallet.values()];
}

async function registerWithBackend(traders: LeaderboardEntry[]): Promise<void> {
  const body = {
    Traders: traders.map(t => ({ Handle: t.username, IsVerified: t.isVerified })),
    Platform: 'Pump',
  };
  try {
    const res = await fetch(`${BACKEND_URL}/api/traders/bulk-register`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    const data = await res.json().catch(() => null);
    console.log(`${ts()} [verified-sync] backend registered:`, JSON.stringify(data));
  } catch (err) {
    console.error(`${ts()} [verified-sync] backend registration failed:`, err);
  }
}

async function getMyWalletAddress(page: Page): Promise<string | null> {
  const data = await fetchJson(page, `${LEADERBOARD_API}/auth/my-profile`).catch(() => null);
  // NOTE: my-profile's own "userId" field is an internal UUID, NOT what the
  // following/single endpoint's "userId" query param actually wants — that endpoint
  // wants the wallet address (confirmed empirically). Use "address", not "userId".
  return data?.address ?? null;
}

async function isFollowing(page: Page, myWallet: string, targetWallet: string): Promise<boolean> {
  const data = await fetchJson(page, `${LEADERBOARD_API}/following/single/${targetWallet}?userId=${myWallet}`).catch(() => null);
  return !!data?.follow;
}

async function followOnPumpFun(page: Page, wallet: string): Promise<boolean> {
  const result = await page.evaluate(async (w) => {
    const res = await fetch(`https://frontend-api-v3.pump.fun/following/v2/${w}`, { method: 'POST', credentials: 'include' });
    return res.status;
  }, wallet);
  return result === 201 || result === 200;
}

/** Sweeps pump.fun's verified-trader roster, syncs it into our backend, and actually
 *  follows any newly-verified trader on the live pump.fun account this page is
 *  authenticated as. Safe to call repeatedly — every step is idempotent. */
export async function runVerifiedSync(page: Page): Promise<void> {
  console.log(`${ts()} [verified-sync] starting sweep...`);
  const traders = await sweepVerifiedTraders(page);
  const verified = traders.filter(t => t.isVerified);
  console.log(`${ts()} [verified-sync] found ${verified.length} verified of ${traders.length} distinct traders swept`);

  await registerWithBackend(traders);

  const myWallet = await getMyWalletAddress(page);
  if (!myWallet) {
    console.warn(`${ts()} [verified-sync] could not resolve own wallet address, skipping follow step`);
    return;
  }

  let followed = 0, alreadyFollowing = 0, failed = 0;
  for (const t of verified) {
    try {
      const already = await isFollowing(page, myWallet, t.walletAddress);
      if (already) { alreadyFollowing++; continue; }

      const ok = await followOnPumpFun(page, t.walletAddress);
      if (ok) {
        followed++;
        console.log(`${ts()} [verified-sync] ✅ followed ${t.username} (${t.walletAddress})`);
      } else {
        failed++;
        console.warn(`${ts()} [verified-sync] ❌ follow failed for ${t.username}`);
      }
      await new Promise(r => setTimeout(r, FOLLOW_PACING_MS));
    } catch (err) {
      failed++;
      console.error(`${ts()} [verified-sync] error following ${t.username}:`, err);
    }
  }
  console.log(`${ts()} [verified-sync] done — followed ${followed} new, ${alreadyFollowing} already following, ${failed} failed`);
}
