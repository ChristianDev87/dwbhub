/**
 * useUpdateTenantSettings — React Query mutation hook for
 * PATCH /api/t/{slug}/settings.
 *
 * Invalidates the dashboard query on success so updated tenant state
 * propagates to any consumer of the tenant context.
 */

import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useApiClient } from "@/lib/api/useApiClient";
import { qk } from "@/lib/api/queryKeys";

export interface UpdateTenantSettingsInput {
  slug: string;
  messageEditWindowSeconds: number | null;
}

export interface UpdateTenantSettingsResult {
  mutate: (input: UpdateTenantSettingsInput) => void;
  mutateAsync: (input: UpdateTenantSettingsInput) => Promise<void>;
  isPending: boolean;
  isSuccess: boolean;
  error: string | null;
  reset: () => void;
}

export function useUpdateTenantSettings(): UpdateTenantSettingsResult {
  const api = useApiClient();
  const queryClient = useQueryClient();

  const mutation = useMutation<
    void,
    { errorCode: string },
    UpdateTenantSettingsInput
  >({
    mutationFn: async ({ slug, messageEditWindowSeconds }) => {
      const res = await api.PATCH(
        "/api/t/{slug}/settings" as never,
        {
          params: { path: { slug } },
          body: { messageEditWindowSeconds },
        } as never,
      );

      // openapi-fetch: check for error
      if ((res as { error?: unknown }).error) {
        const err = (res as { error?: { error?: string } }).error;
        throw { errorCode: err?.error ?? "unknown_error" };
      }
    },
    onSuccess: (_data, { slug }) => {
      // Invalidate the dashboard query so the latest tenant settings propagate.
      void queryClient.invalidateQueries({ queryKey: qk.dashboard(slug) });
    },
  });

  const errorObj = mutation.error as { errorCode?: string } | null;

  return {
    mutate: mutation.mutate,
    mutateAsync: mutation.mutateAsync,
    isPending: mutation.isPending,
    isSuccess: mutation.isSuccess,
    error: errorObj ? (errorObj.errorCode ?? "unknown_error") : null,
    reset: mutation.reset,
  };
}
