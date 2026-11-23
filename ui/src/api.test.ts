import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError, createMeeting, getHealth } from './api'
import type { CreateMeetingInput } from './types'

/** Covers the API client's request shape and typed error conversion. */
describe('API client', () => {
  afterEach(() => {
    vi.useRealTimers()
    vi.unstubAllGlobals()
    vi.restoreAllMocks()
  })

  /** Sends JSON mutations with the media type required by the ASP.NET API. */
  it('sets the JSON content type when creating a meeting', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({
      id: 'meeting-1',
      title: 'Planning',
      meetingDate: '2026-11-17',
      transcript: 'Transcript',
      attendees: ['Priya'],
      items: [],
    }), { status: 201 }))
    vi.stubGlobal('fetch', fetchMock)
    const input: CreateMeetingInput = {
      title: 'Planning',
      date: '2026-11-17',
      attendees: ['Priya'],
      transcript: 'Transcript',
    }

    await createMeeting(input)

    const request = fetchMock.mock.calls[0]?.[1] as RequestInit
    expect(new Headers(request.headers).get('Content-Type')).toBe('application/json')
    expect(JSON.parse(String(request.body))).toEqual(input)
  })

  /** Converts RFC 7807 fields, including traceId, into ApiError properties. */
  it('parses ProblemDetails responses into ApiError', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({
      title: 'Validation failed',
      detail: 'A meeting title is required.',
      status: 400,
      traceId: 'trace-123',
    }), { status: 400, headers: { 'Content-Type': 'application/problem+json' } })))

    const result = getHealth()
    await expect(result).rejects.toMatchObject({
      name: 'ApiError',
      status: 400,
      title: 'Validation failed',
      detail: 'A meeting title is required.',
      traceId: 'trace-123',
    } satisfies Partial<ApiError>)
  })

  /** Gives users the configured API URL and startup hint after a network failure. */
  it('turns network failures into an actionable ApiError', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')))

    await expect(getHealth()).rejects.toMatchObject({
      name: 'ApiError',
      status: 0,
      title: 'Network error',
      detail: expect.stringMatching(/^Cannot reach the API at .+ Is the backend running\?$/),
    })
  })

  /** Stops a request after the configured 15-second timeout and reports it as ApiError. */
  it('aborts a stalled request after fifteen seconds', async () => {
    vi.useFakeTimers()
    vi.stubGlobal('fetch', vi.fn((_url: RequestInfo | URL, init?: RequestInit) =>
      new Promise<Response>((_resolve, reject) => {
        init?.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')), { once: true })
      })))

    const pending = getHealth()
    const rejection = expect(pending).rejects.toMatchObject({
      name: 'ApiError',
      status: 0,
      title: 'Request timed out',
    })
    await vi.advanceTimersByTimeAsync(15_000)
    await rejection
  })

  /** Preserves caller-requested cancellation instead of disguising it as a network error. */
  it('forwards AbortSignal cancellation to the fetch request', async () => {
    vi.stubGlobal('fetch', vi.fn((_url: RequestInfo | URL, init?: RequestInit) =>
      new Promise<Response>((_resolve, reject) => {
        init?.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')), { once: true })
      })))
    const controller = new AbortController()
    const pending = getHealth(controller.signal)

    controller.abort()

    await expect(pending).rejects.toMatchObject({ name: 'AbortError' })
  })
})
