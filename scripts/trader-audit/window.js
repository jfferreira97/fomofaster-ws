// The analysis window, shared by every step: the 30 days ending at AUDIT_END (default: start
// of today, UTC), split in half for the out-of-sample checks.
const DAY = 864e5;
const END = process.env.AUDIT_END ? Date.parse(process.env.AUDIT_END) : Math.floor(Date.now() / DAY) * DAY;
const SINCE = END - 30 * DAY;
const SPLIT = SINCE + 15 * DAY;
const fmt = t => new Date(t).toLocaleDateString('en-US', { month: 'short', day: 'numeric', timeZone: 'UTC' });
const year = new Date(END - DAY).getUTCFullYear();
module.exports = {
  END, SINCE, SPLIT,
  label: `${fmt(SINCE)} – ${fmt(END - DAY)}, ${year}`,
  halfA: `${fmt(SINCE)} – ${fmt(SPLIT - DAY)}`,
  halfB: `${fmt(SPLIT)} – ${fmt(END - DAY)}`,
};
