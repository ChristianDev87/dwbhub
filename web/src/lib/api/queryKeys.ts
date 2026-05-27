/**
 * Centralised TanStack-Query cache key factory. Always derive keys from this
 * object so refetch / invalidation calls match producer keys exactly.
 *
 * Pattern: `qk.<resource>.<scope>(args)` returns a readonly tuple.
 *
 * Tenant-scoped keys include `tenantSlug` as the second segment so that
 * `queryClient.removeQueries({ queryKey: [resource, tenantSlug] })` can evict
 * all data for a specific tenant on logout or tenant switch.
 */
export const qk = {
  setup: {
    status: () => ["setup", "status"] as const,
  },
  health: () => ["health"] as const,
  dashboard: (tenantSlug: string) => ["dashboard", tenantSlug] as const,
  guilds: {
    list: (tenantSlug: string) => ["guilds", tenantSlug, "list"] as const,
    detail: (tenantSlug: string, guildPublicId: string) =>
      ["guilds", tenantSlug, "detail", guildPublicId] as const,
  },
  channels: {
    list: (tenantSlug: string, guildPublicId: string) =>
      ["channels", tenantSlug, guildPublicId, "list"] as const,
    backfillStatus: (tenantSlug: string, channelPublicId: string) =>
      ["channels", tenantSlug, channelPublicId, "backfill-status"] as const,
  },
  messages: {
    list: (tenantSlug: string, channelPublicId: string) =>
      ["messages", tenantSlug, channelPublicId, "list"] as const,
  },
} as const;
