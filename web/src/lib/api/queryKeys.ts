/**
 * Centralised TanStack-Query cache key factory. Always derive keys from this
 * object so refetch / invalidation calls match producer keys exactly.
 *
 * Pattern: `qk.<resource>.<scope>(args)` returns a readonly tuple.
 */
export const qk = {
  setup: {
    status: () => ["setup", "status"] as const,
  },
  health: () => ["health"] as const,
} as const;
