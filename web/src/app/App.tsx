import type React from "react";
import { BrowserRouter } from "react-router-dom";
import { AppRouter } from "./router";
import { SetupGuard } from "./SetupGuard";

export function App(): React.JSX.Element {
  return (
    <BrowserRouter>
      <SetupGuard>
        <AppRouter />
      </SetupGuard>
    </BrowserRouter>
  );
}
