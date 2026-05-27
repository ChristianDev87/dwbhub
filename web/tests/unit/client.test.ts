/**
 * Unit tests for the createApiClient 401-refresh middleware.
 *
 * Three cases:
 *  1. Successful refresh → original request is retried with the new token.
 *  2. Concurrent 401 responses → exactly ONE refreshAccessToken call
 *     (Promise-coalescing).
 *  3. Request with X-Refresh-Retried header → no second refresh, 401 passed
 *     through as-is.
 */
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { createApiClient } from "../../src/lib/api/client";

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/** Build a minimal Response that looks like a 401 from the server. */
function make401(): Response {
  return new Response(null, { status: 401 });
}

/** Build a minimal 200 response. */
function make200(): Response {
  return new Response(JSON.stringify({ ok: true }), {
    status: 200,
    headers: { "Content-Type": "application/json" },
  });
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe("createApiClient — 401-refresh middleware", () => {
  beforeEach(() => {
    vi.stubGlobal("fetch", vi.fn());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("case 1: successful refresh → request is retried with new token", async () => {
    const refreshAccessToken = vi
      .fn()
      .mockResolvedValue("new-access-token-xyz");

    let callCount = 0;
    (fetch as ReturnType<typeof vi.fn>).mockImplementation(
      async (input: RequestInfo | URL, init?: RequestInit) => {
        callCount++;
        const url = input instanceof Request ? input.url : String(input);
        const headers =
          input instanceof Request ? input.headers : new Headers(init?.headers);

        // First call — return 401.
        if (callCount === 1) {
          expect(url).toContain("/api/health");
          return make401();
        }
        // Second call — the retry; must carry the new token and the loop guard.
        if (callCount === 2) {
          expect(headers.get("Authorization")).toBe(
            "Bearer new-access-token-xyz",
          );
          expect(headers.get("X-Refresh-Retried")).toBe("1");
          return make200();
        }
        throw new Error(`Unexpected fetch call #${callCount}`);
      },
    );

    const client = createApiClient(
      () => "old-token",
      refreshAccessToken,
      "http://localhost/",
    );
    const result = await client.GET("/api/health");

    expect(refreshAccessToken).toHaveBeenCalledTimes(1);
    expect(result.response.status).toBe(200);
  });

  it("case 2: concurrent 401s → exactly one refreshAccessToken call (coalescing)", async () => {
    let refreshCallCount = 0;
    const refreshAccessToken = vi.fn().mockImplementation(async () => {
      refreshCallCount++;
      // Small async delay so both 401 handlers enter the coalescing branch.
      await new Promise((r) => setTimeout(r, 5));
      return "coalesced-token";
    });

    let fetchCallIndex = 0;
    (fetch as ReturnType<typeof vi.fn>).mockImplementation(async () => {
      fetchCallIndex++;
      // First two calls → both 401 (triggers concurrent coalescing).
      if (fetchCallIndex <= 2) return make401();
      // Retry calls → 200.
      return make200();
    });

    const client = createApiClient(
      () => "old-token",
      refreshAccessToken,
      "http://localhost/",
    );

    // Fire two requests simultaneously.
    const [r1, r2] = await Promise.all([
      client.GET("/api/health"),
      client.GET("/api/health"),
    ]);

    // Only one refresh call despite two concurrent 401s.
    expect(refreshCallCount).toBe(1);
    expect(refreshAccessToken).toHaveBeenCalledTimes(1);
    expect(r1.response.status).toBe(200);
    expect(r2.response.status).toBe(200);
  });

  it("case 3: X-Refresh-Retried header → no second refresh, 401 passed through", async () => {
    const refreshAccessToken = vi.fn().mockResolvedValue("should-not-be-used");

    (fetch as ReturnType<typeof vi.fn>).mockImplementation(
      async (input: RequestInfo | URL) => {
        // Always 401 — simulates what happens if the retry itself gets a 401.
        const url = input instanceof Request ? input.url : String(input);
        void url;
        return make401();
      },
    );

    const client = createApiClient(
      () => "old-token",
      refreshAccessToken,
      "http://localhost/",
    );

    // First call → 401 → refresh → retry → retry gets 401 again.
    // The loop guard (X-Refresh-Retried on the retry) must prevent a second refresh.
    const result = await client.GET("/api/health");

    // The first 401 triggers exactly one refresh attempt.
    expect(refreshAccessToken).toHaveBeenCalledTimes(1);
    // The final response must be the 401 from the retry (not a further retry).
    expect(result.response.status).toBe(401);
  });
});
