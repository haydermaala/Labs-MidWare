import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { GatewaySummary } from '@lab-connect/api-client';
import { AuthProvider } from '../src/auth/AuthProvider';
import { SignInPage } from '../src/auth/SignInPage';
import { ForgotPasswordPage } from '../src/auth/TokenPages';
import { GatewayTable, PageHeader } from '../src/pages/Pages';

/** Minimal fetch stub: route → [status, body]. */
function stubFetch(routes: Record<string, [number, unknown]>) {
  return vi.fn(async (input: URL | RequestInfo) => {
    const path = new URL(String(input)).pathname;
    const [status, body] = routes[path] ?? [404, { error: 'not found' }];
    return new Response(status === 204 ? null : JSON.stringify(body), {
      status,
      headers: { 'content-type': 'application/json' },
    });
  });
}

function renderIn(ui: React.ReactElement) {
  return render(<MemoryRouter><AuthProvider>{ui}</AuthProvider></MemoryRouter>);
}

beforeEach(() => {
  window.sessionStorage.clear();
  vi.restoreAllMocks();
});

const gateways: GatewaySummary[] = [
  {
    id: 'gw_online0001abcd', tenantId: 'ten_1', name: 'Chemistry Analyzer A',
    enrolledAt: '2026-07-19T10:00:00Z', active: true,
    lastSeenAt: '2026-07-19T11:00:00Z', status: 'online',
  },
  {
    id: 'gw_never00002abcd', tenantId: 'ten_1', name: 'Hematology Analyzer B',
    enrolledAt: '2026-07-19T10:00:00Z', active: true,
    lastSeenAt: null, status: 'never',
  },
  {
    id: 'gw_decomm003abcd', tenantId: 'ten_1', name: 'Retired Bench Unit',
    enrolledAt: '2026-07-19T09:00:00Z', active: false,
    lastSeenAt: '2026-07-19T09:30:00Z', status: 'decommissioned',
  },
];

describe('console auth surface', () => {
  it('sign-in renders labelled credential fields and a forgot-password link', () => {
    renderIn(<SignInPage />);
    expect(screen.getByLabelText('Email address')).toBeTruthy();
    expect(screen.getByLabelText('Password')).toBeTruthy();
    expect(screen.getByRole('button', { name: /sign in/i })).toBeTruthy();
    expect(screen.getByRole('link', { name: /forgot your password/i })).toBeTruthy();
  });

  it('sign-in advances to the MFA step when the server challenges', async () => {
    vi.stubGlobal('fetch', stubFetch({
      '/api/auth/login': [200, { mfaRequired: true, mfaToken: 'mtk_1' }],
    }));
    renderIn(<SignInPage />);

    fireEvent.change(screen.getByLabelText('Email address'), { target: { value: 'a@b.co' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'correct horse battery' } });
    fireEvent.click(screen.getByRole('button', { name: /sign in/i }));

    await waitFor(() => expect(screen.getByLabelText('Authentication code')).toBeTruthy());
    // The recovery-code path is offered as an alternative.
    fireEvent.click(screen.getByRole('button', { name: /recovery code instead/i }));
    expect(screen.getByLabelText('Recovery code')).toBeTruthy();
  });

  it('sign-in shows a generic error and stays on the password step on failure', async () => {
    vi.stubGlobal('fetch', stubFetch({ '/api/auth/login': [401, { error: 'nope' }] }));
    renderIn(<SignInPage />);

    fireEvent.change(screen.getByLabelText('Email address'), { target: { value: 'a@b.co' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'wrong wrong wrong' } });
    fireEvent.click(screen.getByRole('button', { name: /sign in/i }));

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toMatch(/not recognized/i);
    // No hint about whether the account exists.
    expect(alert.textContent).not.toMatch(/no account|unknown user/i);
  });

  it('forgot-password confirms uniformly without revealing account existence', async () => {
    vi.stubGlobal('fetch', stubFetch({ '/api/auth/forgot-password': [202, null] }));
    renderIn(<ForgotPasswordPage />);

    fireEvent.change(screen.getByLabelText('Email address'), { target: { value: 'ghost@b.co' } });
    fireEvent.click(screen.getByRole('button', { name: /send reset link/i }));

    await waitFor(() => expect(screen.getByText(/check your email/i)).toBeTruthy());
    expect(screen.getByText(/if an account exists/i)).toBeTruthy();
  });
});

describe('fleet table', () => {
  it('renders status as icon + text and disables decommission for inactive rows', () => {
    const onDecommission = vi.fn();
    const { container } = render(
      <GatewayTable gateways={gateways} canManage busyId={null} onDecommission={onDecommission} />,
    );

    expect(screen.getByText('online')).toBeTruthy();
    expect(screen.getByText('never seen')).toBeTruthy();
    expect(screen.getByText('decommissioned')).toBeTruthy();
    // Every badge pairs colour with an icon and a text label.
    expect(container.querySelectorAll('[role="status"] svg').length).toBe(3);
    // Never-seen gateways show an em dash rather than a fabricated timestamp.
    expect(screen.getByText('—')).toBeTruthy();

    const buttons = screen.getAllByRole('button', { name: /decommission/i });
    expect(buttons).toHaveLength(3);
    expect(buttons.filter((b) => (b as HTMLButtonElement).disabled)).toHaveLength(1);

    fireEvent.click(buttons[0]!);
    expect(onDecommission).toHaveBeenCalledWith('gw_online0001abcd');
  });

  it('hides the actions column entirely for roles that cannot manage the fleet', () => {
    render(<GatewayTable gateways={gateways} canManage={false} busyId={null} onDecommission={vi.fn()} />);
    expect(screen.queryByRole('button', { name: /decommission/i })).toBeNull();
    expect(screen.queryByRole('columnheader', { name: /actions/i })).toBeNull();
  });

  it('page headers expose a single h1 for the route', () => {
    render(<PageHeader title="Fleet" description="Gateways enrolled in this laboratory." />);
    expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('Fleet');
  });
});

describe('people administration', () => {
  const members = [
    { userId: 'usr_owner', email: 'owner@lab.example', role: 'owner', since: '2026-01-01T00:00:00Z', active: true },
    { userId: 'usr_tech', email: 'tech@lab.example', role: 'technician', since: '2026-02-01T00:00:00Z', active: true },
  ];
  const invitations = [
    { id: 'invt_1', email: 'pending@lab.example', role: 'auditor', expiresAt: '2026-08-01T00:00:00Z', status: 'pending' },
    { id: 'invt_2', email: 'gone@lab.example', role: 'read-only', expiresAt: '2026-08-01T00:00:00Z', status: 'revoked' },
  ];

  /** Signs a session in via sessionStorage so the page renders authenticated. */
  function signedIn(role: string) {
    window.sessionStorage.setItem('lc.session', 'ses_test');
    window.sessionStorage.setItem('lc.tenant', 'ten_1');
    return stubFetch({
      '/api/auth/me': [200, { id: 'usr_owner', email: 'owner@lab.example', createdAt: '2026-01-01T00:00:00Z', emailVerified: true, active: true, mfaEnabled: false }],
      '/api/me/memberships': [200, [{ tenantId: 'ten_1', tenantName: 'Riverside', role, tenantActive: true }]],
      '/api/tenants/ten_1/members': [200, members],
      '/api/tenants/ten_1/invitations': [200, invitations],
    });
  }

  it('owner sees members, roles, and pending invitations', async () => {
    vi.stubGlobal('fetch', signedIn('owner'));
    const { PeoplePage } = await import('../src/pages/PeoplePage');
    renderIn(<PeoplePage />);

    await waitFor(() => expect(screen.getByText('owner@lab.example')).toBeTruthy());
    expect(screen.getByText('tech@lab.example')).toBeTruthy();
    expect(screen.getByText('pending@lab.example')).toBeTruthy();
    // Invitation status uses the shared colour-independent badge.
    expect(screen.getByText('pending')).toBeTruthy();
    expect(screen.getByText('revoked')).toBeTruthy();
  });

  it('protects the last owner: their role select and remove are disabled', async () => {
    vi.stubGlobal('fetch', signedIn('owner'));
    const { PeoplePage } = await import('../src/pages/PeoplePage');
    renderIn(<PeoplePage />);

    await waitFor(() => expect(screen.getByText('owner@lab.example')).toBeTruthy());
    const ownerRole = screen.getByLabelText(/role for owner@lab.example/i) as HTMLSelectElement;
    expect(ownerRole.disabled).toBe(true);

    const removeButtons = screen.getAllByRole('button', { name: /remove/i }) as HTMLButtonElement[];
    // Only the technician can be removed while a single owner remains.
    expect(removeButtons.filter((b) => b.disabled)).toHaveLength(1);
  });

  it('a tenant-admin cannot select the owner role when inviting', async () => {
    vi.stubGlobal('fetch', signedIn('tenant-admin'));
    const { PeoplePage } = await import('../src/pages/PeoplePage');
    renderIn(<PeoplePage />);

    await waitFor(() => expect(screen.getByLabelText('Role')).toBeTruthy());
    const ownerOption = screen
      .getAllByRole('option', { name: 'owner' })
      .filter((o) => (o as HTMLOptionElement).disabled);
    expect(ownerOption.length).toBeGreaterThan(0);
  });

  it('roles without user management get a permission-denied view, not a broken table', async () => {
    vi.stubGlobal('fetch', signedIn('technician'));
    const { PeoplePage } = await import('../src/pages/PeoplePage');
    renderIn(<PeoplePage />);

    await waitFor(() => expect(screen.getByRole('alert').textContent).toMatch(/do not have permission/i));
    expect(screen.queryByRole('button', { name: /send invitation/i })).toBeNull();
  });
});

describe('gateway onboarding', () => {
  function signedIn(role: string, gateways: unknown[] = []) {
    window.sessionStorage.setItem('lc.session', 'ses_test');
    window.sessionStorage.setItem('lc.tenant', 'ten_1');
    return stubFetch({
      '/api/auth/me': [200, { id: 'usr_1', email: 'ops@lab.example', createdAt: '2026-01-01T00:00:00Z', emailVerified: true, active: true, mfaEnabled: false }],
      '/api/me/memberships': [200, [{ tenantId: 'ten_1', tenantName: 'Riverside', role, tenantActive: true }]],
      '/api/tenants/ten_1/gateways': [200, gateways],
      '/api/tenants/ten_1/enrollment-tokens': [200, { token: 'sample-enrollment-token-not-real', expiresAt: '2099-01-01T00:15:00Z' }],
    });
  }

  it('fleet managers get an Add gateway action', async () => {
    vi.stubGlobal('fetch', signedIn('lab-admin'));
    const { FleetPage } = await import('../src/pages/Pages');
    const { unmount } = renderIn(<FleetPage />);
    await waitFor(() => expect(screen.getAllByRole('button', { name: /add gateway/i }).length).toBeGreaterThan(0));
    unmount();
  });

  it('viewers get no Add gateway action', async () => {
    window.sessionStorage.clear();
    vi.stubGlobal('fetch', signedIn('technician'));
    const { FleetPage } = await import('../src/pages/Pages');
    renderIn(<FleetPage />);
    await waitFor(() => expect(screen.getByText(/no gateways enrolled/i)).toBeTruthy());
    expect(screen.queryAllByRole('button', { name: /add gateway/i })).toHaveLength(0);
  });

  it('issuing a token reveals it once as a copyable, single-use secret', async () => {
    vi.stubGlobal('fetch', signedIn('owner'));
    const { OnboardDrawer } = await import('../src/pages/OnboardDrawer');
    renderIn(<OnboardDrawer open onClose={() => {}} onEnrolled={() => {}} />);

    // The drawer is a labelled modal dialog with a step to issue the token.
    expect(screen.getByRole('dialog', { name: /add a gateway/i })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /issue enrollment token/i }));

    await waitFor(() => expect(screen.getByDisplayValue('sample-enrollment-token-not-real')).toBeTruthy());
    // Single-use warning is announced, and the token field is read-only.
    expect(screen.getByText(/cannot be shown again/i)).toBeTruthy();
    expect((screen.getByDisplayValue('sample-enrollment-token-not-real') as HTMLInputElement).readOnly).toBe(true);
    // The control-plane address is offered for the gateway setup.
    expect(screen.getByDisplayValue(/localhost|railway|http/i)).toBeTruthy();
  });
});

describe('security settings', () => {
  function signedIn(routes: Record<string, [number, unknown]>, mfaEnabled = false) {
    window.sessionStorage.setItem('lc.session', 'ses_test');
    window.sessionStorage.setItem('lc.tenant', 'ten_1');
    return stubFetch({
      '/api/auth/me': [200, { id: 'usr_1', email: 'ops@lab.example', createdAt: '2026-01-01T00:00:00Z', emailVerified: true, active: true, mfaEnabled }],
      '/api/me/memberships': [200, [{ tenantId: 'ten_1', tenantName: 'Riverside', role: 'owner', tenantActive: true }]],
      '/api/auth/sessions': [200, [{ id: 's1', createdAt: '2026-07-20T10:00:00Z', expiresAt: '2026-07-27T10:00:00Z', lastSeenAt: '2026-07-20T12:00:00Z', current: true }]],
      ...routes,
    });
  }

  it('guides MFA enrollment: secret, then recovery codes shown once', async () => {
    vi.stubGlobal('fetch', signedIn({
      '/api/auth/mfa/setup': [200, { secret: 'JBSWY3DPEHPK3PXP', provisioningUri: 'otpauth://totp/LabConnect:ops@lab.example?secret=JBSWY3DPEHPK3PXP' }],
      '/api/auth/mfa/enable': [200, { recoveryCodes: ['aaaaa-bbbbb', 'ccccc-ddddd'] }],
    }));
    const { SecurityPage } = await import('../src/pages/SecurityPage');
    renderIn(<SecurityPage />);

    fireEvent.click(await screen.findByRole('button', { name: /set up two-factor/i }));
    // The secret is exposed for manual entry, plus the otpauth URI.
    await waitFor(() => expect(screen.getByDisplayValue('JBSWY3DPEHPK3PXP')).toBeTruthy());
    expect(screen.getByDisplayValue(/otpauth:\/\/totp/)).toBeTruthy();

    fireEvent.change(screen.getByLabelText(/current 6-digit code/i), { target: { value: '123456' } });
    fireEvent.click(screen.getByRole('button', { name: /verify and enable/i }));

    // Recovery codes are revealed exactly once, behind an acknowledgement.
    await waitFor(() => expect(screen.getByText('aaaaa-bbbbb')).toBeTruthy());
    expect(screen.getByText(/never be shown again/i)).toBeTruthy();
    expect(screen.getByRole('button', { name: /saved my recovery codes/i })).toBeTruthy();
  });

  it('a bad enrollment code surfaces a generic error, not the code', async () => {
    vi.stubGlobal('fetch', signedIn({
      '/api/auth/mfa/setup': [200, { secret: 'JBSWY3DPEHPK3PXP', provisioningUri: 'otpauth://x' }],
      '/api/auth/mfa/enable': [400, { error: 'bad code' }],
    }));
    const { SecurityPage } = await import('../src/pages/SecurityPage');
    renderIn(<SecurityPage />);

    fireEvent.click(await screen.findByRole('button', { name: /set up two-factor/i }));
    await screen.findByDisplayValue('JBSWY3DPEHPK3PXP');
    fireEvent.change(screen.getByLabelText(/current 6-digit code/i), { target: { value: '000000' } });
    fireEvent.click(screen.getByRole('button', { name: /verify and enable/i }));

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toMatch(/not accepted/i);
  });

  it('an MFA-enabled account offers disable, not setup', async () => {
    vi.stubGlobal('fetch', signedIn({}, true));
    const { SecurityPage } = await import('../src/pages/SecurityPage');
    renderIn(<SecurityPage />);

    await waitFor(() => expect(screen.getByRole('button', { name: /disable/i })).toBeTruthy());
    expect(screen.queryByRole('button', { name: /set up two-factor/i })).toBeNull();
  });

  it('lists active sessions, marking the current device', async () => {
    vi.stubGlobal('fetch', signedIn({}));
    const { SecurityPage } = await import('../src/pages/SecurityPage');
    renderIn(<SecurityPage />);

    await waitFor(() => expect(screen.getByText('This device')).toBeTruthy());
    expect(screen.getByRole('button', { name: /sign out everywhere/i })).toBeTruthy();
  });
});

describe('tenant settings', () => {
  function signedIn(role: string, tenant = { id: 'ten_1', name: 'Riverside Diagnostics', createdAt: '2026-01-01T00:00:00Z', active: true }) {
    window.sessionStorage.setItem('lc.session', 'ses_test');
    window.sessionStorage.setItem('lc.tenant', 'ten_1');
    return stubFetch({
      '/api/auth/me': [200, { id: 'usr_1', email: 'ops@lab.example', createdAt: '2026-01-01T00:00:00Z', emailVerified: true, active: true, mfaEnabled: false }],
      '/api/me/memberships': [200, [{ tenantId: 'ten_1', tenantName: tenant.name, role, tenantActive: tenant.active }]],
      '/api/tenants/ten_1/settings': [200, tenant],
    });
  }

  it('owner can edit the name and sees the danger zone', async () => {
    vi.stubGlobal('fetch', signedIn('owner'));
    const { SettingsPage } = await import('../src/pages/SettingsPage');
    renderIn(<SettingsPage />);

    const nameInput = await screen.findByLabelText('Laboratory name') as HTMLInputElement;
    expect(nameInput.disabled).toBe(false);
    expect(nameInput.value).toBe('Riverside Diagnostics');
    expect(screen.getByText('ten_1')).toBeTruthy();
    expect(screen.getByRole('heading', { name: /deactivate laboratory/i })).toBeTruthy();
  });

  it('non-owners see a read-only name and no danger zone', async () => {
    vi.stubGlobal('fetch', signedIn('technician'));
    const { SettingsPage } = await import('../src/pages/SettingsPage');
    renderIn(<SettingsPage />);

    const nameInput = await screen.findByLabelText('Laboratory name') as HTMLInputElement;
    expect(nameInput.disabled).toBe(true);
    expect(screen.getByText(/only an owner can rename/i)).toBeTruthy();
    expect(screen.queryByRole('heading', { name: /deactivate laboratory/i })).toBeNull();
    expect(screen.queryByRole('button', { name: /save changes/i })).toBeNull();
  });

  it('deactivation requires typing the exact laboratory name', async () => {
    vi.stubGlobal('fetch', signedIn('owner'));
    const { SettingsPage } = await import('../src/pages/SettingsPage');
    renderIn(<SettingsPage />);

    await screen.findByLabelText('Laboratory name');
    const deactivate = screen.getByRole('button', { name: /^deactivate laboratory$/i }) as HTMLButtonElement;
    expect(deactivate.disabled).toBe(true);

    fireEvent.change(screen.getByLabelText(/type the laboratory name to confirm/i), { target: { value: 'wrong' } });
    expect(deactivate.disabled).toBe(true);
    fireEvent.change(screen.getByLabelText(/type the laboratory name to confirm/i), { target: { value: 'Riverside Diagnostics' } });
    expect(deactivate.disabled).toBe(false);
  });
});
