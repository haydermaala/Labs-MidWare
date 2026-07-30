// QR code for TOTP enrolment.
//
// Authenticator apps lead with "scan a QR code" — Google Authenticator's primary flow
// is the camera, and manual key entry is buried. Showing only the secret and the
// otpauth URI therefore pushed operators into copying the URI into some external QR
// tool, which is both bad onboarding and a poor security habit to teach.
//
// The code is generated IN THE BROWSER from the provisioning URI. The secret is never
// sent anywhere to be rendered — no third-party image service, no server round-trip —
// which also keeps us inside the app's `img-src 'self' data:` CSP.

import { useEffect, useState } from 'react';
import QRCode from 'qrcode';
import { color, fontSize, space } from '@lab-connect/ui';

/** Renders `uri` as a scannable QR, or falls back to the manual key on failure. */
export function TotpQr({ uri }: { readonly uri: string }): JSX.Element {
  const [dataUrl, setDataUrl] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let cancelled = false;
    QRCode.toDataURL(uri, {
      errorCorrectionLevel: 'M',
      margin: 2,
      scale: 6,
      color: { dark: '#000000', light: '#ffffff' },
    })
      .then((url) => { if (!cancelled) setDataUrl(url); })
      .catch(() => { if (!cancelled) setFailed(true); });
    return () => { cancelled = true; };
  }, [uri]);

  if (failed) {
    // Never block enrolment on the QR — the secret field below is always present.
    return (
      <p style={{ margin: 0, color: color.fgMuted, fontSize: fontSize.meta }}>
        Could not render the QR code. Use the secret key below to add the account manually.
      </p>
    );
  }

  return (
    <div style={{ display: 'grid', gap: space[2], justifyItems: 'start' }}>
      <div
        style={{
          background: '#fff', padding: space[3], borderRadius: 8,
          // Reserve the final size so the form does not jump when the image resolves.
          minWidth: 188, minHeight: 188, display: 'grid', placeItems: 'center',
        }}
      >
        {dataUrl === null
          ? <span style={{ color: '#666', fontSize: fontSize.meta }}>Generating…</span>
          : <img src={dataUrl} alt="QR code for authenticator app enrolment" width={164} height={164} />}
      </div>
      <p style={{ margin: 0, color: color.fgMuted, fontSize: fontSize.meta }}>
        Scan with Google Authenticator, 1Password, Authy — or add the key manually below.
      </p>
    </div>
  );
}
