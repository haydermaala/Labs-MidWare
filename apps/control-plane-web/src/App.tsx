// Console router. Public routes are reachable signed-out; everything under the
// shell requires a session and redirects to sign-in otherwise.

import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import { color, space } from '@lab-connect/ui';
import { AuthProvider, useAuth } from './auth/AuthProvider';
import { SignInPage } from './auth/SignInPage';
import { AcceptInvitePage, ForgotPasswordPage, ResetPasswordPage, VerifyEmailPage } from './auth/TokenPages';
import { AppShell } from './shell/AppShell';
import { AuditPage, DashboardPage, FleetPage, SecurityPage } from './pages/Pages';

function RequireSession(): JSX.Element {
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
  if (status === 'signed-out') {
    return <Navigate to="/sign-in" replace />;
  }
  return <AppShell />;
}

export function App(): JSX.Element {
  return (
    <AuthProvider>
      <BrowserRouter>
        <Routes>
          <Route path="/sign-in" element={<SignInPage />} />
          <Route path="/forgot-password" element={<ForgotPasswordPage />} />
          <Route path="/reset-password" element={<ResetPasswordPage />} />
          <Route path="/verify-email" element={<VerifyEmailPage />} />
          <Route path="/invite" element={<AcceptInvitePage />} />
          <Route element={<RequireSession />}>
            <Route index element={<DashboardPage />} />
            <Route path="/fleet" element={<FleetPage />} />
            <Route path="/audit" element={<AuditPage />} />
            <Route path="/security" element={<SecurityPage />} />
          </Route>
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </BrowserRouter>
    </AuthProvider>
  );
}
