/** Declares the small jest-axe surface used by the UI accessibility test. */
declare module 'jest-axe' {
  /** Runs axe-core against a rendered element and returns detected violations. */
  export function axe(container: Element): Promise<{ violations: unknown[] }>
}
