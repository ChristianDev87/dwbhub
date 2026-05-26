/**
 * useUpdateTenantSettings — React-Query mutation for PATCH /api/t/{slug}/settings.
 *
 * On success the mutation resolves; callers should show a toast. There is no
 * React Query cache key to invalidate here because the tenant's edit-window
 * value is not stored in the Query cache — it flows through AuthContext which
 * is session-storage backed and refreshed on login. The backend enforces the
 * value server-side; the frontend UI hint in ChatPage only needs refreshing on
 * next full page load, which is acceptable per Plan 1.1.
 */

import { useState } from "react";
import { useAuth } from "../auth-context";

export interface UpdateTenantSettingsInput {
  /** Seconds (60–31536000) or null for unlimited. */
  messageEditWindowSeconds: number | null;
}

export interface UpdateTenantSettingsResult {
  mutate: (input: UpdateTenantSettingsInput) => Promise<void>;
  isPending: boolean;
  error: string | null;
  isSuccess: boolean;
  reset: () => void;
}

export function useUpdateTenantSettings(
  slug: string,
): UpdateTenantSettingsResult {
  const { state } = useAuth();
  const accessToken = state.kind === "authenticated" ? state.accessToken : null;

  const [isPending, setIsPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [isSuccess, setIsSuccess] = useState(false);

  function reset() {
    setError(null);
    setIsSuccess(false);
  }

  async function mutate(input: UpdateTenantSettingsInput): Promise<void> {
    if (!accessToken) return;
    setIsPending(true);
    setError(null);
    setIsSuccess(false);
    try {
      const res = await fetch(`/api/t/${encodeURIComponent(slug)}/settings`, {
        method: "PATCH",
        credentials: "include",
        headers: {
          Authorization: `Bearer ${accessToken}`,
          "Content-Type": "application/json",
        },
        body: JSON.stringify({
          messageEditWindowSeconds: input.messageEditWindowSeconds,
        }),
      });
      if (!res.ok) {
        setError("network");
        return;
      }
      setIsSuccess(true);
    } catch {
      setError("network");
    } finally {
      setIsPending(false);
    }
  }

  return { mutate, isPending, error, isSuccess, reset };
}
