import type React from "react";
import { createBrowserRouter, RouterProvider } from "react-router-dom";
import { HelloPage } from "./HelloPage";
import { VerifyEmailPage } from "./VerifyEmailPage";
import { PasswordResetPage } from "./PasswordResetPage";

const router = createBrowserRouter([
  {
    path: "/",
    element: <HelloPage />,
  },
  {
    path: "/t/:slug/verify-email",
    element: <VerifyEmailPage />,
  },
  {
    path: "/t/:slug/password-reset",
    element: <PasswordResetPage />,
  },
]);

export function AppRouter(): React.JSX.Element {
  return <RouterProvider router={router} />;
}
