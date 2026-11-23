/** Provides stable, centralized query keys for server-backed data. */
export const queryKeys = {
  /** Keys for meetings and meeting-specific data. */
  meetings: {
    all: ['meetings'] as const,
    detail: (id: string) => ['meetings', 'detail', id] as const,
    nextAgenda: (id: string) => ['meetings', 'next-agenda', id] as const,
  },
  /** Keys for action-item collections and per-meeting review data. */
  items: {
    all: ['items'] as const,
    meetingRoot: ['meeting-items'] as const,
    meeting: (meetingId: string) => ['meeting-items', meetingId] as const,
    audit: (itemId: string) => ['audit', itemId] as const,
  },
  /** Keys for aggregate dashboard metrics. */
  dashboard: ['dashboard'] as const,
  /** Keys for health and virtual-clock polling. */
  health: ['health'] as const,
  clock: ['clock'] as const,
}
