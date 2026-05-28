import type React from "react";
import { useState, useEffect } from "react";
import { useParams } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { useAuth } from "@/app/auth-context";
import { useUpdateTenantSettings } from "@/app/hooks/useUpdateTenantSettings";

// Preset values sent directly as seconds; "custom" reveals a manual input.
type PresetKey = "null" | "300" | "900" | "3600" | "86400" | "custom";

interface Preset {
  key: PresetKey;
  labelKey: string;
  value: number | null;
}

const PRESETS: Preset[] = [
  { key: "null", labelKey: "settings.presetDefault", value: null },
  { key: "300", labelKey: "settings.preset5m", value: 300 },
  { key: "900", labelKey: "settings.preset15m", value: 900 },
  { key: "3600", labelKey: "settings.preset1h", value: 3600 },
  { key: "86400", labelKey: "settings.preset24h", value: 86400 },
  { key: "custom", labelKey: "settings.presetCustom", value: null },
];

function secondsToPresetKey(seconds: number | null | undefined): PresetKey {
  if (seconds == null) return "null";
  const match = PRESETS.find((p) => p.value === seconds && p.key !== "custom");
  return match ? (match.key as PresetKey) : "custom";
}

/**
 * SettingsPage — per-tenant settings management page.
 * Accessible at /t/:slug/settings; renders the edit-window configuration for
 * Owners and a "access denied" notice for regular members.
 */
export function SettingsPage(): React.JSX.Element {
  const { slug } = useParams<{ slug: string }>();
  const { t } = useTranslation();
  const { state } = useAuth();
  const { mutateAsync, isPending, isSuccess, error, reset } =
    useUpdateTenantSettings();

  const isOwner = state.kind === "authenticated" && state.user.role === "Owner";

  // Derive initial preset from the tenant auth state (if available).
  const initialSeconds =
    state.kind === "authenticated"
      ? (state.tenant.messageEditWindowSeconds ?? null)
      : null;

  const [selectedPreset, setSelectedPreset] = useState<PresetKey>(
    secondsToPresetKey(initialSeconds),
  );
  const [customMinutes, setCustomMinutes] = useState<string>(
    initialSeconds != null && secondsToPresetKey(initialSeconds) === "custom"
      ? String(Math.round(initialSeconds / 60))
      : "",
  );
  // Track the value that was last successfully saved so the Save button disables
  // when there are no changes.
  const [savedPreset, setSavedPreset] = useState<PresetKey>(selectedPreset);
  const [savedCustom, setSavedCustom] = useState<string>(customMinutes);

  const isDirty =
    selectedPreset !== savedPreset ||
    (selectedPreset === "custom" && customMinutes !== savedCustom);

  useEffect(() => {
    if (isSuccess) {
      setSavedPreset(selectedPreset);
      setSavedCustom(customMinutes);
    }
  }, [isSuccess, selectedPreset, customMinutes]);

  function resolveSeconds(): number | null | "invalid" {
    const preset = PRESETS.find((p) => p.key === selectedPreset);
    if (!preset) return "invalid";
    if (selectedPreset !== "custom") return preset.value;

    const mins = parseInt(customMinutes, 10);
    if (isNaN(mins) || mins < 1 || mins > 525600) return "invalid";
    return mins * 60;
  }

  async function handleSave(e: React.FormEvent) {
    e.preventDefault();
    reset();
    const seconds = resolveSeconds();
    if (seconds === "invalid") return;
    if (!slug) return;

    await mutateAsync({
      slug,
      messageEditWindowSeconds: seconds,
    });
  }

  if (!isOwner) {
    return (
      <div className="max-w-xl mx-auto p-8">
        <h1 className="text-2xl font-semibold mb-4">{t("settings.title")}</h1>
        <p role="alert" data-testid="settings-owner-only">
          {t("settings.ownerOnly")}
        </p>
      </div>
    );
  }

  const isInvalid =
    selectedPreset === "custom" && resolveSeconds() === "invalid";

  return (
    <div className="max-w-xl mx-auto p-8">
      <h1 className="text-2xl font-semibold mb-6">{t("settings.title")}</h1>

      <form onSubmit={(e) => void handleSave(e)}>
        <section className="mb-6">
          <h2 className="text-lg font-medium mb-2">
            {t("settings.editWindowSection")}
          </h2>

          <label
            htmlFor="edit-window-select"
            className="block text-sm font-medium mb-1"
          >
            {t("settings.editWindowLabel")}
          </label>
          <select
            id="edit-window-select"
            data-testid="settings-edit-window-select"
            value={selectedPreset}
            onChange={(ev) => {
              setSelectedPreset(ev.target.value as PresetKey);
              reset();
            }}
            className="block w-full border rounded px-3 py-2 text-sm"
          >
            {PRESETS.map((p) => (
              <option key={p.key} value={p.key}>
                {t(p.labelKey)}
              </option>
            ))}
          </select>

          {selectedPreset === "custom" && (
            <div className="mt-3">
              <label
                htmlFor="edit-window-custom"
                className="block text-sm font-medium mb-1"
              >
                {t("settings.editWindowCustomLabel")}
              </label>
              <input
                id="edit-window-custom"
                data-testid="settings-custom-minutes"
                type="number"
                min={1}
                max={525600}
                value={customMinutes}
                onChange={(ev) => {
                  setCustomMinutes(ev.target.value);
                  reset();
                }}
                className="block w-full border rounded px-3 py-2 text-sm"
              />
              {isInvalid && (
                <p
                  role="alert"
                  data-testid="settings-custom-error"
                  className="mt-1 text-sm text-red-600"
                >
                  {t("settings.editWindowCustomError")}
                </p>
              )}
            </div>
          )}
        </section>

        {error === "out_of_range" && (
          <p
            role="alert"
            data-testid="settings-out-of-range-error"
            className="mb-4 text-sm text-red-600"
          >
            {t("settings.outOfRangeError")}
          </p>
        )}

        {isSuccess && (
          <p
            data-testid="settings-save-success"
            className="mb-4 text-sm text-green-700"
          >
            {t("settings.saved")}
          </p>
        )}

        <button
          type="submit"
          data-testid="settings-save-button"
          disabled={!isDirty || isPending || isInvalid}
          className="px-4 py-2 bg-blue-600 text-white rounded text-sm disabled:opacity-50"
        >
          {isPending ? t("settings.saving") : t("settings.save")}
        </button>
      </form>
    </div>
  );
}
