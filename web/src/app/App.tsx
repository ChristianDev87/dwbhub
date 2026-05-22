import type React from "react";
import { BrowserRouter } from "react-router-dom";
import { AppRouter } from "./router";
import { AuthProvider } from "./AuthContext";
import { SetupGuard } from "./SetupGuard";

export function App(): React.JSX.Element {
  return (
    <BrowserRouter>
      <AuthProvider>
        <SetupGuard>
          <AppRouter />
        </SetupGuard>
      </AuthProvider>
    </BrowserRouter>
  );
}
