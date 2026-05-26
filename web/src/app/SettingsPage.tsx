/**
 * SettingsPage — per-tenant settings for Owners.
 *
 * Currently exposes the edit-window for messages. Access is restricted to
 * users with the "Owner" role; non-owners see a 403 placeholder.
 *
 * Layout pattern matches GuildsPage: max-w-3xl container with section + border cards.
 */

import type React from "react";
import { useState } from "react";
import { useParams, Link } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { useAuth } from "./auth-context";
import { useUpdateTenantSettings } from "./hooks/useUpdateTenantSettings";

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

const PRESET_OPTIONS = [
  {
    labelKey: "settings.messageEditWindow.unlimited",
    value: null as number | null,
  },
  { labelKey: "settings.messageEditWindow.fifteenMin", value: 900 },
  { labelKey: "settings.messageEditWindow.oneHour", value: 3600 },
  { labelKey: "settings.messageEditWindow.twentyFourHours", value: 86400 },
] as const;

const CUSTOM_SENTINEL = "custom";

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

export function SettingsPage(): React.JSX.Element {
  const { slug } = useParams<{ slug: string }>();
  const { t } = useTranslation();
  const { state: authState } = useAuth();

  // Role gate — show access-denied for non-Owners.
  const isOwner =
    authState.kind === "authenticated" && authState.user.role === "Owner";

  const mutation = useUpdateTenantSettings(slug ?? "");

  // Selected preset key — null | 900 | 3600 | 86400 | "custom"
  const [selected, setSelected] = useState<
    number | null | typeof CUSTOM_SENTINEL
  >(null);
  // Custom value in minutes (used when selected === "custom")
  const [customMinutes, setCustomMinutes] = useState<number>(60);
  const [customError, setCustomError] = useState<string | null>(null);

  const isCustom = selected === CUSTOM_SENTINEL;

  // Derive the value to submit
  function getSubmitValue(): number | null {
    if (isCustom) return customMinutes * 60;
    return selected as number | null;
  }

  function handleSelectChange(e: React.ChangeEvent<HTMLSelectElement>) {
    const raw = e.target.value;
    if (raw === CUSTOM_SENTINEL) {
      setSelected(CUSTOM_SENTINEL);
    } else if (raw === "null") {
      setSelected(null);
    } else {
      setSelected(Number(raw));
    }
    setCustomError(null);
    mutation.reset();
  }

  function handleCustomMinutesChange(e: React.ChangeEvent<HTMLInputElement>) {
    const v = Number(e.target.value);
    setCustomMinutes(isNaN(v) ? 0 : v);
    setCustomError(null);
    mutation.reset();
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setCustomError(null);
    mutation.reset();

    if (isCustom) {
      if (
        !Number.isInteger(customMinutes) ||
        customMinutes < 1 ||
        customMinutes > 525600
      ) {
        setCustomError(t("settings.messageEditWindow.customRangeError"));
        return;
      }
    }

    await mutation.mutate({ messageEditWindowSeconds: getSubmitValue() });
  }

  // ---------------------------------------------------------------------------
  // Access denied
  // ---------------------------------------------------------------------------

  if (!isOwner) {
    return (
      <div
        className="max-w-3xl mx-auto p-8"
        data-testid="settings-access-denied"
      >
        <h1 className="text-2xl font-semibold">{t("settings.title")}</h1>
        <p className="mt-4 text-red-600" role="alert">
          {t("settings.accessDenied")}
        </p>
        <Link
          to={`/t/${slug ?? ""}/dashboard`}
          className="mt-4 inline-block text-blue-600 hover:underline text-sm"
        >
          ← {t("dashboard.welcome", { name: "" }).trim() || "Dashboard"}
        </Link>
      </div>
    );
  }

  // ---------------------------------------------------------------------------
  // Owner view
  // ---------------------------------------------------------------------------

  // Determine the <select> value string for the controlled input
  function selectValue(): string {
    if (selected === CUSTOM_SENTINEL) return CUSTOM_SENTINEL;
    if (selected === null) return "null";
    return String(selected);
  }

  return (
    <div className="max-w-3xl mx-auto p-8" data-testid="settings-page">
      <h1 className="text-2xl font-semibold" data-testid="settings-title">
        {t("settings.title")}
      </h1>

      <section className="mt-6 border rounded p-4">
        <h2 className="text-lg font-medium">
          {t("settings.messageEditWindow.label")}
        </h2>
        <p className="text-sm text-gray-500 mt-1">
          {t("settings.messageEditWindow.description")}
        </p>

        <form
          onSubmit={(e) => void handleSubmit(e)}
          className="mt-4 space-y-4"
          noValidate
          data-testid="settings-form"
        >
          {/* Dropdown */}
          <div>
            <label className="block text-sm font-medium">
              {t("settings.messageEditWindow.label")}
              <select
                data-testid="settings-edit-window-select"
                value={selectValue()}
                onChange={handleSelectChange}
                className="mt-1 block w-full border rounded px-2 py-1"
              >
                {PRESET_OPTIONS.map((opt) => (
                  <option
                    key={opt.value === null ? "null" : String(opt.value)}
                    value={opt.value === null ? "null" : String(opt.value)}
                  >
                    {t(opt.labelKey)}
                  </option>
                ))}
                <option value={CUSTOM_SENTINEL}>
                  {t("settings.messageEditWindow.custom")}
                </option>
              </select>
            </label>
          </div>

          {/* Custom minutes input */}
          {isCustom && (
            <div>
              <label className="block text-sm font-medium">
                {t("settings.messageEditWindow.customLabel")}
                <input
                  type="number"
                  min={1}
                  max={525600}
                  value={customMinutes}
                  onChange={handleCustomMinutesChange}
                  data-testid="settings-custom-minutes"
                  className="mt-1 block w-full border rounded px-2 py-1"
                />
              </label>
              {customError && (
                <p
                  data-testid="settings-custom-error"
                  className="text-sm text-red-600 mt-1"
                >
                  {customError}
                </p>
              )}
            </div>
          )}

          {/* Network error */}
          {mutation.error && (
            <p
              role="alert"
              data-testid="settings-error"
              className="text-sm text-red-600"
            >
              {t("guilds.networkError")}
            </p>
          )}

          {/* Success toast */}
          {mutation.isSuccess && (
            <p
              role="status"
              data-testid="settings-saved"
              className="text-sm text-green-600"
            >
              {t("settings.saved")}
            </p>
          )}

          <button
            type="submit"
            disabled={mutation.isPending}
            data-testid="settings-save-button"
            className="px-4 py-2 bg-blue-600 text-white rounded disabled:opacity-50"
          >
            {mutation.isPending ? "…" : t("settings.messageEditWindow.save")}
          </button>
        </form>
      </section>
    </div>
  );
}
