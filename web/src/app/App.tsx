import type React from "react";
import { BrowserRouter } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { AppRouter } from "./router";
import { AuthProvider } from "./AuthContext";
import { SetupGuard } from "./SetupGuard";

const queryClient = new QueryClient({
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

export function App(): React.JSX.Element {
  return (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <AuthProvider>
          <SetupGuard>
            <AppRouter />
          </SetupGuard>
        </AuthProvider>
      </BrowserRouter>
    </QueryClientProvider>
  );
}
