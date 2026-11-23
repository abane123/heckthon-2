/** Formats valid dates using the product's en-GB display format. */
export function formatDate(value: string | Date | null | undefined): string {
  if (!value) return '—'
  const date = parseDate(value)
  if (Number.isNaN(date.getTime())) return '—'
  return new Intl.DateTimeFormat('en-GB', { day: '2-digit', month: 'short', year: 'numeric' }).format(date)
}

/** Formats a deadline relative to today without exposing raw ISO dates. */
export function formatRelativeDays(value: string | Date | null | undefined, now = new Date()): string {
  if (!value) return 'No due date'
  const due = parseDate(value)
  if (Number.isNaN(due.getTime())) return 'No due date'
  const today = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime()
  const dueDay = new Date(due.getFullYear(), due.getMonth(), due.getDate()).getTime()
  const days = Math.round((dueDay - today) / 86_400_000)
  if (days === 0) return 'Due today'
  if (days < 0) {
    const overdueDays = Math.abs(days)
    return `${overdueDays} ${overdueDays === 1 ? 'day' : 'days'} overdue`
  }
  return `Due in ${days} ${days === 1 ? 'day' : 'days'}`
}

/** Formats a numeric percentage with a compact en-GB representation. */
export function formatPercent(value: number): string {
  if (!Number.isFinite(value)) return '—'
  return `${new Intl.NumberFormat('en-GB', { maximumFractionDigits: 0 }).format(value)}%`
}

/** Parses API ISO calendar dates as local calendar days to avoid timezone shifts. */
function parseDate(value: string | Date): Date {
  if (value instanceof Date) return value
  const calendarDate = /^(\d{4})-(\d{2})-(\d{2})(?:$|T)/.exec(value)
  if (!calendarDate) return new Date(value)
  const year = Number(calendarDate[1])
  const month = Number(calendarDate[2])
  const day = Number(calendarDate[3])
  const parsed = new Date(year, month - 1, day)
  if (parsed.getFullYear() !== year || parsed.getMonth() !== month - 1 || parsed.getDate() !== day) {
    return new Date(Number.NaN)
  }
  return parsed
}
