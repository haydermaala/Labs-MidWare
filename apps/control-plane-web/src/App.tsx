// Console router. Two route trees selected by auth state: signed-out visitors
// get the public marketing/legal site, signed-in operators get the app shell.
// Auth + token flows are available in either state (they manage their own
// redirects). The public "/security" (marketing) and the app "/security"
// (account settings) intentionally share a path, disambiguated by auth state.

import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import { color, space } from '@lab-connect/ui';
import { AuthProvider, useAuth } from './auth/AuthProvider';
import { SignInPage } from './auth/SignInPage';
import { AcceptInvitePage, ForgotPasswordPage, ResetPasswordPage, VerifyEmailPage } from './auth/TokenPages';
import { AppShell } from './shell/AppShell';
import { AuditPage, DashboardPage, FleetPage } from './pages/Pages';
import { SecurityPage } from './pages/SecurityPage';
import { SettingsPage } from './pages/SettingsPage';
import { PeoplePage } from './pages/PeoplePage';
import {
  DocsPage, LandingPage, LegalPage, PricingPage, SecurityPublicPage, StatusPage,
} from './public/PublicPages';

function Router(): JSX.Element {
  const { status } = useAuth();

  if (status === 'loading') {
    return (
      <div
        role="status"
        aria-live="polite"
        style={{ minHeight: '100vh', display: 'grid', placeItems: 'center', color: color.fgMuted, padding: space[5] }}
      >
        Loading your workspace…
      </div>
    );
  }

  const authed = status === 'signed-in';

  return (
    <Routes>
      {/* Auth + token flows: reachable in any state; each self-redirects. */}
      <Route path="/sign-in" element={<SignInPage />} />
      <Route path="/forgot-password" element={<ForgotPasswordPage />} />
      <Route path="/reset-password" element={<ResetPasswordPage />} />
      <Route path="/verify-email" element={<VerifyEmailPage />} />
      <Route path="/invite" element={<AcceptInvitePage />} />

      {authed ? (
        <Route element={<AppShell />}>
          <Route index element={<DashboardPage />} />
          <Route path="/fleet" element={<FleetPage />} />
          <Route path="/people" element={<PeoplePage />} />
          <Route path="/audit" element={<AuditPage />} />
          <Route path="/security" element={<SecurityPage />} />
          <Route path="/settings" element={<SettingsPage />} />
        </Route>
      ) : (
        <>
          <Route index element={<LandingPage />} />
          <Route path="/pricing" element={<PricingPage />} />
          <Route path="/security" element={<SecurityPublicPage />} />
          <Route path="/docs" element={<DocsPage />} />
          <Route path="/status" element={<StatusPage />} />
          <Route path="/legal/terms" element={<LegalPage kind="terms" />} />
          <Route path="/legal/privacy" element={<LegalPage kind="privacy" />} />
        </>
      )}

      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}

export function App(): JSX.Element {
  return (
    <AuthProvider>
      <BrowserRouter>
        <Router />
      </BrowserRouter>
    </AuthProvider>
  );
}
