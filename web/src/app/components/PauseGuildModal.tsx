import { useTranslation } from "react-i18next";

interface Props {
  guildDisplayName: string;
  onCancel: () => void;
  onConfirm: () => void;
}

export function PauseGuildModal({
  guildDisplayName,
  onCancel,
  onConfirm,
}: Props): React.JSX.Element {
  const { t } = useTranslation();

  return (
    <div
      role="dialog"
      aria-modal="true"
      data-testid="pause-guild-modal"
      className="fixed inset-0 bg-black/40 flex items-center justify-center p-4"
    >
      <div className="bg-white rounded p-6 max-w-sm w-full">
        <h3 className="text-lg font-semibold">
          {t("botConnection.pauseModal.title")}
        </h3>
        <p className="text-sm text-gray-600 mt-1">{guildDisplayName}</p>
        <p className="mt-2 text-sm">{t("botConnection.pauseModal.body")}</p>
        <div className="mt-4 flex gap-3 justify-end">
          <button
            type="button"
            data-testid="pause-modal-cancel"
            onClick={onCancel}
            className="px-3 py-1 border rounded"
          >
            {t("guilds.deleteConfirmCancel")}
          </button>
          <button
            type="button"
            data-testid="pause-modal-confirm"
            onClick={onConfirm}
            className="px-3 py-1 bg-amber-600 text-white rounded"
          >
            {t("botConnection.pauseModal.confirm")}
          </button>
        </div>
      </div>
    </div>
  );
}
