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
import { ChatPage } from "./messaging/ChatPage";
import { SettingsPage } from "./SettingsPage";

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
      <Route
        path="/t/:slug/channels/:channelPublicId"
        element={
          <AuthGuard>
            <ChatPage />
          </AuthGuard>
        }
      />
      <Route
        path="/t/:slug/settings"
        element={
          <AuthGuard>
            <SettingsPage />
          </AuthGuard>
        }
      />
    </Routes>
  );
}
