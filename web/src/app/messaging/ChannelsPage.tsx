import type React from "react";
import { Link, useParams } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { useChannelsList } from "./useChannelsList";

export function ChannelsPage(): React.JSX.Element {
  const { slug, guildPublicId } = useParams<{
    slug: string;
    guildPublicId: string;
  }>();
  const { t } = useTranslation();
  const { channels, isLoading, error, toggleError, refresh, toggleBridge } =
    useChannelsList(slug ?? "", guildPublicId ?? "");

  if (isLoading) {
    return (
      <div className="max-w-3xl mx-auto p-8">
        <p className="text-gray-500">{t("channels.loading")}</p>
      </div>
    );
  }

  if (error) {
    return (
      <div className="max-w-3xl mx-auto p-8">
        <p role="alert" data-testid="channels-error" className="text-red-600">
          {t("channels.error")}
        </p>
      </div>
    );
  }

  return (
    <div className="max-w-3xl mx-auto p-8">
      <Link
        to={`/t/${slug ?? ""}/guilds`}
        className="text-blue-600 hover:underline text-sm"
        data-testid="channels-back-link"
      >
        {t("channels.back")}
      </Link>

      <div className="flex items-center justify-between mt-4">
        <h1 className="text-2xl font-semibold">{t("channels.title")}</h1>
        <button
          type="button"
          data-testid="channels-resync"
          onClick={refresh}
          className="px-3 py-1 bg-blue-600 text-white rounded text-sm"
        >
          {t("channels.resync")}
        </button>
      </div>

      {toggleError && (
        <p
          role="alert"
          data-testid="channels-toggle-error"
          className="mt-2 text-sm text-red-600"
        >
          {t("channels.toggleError", { code: toggleError })}
        </p>
      )}

      <ul className="mt-4 divide-y border rounded">
        {channels.map((c) => {
          const isText = c.channelType === 0;
          return (
            <li
              key={c.publicId}
              data-testid={`channel-row-${c.publicId}`}
              className={`px-4 py-3 flex items-center justify-between ${
                !isText ? "opacity-60" : ""
              }`}
            >
              <label className="flex items-center gap-3 cursor-pointer select-none">
                <input
                  type="checkbox"
                  data-testid={`channel-bridge-toggle-${c.publicId}`}
                  checked={c.isBridged}
                  disabled={!isText}
                  onChange={(e: React.ChangeEvent<HTMLInputElement>) => {
                    void toggleBridge(c.publicId, e.target.checked);
                  }}
                  className="h-4 w-4 accent-blue-600 disabled:opacity-50"
                />
                <span className="font-mono text-sm">#{c.name}</span>
                {!isText && (
                  <span
                    data-testid={`channel-not-bridgeable-${c.publicId}`}
                    className="text-xs text-gray-500 italic"
                  >
                    ({t("channels.notBridgeable")})
                  </span>
                )}
                {c.backfill?.status === "running" && (
                  <span
                    data-testid={`channel-backfill-progress-${c.publicId}`}
                    className="text-xs text-amber-600"
                  >
                    {t("channels.backfillProgress", {
                      count: c.backfill.fetchedCount,
                    })}
                  </span>
                )}
              </label>

              {isText && c.isBridged && (
                <Link
                  to={`/t/${slug ?? ""}/channels/${c.publicId}`}
                  data-testid={`channel-open-chat-${c.publicId}`}
                  className="text-sm text-blue-600 hover:underline"
                >
                  {t("channels.openChat")}
                </Link>
              )}
            </li>
          );
        })}
      </ul>

      {channels.length === 0 && (
        <p data-testid="channels-empty" className="mt-4 text-gray-500 text-sm">
          {t("channels.empty")}
        </p>
      )}
    </div>
  );
}
