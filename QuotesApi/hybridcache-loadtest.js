/**
 * hybridcache-loadtest.js — Day 21 verification
 *
 * AFTER  (hot key, cache warm):   .\k6-v2.0.0-windows-amd64\k6.exe run hybridcache-loadtest.js
 * BEFORE (unique IDs, all DB):    .\k6-v2.0.0-windows-amd64\k6.exe run --env BEFORE=1 hybridcache-loadtest.js
 * STAMPEDE (50 VUs × 1 iter):     .\k6-v2.0.0-windows-amd64\k6.exe run --vus 50 --iterations 50 hybridcache-loadtest.js
 */

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Counter, Trend } from 'k6/metrics';

const BASE_URL  = __ENV.BASE_URL || 'http://localhost:5032';
const IS_BEFORE = __ENV.BEFORE  === '1';

const cacheHitRate = new Rate('cache_hit_rate');
const dbFetches    = new Counter('suspected_db_fetches');
const quoteTrend   = new Trend('quote_latency_ms', true);

export const options = {
  scenarios: {
    quotes: {
      executor: 'constant-vus',
      vus: 50,
      duration: '10s',
    },
  },
  thresholds: {
    http_req_duration: ['p(95)<300', 'p(99)<800'],
    // 404 (quote not found) is an expected outcome in BEFORE mode —
    // only genuine errors (500, network failures) should fail this gate.
    http_req_failed: ['rate<0.10'],
  },
};

export function setup() {
  const res = http.post(
    `${BASE_URL}/api/auth/login`,
    JSON.stringify({ email: 'admin@example.com', password: 'password123' }),
    { headers: { 'Content-Type': 'application/json' } }
  );
  if (!check(res, { 'login 200': r => r.status === 200 })) {
    throw new Error(`Login failed — HTTP ${res.status}: ${res.body}`);
  }
  return { token: res.json('access_token') };
}

export default function (data) {
  // BEFORE: unique ID per (VU × iteration) — never repeats, so every request
  //         is a guaranteed cache miss and triggers a DB query.
  //         IDs start at 100001 so they don't exist in the DB; the factory
  //         runs, returns null, and the null IS cached — but since no two
  //         requests share a key, there are zero L1/L2 hits during the run.
  // AFTER:  always quote 5    → L1 hit on every request after the first miss
  const id = IS_BEFORE ? ((__VU - 1) * 10000 + __ITER + 100001) : 5;

  const res = http.get(`${BASE_URL}/api/quotes/${id}`, {
    headers:          { Authorization: `Bearer ${data.token}` },
    tags:             { mode: IS_BEFORE ? 'before' : 'after' },
    // Prevents k6 counting 404 ("quote not found") as a failed request.
    responseCallback: http.expectedStatuses(200, 404),
  });

  check(res, { 'ok': r => r.status === 200 || r.status === 404 });

  quoteTrend.add(res.timings.duration);

  // < 5 ms  = L1 in-process hit
  // 5–30 ms = L2 (DistributedMemoryCache / Redis) hit
  // > 50 ms = DB fetch (cache miss)
  const isHit = res.timings.duration < 50;
  cacheHitRate.add(isHit);
  if (!isHit) dbFetches.add(1);

  sleep(0.05);
}
