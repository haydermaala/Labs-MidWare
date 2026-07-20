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
