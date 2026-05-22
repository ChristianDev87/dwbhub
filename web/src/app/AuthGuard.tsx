import { Navigate, useLocation } from "react-router-dom";
import { useAuth } from "./AuthContext";

/**
 * Renders children only when the user is authenticated. While the initial refresh
 * is in flight (state === "checking") shows a small placeholder. When unauthenticated,
 * declaratively redirects to /login via <Navigate>. NOT imperative — using
 * useEffect+navigate() is what caused Plan-0.3d's SetupGuard to get stuck.
 */
export function AuthGuard({
  children,
}: {
  children: React.ReactNode;
}): React.JSX.Element {
  const location = useLocation();
  const { state } = useAuth();

  if (state.kind === "checking") {
    return (
      <div data-testid="auth-guard-checking" className="p-8">
        …
      </div>
    );
  }
  if (state.kind === "unauthenticated") {
    return <Navigate to="/login" replace state={{ from: location }} />;
  }
  return <>{children}</>;
}
