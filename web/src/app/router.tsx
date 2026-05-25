import type React from "react";
import { Routes, Route } from "react-router-dom";
import { HelloPage } from "./HelloPage";
import { LoginPage } from "./LoginPage";
import { SetupPage } from "./SetupPage";
import { VerifyEmailPage } from "./VerifyEmailPage";
import { PasswordResetPage } from "./PasswordResetPage";
import { VerifyEmailPromptPage } from "./VerifyEmailPromptPage";
import { DashboardPage } from "./DashboardPage";
import { GuildsPage } from "./GuildsPage";
import { AuthGuard } from "./AuthGuard";
import { ChannelsPage } from "./messaging/ChannelsPage";

export function AppRouter(): React.JSX.Element {
  return (
    <Routes>
      <Route path="/" element={<HelloPage />} />
      <Route path="/setup" element={<SetupPage />} />
      <Route path="/login" element={<LoginPage />} />
      <Route path="/t/:slug/verify-email" element={<VerifyEmailPage />} />
      <Route path="/t/:slug/password-reset" element={<PasswordResetPage />} />
      <Route
        path="/t/:slug/verify-email-prompt"
        element={<VerifyEmailPromptPage />}
      />
      <Route
        path="/t/:slug/dashboard"
        element={
          <AuthGuard>
            <DashboardPage />
          </AuthGuard>
        }
      />
      <Route
        path="/t/:slug/guilds"
        element={
          <AuthGuard>
            <GuildsPage />
          </AuthGuard>
        }
      />
      <Route
        path="/t/:slug/guilds/:guildPublicId/channels"
        element={
          <AuthGuard>
            <ChannelsPage />
          </AuthGuard>
        }
      />
      {/* Task 13: ChatPage placeholder route */}
      <Route
        path="/t/:slug/channels/:channelPublicId"
        element={
          <AuthGuard>
            <div className="p-8 text-gray-500">
              Chat — coming soon (Task 13)
            </div>
          </AuthGuard>
        }
      />
    </Routes>
  );
}
