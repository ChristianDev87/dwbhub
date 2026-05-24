import { useTranslation } from "react-i18next";
import { useAuth } from "./auth-context";

export function DashboardPage(): React.JSX.Element | null {
  const { t } = useTranslation();
  const { state, logout } = useAuth();

  if (state.kind !== "authenticated") return null;

  return (
    <div className="p-8 max-w-2xl mx-auto">
      <h1 className="text-2xl font-semibold">
        {t("dashboard.welcome", { name: state.user.displayName })}
      </h1>
      <p className="text-gray-600 mt-2">
        {t("dashboard.tenant", { name: state.tenant.name })}
      </p>
      <button
        type="button"
        onClick={() => void logout()}
        data-testid="dashboard-logout"
        className="mt-6 px-4 py-2 bg-red-600 text-white rounded"
      >
        {t("dashboard.logout")}
      </button>
    </div>
  );
}
