import { QueryClient } from "@tanstack/react-query";

/**
 * Application-wide TanStack Query client singleton.
 *
 * Exported as a module-level constant so both <QueryClientProvider> in App.tsx
 * and non-React code (e.g. the AuthContext logout handler) can reference the
 * exact same instance without going through React context.
 */
export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      retry: 1,
      refetchOnWindowFocus: false,
    },
    mutations: {
      retry: 0,
    },
  },
});
