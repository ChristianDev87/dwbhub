import type React from "react";
import { Routes, Route } from "react-router-dom";
import { HelloPage } from "./HelloPage";
import { VerifyEmailPage } from "./VerifyEmailPage";
import { PasswordResetPage } from "./PasswordResetPage";
import { SetupPage } from "./SetupPage";
import { VerifyEmailPromptPage } from "./VerifyEmailPromptPage";

export function AppRouter(): React.JSX.Element {
  return (
    <Routes>
      <Route path="/" element={<HelloPage />} />
      <Route path="/setup" element={<SetupPage />} />
      <Route path="/t/:slug/verify-email" element={<VerifyEmailPage />} />
      <Route path="/t/:slug/password-reset" element={<PasswordResetPage />} />
      <Route path="/t/:slug/verify-email-prompt" element={<VerifyEmailPromptPage />} />
    </Routes>
  );
}
