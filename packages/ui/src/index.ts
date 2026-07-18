// Shared UI foundation. Phase 1 scaffold: design tokens only. React components
// are added in Phase 5 once the desktop/web shells stabilize.

export const tokens = {
  color: {
    // Placeholder neutral palette; finalized with design in a later phase.
    bg: '#0b0d10',
    fg: '#e6e9ee',
    accent: '#3b82f6',
    danger: '#ef4444',
  },
  space: [0, 4, 8, 12, 16, 24, 32] as const,
} as const;

export type Tokens = typeof tokens;
