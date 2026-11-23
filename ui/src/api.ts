import type {
  ActionItem,
  ActionReplyInput,
  AuditEvent,
  ClockResponse,
  CreateMeetingInput,
  Dashboard,
  HealthResponse,
  Meeting,
  ProblemDetailsDto,
  SimulationResponse,
  TicketCreationResponse,
  UpdateActionItemInput,
} from './types'

/** Describes an HTTP API error in a form safe to show in the interface. */
export class ApiError extends Error {
  /** HTTP response status, or zero when no HTTP response was received. */
  public readonly status: number
  /** Short ProblemDetails title or network-error category. */
  public readonly title: string
  /** Problem detail or actionable network-failure explanation. */
  public readonly detail: string
  /** API trace identifier when the server provided one. */
  public readonly traceId: string | undefined

  /** Creates an API error from an HTTP response or network failure. */
  constructor(status: number, title: string, detail: string, traceId: string | undefined) {
    super(detail || title)
    this.name = 'ApiError'
    this.status = status
    this.title = title
    this.detail = detail
    this.traceId = traceId
  }
}

const requestTimeoutMs = 15_000

/** Builds the API origin from VITE_API_URL, falling back to the current page origin. */
export function getApiBaseUrl(): string {
  return import.meta.env.VITE_API_URL?.trim().replace(/\/+$/, '') || window.location.origin
}

/** Calls the API with caller cancellation and a fixed request timeout. */
async function fetchApi(path: string, init: RequestInit = {}): Promise<Response> {
  const url = `${getApiBaseUrl()}${path}`
  const controller = new AbortController()
  const timeoutId = window.setTimeout(
    () => controller.abort(new DOMException('The API request timed out.', 'TimeoutError')),
    requestTimeoutMs,
  )
  const callerSignal = init.signal
  const forwardCallerAbort = () => controller.abort(callerSignal?.reason)
  callerSignal?.addEventListener('abort', forwardCallerAbort, { once: true })
  if (callerSignal?.aborted) forwardCallerAbort()

  try {
    return await fetch(url, { ...init, signal: controller.signal })
  } catch (error) {
    if (callerSignal?.aborted) throw error
    if (controller.signal.aborted) {
      throw new ApiError(0, 'Request timed out', `The request to ${url} timed out.`, undefined)
    }

    throw new ApiError(0, 'Network error', `Cannot reach the API at ${url}. Is the backend running?`, undefined)
  } finally {
    window.clearTimeout(timeoutId)
    callerSignal?.removeEventListener('abort', forwardCallerAbort)
  }
}

/** Parses an HTTP response as JSON and converts ProblemDetails to ApiError. */
async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers)
  if (!headers.has('Accept')) {
    headers.set('Accept', 'application/json, application/problem+json')
  }
  if (typeof init.body === 'string' && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json')
  }
  const response = await fetchApi(path, {
    ...init,
    headers,
  })

  if (response.status === 204) return undefined as T
  const body = await response.text()
  if (!response.ok) throw toApiError(response.status, body, response.statusText)
  if (!body) return undefined as T

  return JSON.parse(body) as T
}

/** Converts a ProblemDetails response into the UI's typed API error. */
function toApiError(status: number, body: string, statusText: string): ApiError {
  let problem: ProblemDetailsDto | undefined
  try {
    problem = JSON.parse(body) as ProblemDetailsDto
  } catch {
    problem = undefined
  }
  const validationDetail = problem?.errors
    ? Object.values(problem.errors).flat().join(' ')
    : ''
  return new ApiError(
    status,
    problem?.title || statusText || 'Request failed',
    problem?.detail || validationDetail || body || `Request failed with status ${status}.`,
    problem?.traceId,
  )
}

/** Creates a meeting and requests transcript extraction. */
export const createMeeting = (input: CreateMeetingInput, signal?: AbortSignal) =>
  request<Meeting>('/api/meetings', { method: 'POST', body: JSON.stringify(input), signal })

/** Lists meetings for series selection and dashboard agenda context. */
export const getMeetings = (signal?: AbortSignal) => request<Meeting[]>('/api/meetings', { signal })

/** Gets one meeting with its attendees and extracted action items. */
export const getMeeting = (meetingId: string, signal?: AbortSignal) =>
  request<Meeting>(`/api/meetings/${meetingId}`, { signal })

/** Gets open actions from earlier meetings in the same title-based series. */
export const getNextAgenda = (meetingId: string, signal?: AbortSignal) =>
  request<ActionItem[]>(`/api/meetings/${meetingId}/next-agenda`, { signal })

/** Gets the action items belonging to a meeting. */
export const getMeetingItems = (meetingId: string, signal?: AbortSignal) =>
  request<ActionItem[]>(`/api/meetings/${meetingId}/items`, { signal })

/** Lists all action items for the tracker. */
export const getActionItems = (signal?: AbortSignal) => request<ActionItem[]>('/api/items', { signal })

/** Approves a draft action item. */
export const approveItem = (itemId: string) =>
  request<ActionItem>(`/api/items/${itemId}/approve`, { method: 'POST' })

/** Returns an approved action item to draft through the unapprove endpoint. */
export const undoApproval = (itemId: string) =>
  request<ActionItem>(`/api/items/${itemId}/unapprove`, { method: 'POST' })

/** Rejects a draft action item. */
export const rejectItem = (itemId: string) =>
  request<ActionItem>(`/api/items/${itemId}/reject`, { method: 'POST' })

/** Restores a rejected item to draft review. */
export const restoreItem = (itemId: string) =>
  request<ActionItem>(`/api/items/${itemId}/restore`, { method: 'POST' })

/** Updates organizer-editable fields on a draft action item. */
export const updateItem = (itemId: string, fields: UpdateActionItemInput) =>
  request<ActionItem>(`/api/items/${itemId}`, { method: 'PUT', body: JSON.stringify(fields) })

/** Creates task-provider tickets for approved items in a meeting. */
export const createTickets = (meetingId: string) =>
  request<TicketCreationResponse>(`/api/meetings/${meetingId}/create-tickets`, { method: 'POST' })

/** Gets dashboard roll-up metrics. */
export const getDashboard = (signal?: AbortSignal) => request<Dashboard>('/api/dashboard', { signal })

/** Gets API readiness and version information. */
export const getHealth = (signal?: AbortSignal) => request<HealthResponse>('/api/health', { signal })

/** Gets the current demo clock date. */
export const getClock = (signal?: AbortSignal) => request<ClockResponse>('/api/dev/clock', { signal })

/** Advances demo time and runs the follow-up pass. */
export const simulateDays = (days: number) =>
  request<SimulationResponse>(`/api/dev/simulate-days?days=${days}`, { method: 'POST' })

/** Gets the audit history for an action item. */
export const getAudit = (itemId: string, signal?: AbortSignal) =>
  request<AuditEvent[]>(`/api/items/${itemId}/audit`, { signal })

/** Sends an owner quick reply to the action-item API. */
export const sendReply = (itemId: string, input: ActionReplyInput) =>
  request<ActionItem>(`/api/items/${itemId}/reply`, { method: 'POST', body: JSON.stringify(input) })

/** Loads the sample transcript bundled with the API as plain text. */
export async function getSampleTranscript(signal?: AbortSignal): Promise<string> {
  const response = await fetchApi('/api/sample-transcript', {
    signal,
    headers: { Accept: 'text/plain' },
  })
  if (!response.ok) throw toApiError(response.status, await response.text(), response.statusText)
  return response.text()
}
