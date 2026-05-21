import type React from "react";
import { createBrowserRouter, RouterProvider } from "react-router-dom";
import { HelloPage } from "./HelloPage";

const router = createBrowserRouter([
  {
    path: "/",
    element: <HelloPage />,
  },
]);

export function AppRouter(): React.JSX.Element {
  return <RouterProvider router={router} />;
}
