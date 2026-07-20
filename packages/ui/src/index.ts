// @lab-connect/ui — LabConnect design system (DESIGN.md is the binding source).
// tokens: semantic constants · theme: CSS custom properties for both themes ·
// primitives: Button/Field/StatusBadge/Card with the full interaction matrix.

export * from './tokens';
export { themeCss } from './theme';
export { componentCss, Button, Field, StatusBadge, Card } from './primitives';
export type { ButtonProps, FieldProps, StatusKind } from './primitives';

import { themeCss as t } from './theme';
import { componentCss as c } from './primitives';

/** Everything an app injects once at startup. */
export const uiCss: string = t + c;
