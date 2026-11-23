/** Mirrors the string-valued workflow enum returned by the API. */
export type ActionStatus = 'Draft' | 'Approved' | 'Created' | 'InProgress' | 'Overdue' | 'Escalated' | 'Done' | 'Rejected'

/** Mirrors the API action-item DTO. */
export type ActionItem = {
  id: string
  meetingId: string
  title: string
  ownerId: string | null
  ownerName: string | null
  suggestedOwnerName: string | null
  dueDate: string | null
  dueDateText: string
  status: ActionStatus
  confidence: number
  sourceQuote: string
  timestamp: string
  needsInput: string[]
  externalTicketId: string | null
  slipCount: number
}

/** Mirrors the meeting-creation request accepted by the API. */
export type CreateMeetingInput = {
  title: string
  date: string
  attendees: string[]
  transcript: string
}

/** Mirrors a meeting and its action items returned by the API. */
export type Meeting = {
  id: string
  title: string
  meetingDate: string
  transcript: string
  attendees: string[]
  items: ActionItem[]
  decisions?: string[] | null
  analysis?: MeetingAnalysis | null
}

/** Describes the extractor and deterministic post-processing for one meeting. */
export type MeetingAnalysis = {
  provider: string
  model: string
  durationMs: number
  droppedItems: number
  chunkCount: number
  warnings?: string[]
}

/** Mirrors the active transcript-analysis provider's health endpoint. */
export type LlmInfo = {
  provider: string
  model: string
  status: string
  message: string
  fallbackToStub: boolean
  maxTranscriptCharsPerChunk: number
}

/** Mirrors the API user DTO. */
export type User = {
  id: string
  name: string
  email: string
  externalUserId: string | null
}

/** Mirrors a recorded notification follow-up. */
export type FollowUp = {
  id: string
  actionItemId: string
  sentAt: string
  channel: string
  response: string | null
}

/** Mirrors an audit event returned by the API. */
export type AuditEvent = {
  id: string
  actionItemId: string
  eventType: string
  actor: string
  createdAt: string
}

/** Mirrors the aggregate metrics returned by the dashboard API. */
export type Dashboard = {
  onTimeRate: number | null
  slipRate: number | null
  openItemsPerOwner: Record<string, number>
}

/** Mirrors the result of resetting the development demo database and clock. */
export type DemoResetResponse = {
  currentDate: string
  meetingCount: number
  actionItemCount: number
}

/** Mirrors the health endpoint response. */
export type HealthResponse = {
  status: string
  version: string
  utcTime: string
  llm?: {
    provider: string
    status: string
    model: string
  }
}

/** Mirrors the virtual-clock endpoint response. */
export type ClockResponse = {
  currentDate: string
}

/** Mirrors the existing simulate-days endpoint response. */
export type SimulationResponse = {
  utcNow: string
  advancedDays: number
  /** Number of reminder notifications sent during this simulation. */
  remindersSent: number
}

/** Mirrors the quick-reply request accepted by the API. */
export type ActionReplyInput = {
  action: 'Done' | 'PushToFriday' | 'Blocked' | 'Reassign'
  reassignTo?: string
}

/** Mirrors editable action fields accepted by the API. */
export type UpdateActionItemInput = {
  title: string
  ownerName: string | null
  dueDate: string | null
}

/** Mirrors the API's per-item ticket creation failure response. */
export type TicketCreationFailure = {
  actionItemId: string
  title: string
  detail: string
}

/** Mirrors the API response for successful and failed ticket creation attempts. */
export type TicketCreationResponse = {
  createdItems: ActionItem[]
  failures: TicketCreationFailure[]
}

/** Mirrors RFC 7807 ProblemDetails fields used by the API. */
export type ProblemDetailsDto = {
  type?: string
  title?: string
  status?: number
  detail?: string
  instance?: string
  traceId?: string
  errors?: Record<string, string[]>
}
