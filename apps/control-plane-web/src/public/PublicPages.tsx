// Public (signed-out) pages. Content is deliberately factual: it describes the
// real architecture and the security controls that are actually implemented. No
// invented metrics, and no concrete prices — pricing is not published until it is
// approved, so the pricing page presents tiers and a contact path, not numbers.

import { Link } from 'react-router-dom';
import { color, fontSize, space } from '@lab-connect/ui';
import { PublicLayout, Prose } from './PublicLayout';

function Card({ title, children }: { readonly title: string; readonly children: React.ReactNode }): JSX.Element {
  return (
    <div className="lc-card" style={{ padding: space[4], display: 'grid', gap: space[2] }}>
      <h3 style={{ fontSize: fontSize.body, fontWeight: 600 }}>{title}</h3>
      <p style={{ margin: 0, color: color.fgMuted, fontSize: fontSize.body, lineHeight: 1.5 }}>{children}</p>
    </div>
  );
}

export function LandingPage(): JSX.Element {
  return (
    <PublicLayout>
      <section style={{ maxWidth: 960, margin: '0 auto', padding: `${space[6]}px ${space[5]}px`, textAlign: 'center' }}>
        <h1 style={{ fontSize: fontSize.hero, fontWeight: 700, letterSpacing: '-0.02em', lineHeight: 1.15 }}>
          Reliable connectivity between laboratory analyzers and your LIS
        </h1>
        <p style={{ maxWidth: 620, margin: `${space[4]}px auto 0`, fontSize: fontSize.section, color: color.fgMuted, lineHeight: 1.5 }}>
          LabConnect captures analyzer output on-site, standardizes it, and delivers it to your
          information systems — with full provenance and nothing released until it is validated.
        </p>
        <div style={{ display: 'flex', gap: space[3], justifyContent: 'center', marginTop: space[5] }}>
          <Link to="/sign-in" className="lc-btn lc-btn--primary">Get started</Link>
          <Link to="/security" className="lc-btn lc-btn--secondary">How we keep it safe</Link>
        </div>
      </section>

      <section style={{ maxWidth: 960, margin: '0 auto', padding: `0 ${space[5]}px ${space[6]}px` }}>
        <h2 style={{ fontSize: fontSize.title, fontWeight: 600, marginBottom: space[4] }}>How it works</h2>
        <ol style={{
          margin: 0, padding: 0, listStyle: 'none',
          display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))', gap: space[4],
        }}>
          <li><Card title="1 · Capture on-site">
            A small gateway service runs on the machine beside the analyzer. It reads output over
            serial, TCP, or file drops, makes only outbound connections, and needs no inbound ports.
          </Card></li>
          <li><Card title="2 · Standardize">
            Raw ASTM and HL7 messages are parsed into one canonical model with exact decimal values —
            never coerced to floating point — and every message keeps its raw provenance.
          </Card></li>
          <li><Card title="3 · Deliver">
            Results are queued durably and delivered to your LIS/HIS with idempotent, acknowledged
            hand-off. Unknown analyzers are capture-only until validated.
          </Card></li>
        </ol>

        <div style={{ marginTop: space[6], padding: space[5], borderRadius: 6, border: `1px solid ${color.border}`, background: color.surface1 }}>
          <p className="lc-mono" style={{ margin: 0, textAlign: 'center', color: color.fgMuted, fontSize: fontSize.body }}>
            Analyzer → on-site gateway → secure outbound channel → LabConnect → LIS / HIS
          </p>
        </div>
      </section>
    </PublicLayout>
  );
}

export function PricingPage(): JSX.Element {
  const tiers = [
    { name: 'Pilot', for: 'A single site validating one or a few analyzers.', includes: ['One laboratory', 'Passive capture + validation', 'Community support'] },
    { name: 'Laboratory', for: 'A production laboratory running bidirectional connectivity.', includes: ['Sites and departments', 'Driver certification workflow', 'Priority support'] },
    { name: 'Network', for: 'Multi-site organizations with central administration.', includes: ['Many laboratories', 'Central fleet + audit', 'Onboarding and SLAs'] },
  ];
  return (
    <PublicLayout>
      <Prose title="Pricing" lead="LabConnect is priced per laboratory and per connected device. Final pricing is set with your team during onboarding — the tiers below describe scope, not published rates.">
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(240px, 1fr))', gap: space[4] }}>
          {tiers.map((t) => (
            <div key={t.name} className="lc-card" style={{ padding: space[4], display: 'grid', gap: space[3] }}>
              <div>
                <h3 style={{ fontSize: fontSize.section, fontWeight: 600 }}>{t.name}</h3>
                <p style={{ margin: `${space[1]}px 0 0`, color: color.fgMuted, fontSize: fontSize.body }}>{t.for}</p>
              </div>
              <ul style={{ margin: 0, paddingLeft: space[4], color: color.fgMuted, fontSize: fontSize.body, display: 'grid', gap: 4 }}>
                {t.includes.map((i) => <li key={i}>{i}</li>)}
              </ul>
              <Link to="/sign-in" className="lc-btn lc-btn--secondary" style={{ justifySelf: 'start' }}>Talk to us</Link>
            </div>
          ))}
        </div>
        <p style={{ marginTop: space[5], color: color.fgMuted, fontSize: fontSize.meta }}>
          Clinical validation, code-signing, and production connectivity are scoped separately and are
          never enabled without a laboratory's explicit sign-off.
        </p>
      </Prose>
    </PublicLayout>
  );
}

export function SecurityPublicPage(): JSX.Element {
  const controls = [
    ['Capture-only by default', 'Unknown analyzers are read-only. The gateway has no code path to send arbitrary commands to a device; outbound control is separately allowlisted and approved.'],
    ['Provenance and validation', 'Every result keeps its raw message, driver, parser, and mapping versions. Nothing is released to a patient record until the exact device, workflow, and LIS path complete controlled validation.'],
    ['Least-exposure networking', 'On-site gateways make only outbound connections and need no inbound firewall ports. Device credentials are rotated at enrollment and never reused.'],
    ['Tenant isolation', 'Every operation is authorized server-side by laboratory membership and role. One laboratory can never see another’s devices, results, or audit.'],
    ['Account security', 'Modern password hashing, single-use expiring links for verification and reset, server-side sessions you can revoke, and optional TOTP two-factor with recovery codes.'],
    ['Audit and redaction', 'Administrative and delivery activity is recorded in an append-only trail. Logs, metrics, and support bundles never contain patient identifiers or result values.'],
  ];
  return (
    <PublicLayout>
      <Prose title="Security" lead="LabConnect handles clinical infrastructure, so safety and least privilege are built in — not bolted on.">
        <div style={{ display: 'grid', gap: space[3] }}>
          {controls.map(([title, body]) => (
            <div key={title} className="lc-card" style={{ padding: space[4], display: 'grid', gap: space[1] }}>
              <h3 style={{ fontSize: fontSize.body, fontWeight: 600 }}>{title}</h3>
              <p style={{ margin: 0, color: color.fgMuted, fontSize: fontSize.body, lineHeight: 1.5 }}>{body}</p>
            </div>
          ))}
        </div>
        <p style={{ marginTop: space[5], color: color.fgMuted, fontSize: fontSize.meta }}>
          Independent threat-modeling, penetration testing, and formal compliance reviews are part of
          our pre-pilot gate. Contact us for the current security summary.
        </p>
      </Prose>
    </PublicLayout>
  );
}

export function DocsPage(): JSX.Element {
  return (
    <PublicLayout>
      <Prose title="Documentation" lead="Guides for operators and administrators are published as LabConnect rolls out to your laboratory.">
        <div className="lc-card" style={{ padding: space[4], color: color.fgMuted, fontSize: fontSize.body }}>
          Detailed documentation — gateway installation, driver certification, mapping review, and LIS
          integration — is provided during onboarding. Signed in? Your dashboard links the guides
          relevant to your role.
        </div>
      </Prose>
    </PublicLayout>
  );
}

export function StatusPage(): JSX.Element {
  return (
    <PublicLayout>
      <Prose title="Service status" lead="Operational status for the LabConnect control plane.">
        <div className="lc-card" style={{ padding: space[4], color: color.fgMuted, fontSize: fontSize.body }}>
          A public status page with uptime history and incident notices is part of the launch
          checklist. Until it is live, contact your administrator for the current operational status.
        </div>
      </Prose>
    </PublicLayout>
  );
}

/** Legal placeholders — clearly marked as not-yet-final so they are never mistaken for binding terms. */
export function LegalPage({ kind }: { readonly kind: 'terms' | 'privacy' }): JSX.Element {
  const title = kind === 'terms' ? 'Terms of Service' : 'Privacy Policy';
  return (
    <PublicLayout>
      <Prose title={title}>
        <div className="lc-card" style={{ padding: space[4], display: 'grid', gap: space[3], color: color.fgMuted, fontSize: fontSize.body, lineHeight: 1.6 }}>
          <p style={{ margin: 0, color: color.warn }}>
            This is a placeholder. The final {title.toLowerCase()} will be published before general
            availability and reviewed with legal counsel.
          </p>
          <p style={{ margin: 0 }}>
            {kind === 'privacy'
              ? 'LabConnect is designed so that patient identifiers and result values are not exposed to logs, metrics, or support tooling. Identifiable clinical data is processed only within a laboratory’s own tenant and governed by the agreements established during onboarding.'
              : 'Use of LabConnect during development and pilot is governed by the agreement established with each laboratory. Clinical result release and bidirectional device control are enabled only after explicit, documented laboratory sign-off.'}
          </p>
          <p style={{ margin: 0 }}>
            Questions in the meantime? <Link to="/security" style={{ color: color.primary }}>Read about our security posture</Link>.
          </p>
        </div>
      </Prose>
    </PublicLayout>
  );
}
