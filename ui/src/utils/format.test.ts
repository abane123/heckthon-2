import { describe, expect, it } from 'vitest'
import { formatDate, formatPercent, formatRelativeDays } from './format'

/** Verifies user-facing date and percentage formatting without raw ISO output. */
describe('display formatters', () => {
  /** Keeps API date-only values on their stated calendar day in every timezone. */
  it('formats ISO calendar dates without shifting the day', () => {
    expect(formatDate('2026-11-27')).toBe('27 Nov 2026')
    expect(formatDate('2026-11-27T00:00:00Z')).toBe('27 Nov 2026')
  })

  /** Describes deadlines relative to the local calendar day. */
  it('formats a deadline as a relative day count', () => {
    expect(formatRelativeDays('2026-11-24', new Date(2026, 10, 27))).toBe('3 days overdue')
    expect(formatRelativeDays('2026-11-28', new Date(2026, 10, 27))).toBe('Due in 1 day')
  })

  /** Formats finite percentage values and safely handles invalid numbers. */
  it('formats percentages and invalid values', () => {
    expect(formatPercent(75)).toBe('75%')
    expect(formatPercent(Number.NaN)).toBe('—')
  })
})
