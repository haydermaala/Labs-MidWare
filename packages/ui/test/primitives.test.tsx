import { render, screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { Button, Field, StatusBadge, Card, uiCss, tokens, color, space } from '../src/index';

describe('design system', () => {
  it('uiCss defines both themes and the focus ring', () => {
    expect(uiCss).toContain('--lc-primary');
    expect(uiCss).toContain('[data-theme="dark"]');
    expect(uiCss).toContain('prefers-color-scheme: dark');
    expect(uiCss).toContain(':focus-visible');
    expect(uiCss).toContain('prefers-reduced-motion');
  });

  it('legacy tokens shape stays compatible', () => {
    expect(tokens.color.bg).toBeTruthy();
    expect(tokens.space.length).toBe(9);
    expect(space[2]).toBe(8);
    expect(color.primary).toBe('var(--lc-primary)');
  });

  it('Button renders variants, disables while loading, and marks busy', () => {
    const { rerender } = render(<Button variant="danger">Delete</Button>);
    const btn = screen.getByRole('button', { name: /delete/i });
    expect(btn.className).toContain('lc-btn--danger');
    expect((btn as HTMLButtonElement).disabled).toBe(false);

    rerender(<Button loading>Saving</Button>);
    const busy = screen.getByRole('button', { name: /saving/i });
    expect((busy as HTMLButtonElement).disabled).toBe(true);
    expect(busy.getAttribute('aria-busy')).toBe('true');
  });

  it('Field wires label, error, and aria-invalid', () => {
    render(<Field label="Email" error="required" defaultValue="" />);
    const input = screen.getByLabelText('Email');
    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(screen.getByRole('alert').textContent).toBe('required');
  });

  it('StatusBadge is color-independent: icon + text for every state', () => {
    for (const status of ['online', 'offline', 'never', 'decommissioned', 'pending'] as const) {
      const { container, unmount } = render(<StatusBadge status={status} />);
      expect(container.querySelector('svg')).toBeTruthy();
      expect(container.textContent!.length).toBeGreaterThan(0);
      unmount();
    }
  });

  it('Card renders children on a bordered surface', () => {
    const { container } = render(<Card>content</Card>);
    expect(container.querySelector('.lc-card')!.textContent).toBe('content');
  });
});
