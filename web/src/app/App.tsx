import type React from "react";
import { BrowserRouter } from "react-router-dom";
import { QueryClientProvider } from "@tanstack/react-query";
import { AppRouter } from "./router";
import { AuthProvider } from "./AuthContext";
import { SetupGuard } from "./SetupGuard";
import { queryClient } from "@/lib/api/queryClient";

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
