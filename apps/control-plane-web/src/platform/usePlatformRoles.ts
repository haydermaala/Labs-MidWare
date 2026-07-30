// Resolves the signed-in user's PLATFORM roles (P6) via the whoami capability
// endpoint. Platform access is disjoint from tenant membership, so this is the single
// source of truth for whether to show the platform console + which sections apply.
import { useEffect, useState } from 'react';
import { platformWhoami, type ControlPlaneOptions } from '@lab-connect/api-client';
import { API_BASE } from '../config';
import { useAuth } from '../auth/AuthProvider';

export interface PlatformRolesState {
  readonly roles: readonly string[];
  readonly hasAccess: boolean;
  readonly loading: boolean;
  /** Whether the user's roles grant a given platform permission-ish capability. */
  readonly has: (role: string) => boolean;
}

export function usePlatformRoles(): PlatformRolesState {
  const { token } = useAuth();
  const [roles, setRoles] = useState<readonly string[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let live = true;
    if (token === null) {
      setRoles([]);
      setLoading(false);
      return;
    }
    const opts: ControlPlaneOptions = { baseUrl: API_BASE, adminToken: token };
    void platformWhoami(opts)
      .then((who) => { if (live) setRoles(who.roles); })
      .catch(() => { if (live) setRoles([]); })
      .finally(() => { if (live) setLoading(false); });
    return () => { live = false; };
  }, [token]);

  return {
    roles,
    hasAccess: roles.length > 0,
    loading,
    has: (role: string) => roles.includes(role),
  };
}
