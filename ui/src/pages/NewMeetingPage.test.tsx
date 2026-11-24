import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { axe } from 'jest-axe'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it } from 'vitest'
import { MemoryRouter } from 'react-router-dom'
import type { ReactNode } from 'react'
import { ToastProvider } from '../components/DesignSystem'
import NewMeetingPage from './NewMeetingPage'

const draftStorageKey = 'meeting-actions-new-meeting-draft'
const server = setupServer()

/** Mounts the meeting form with an isolated query client and router. */
function renderPage() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })

  /** Wraps page content in the shared providers required by the production shell. */
  function Providers({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <MemoryRouter>{children}</MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>
    )
  }

  return render(<NewMeetingPage />, { wrapper: Providers })
}

/** Verifies validation, file constraints, saved drafts, and accessibility of meeting entry. */
describe('NewMeetingPage', () => {
  beforeAll(() => server.listen({ onUnhandledRequest: 'error' }))
  afterAll(() => server.close())

  beforeEach(() => {
    sessionStorage.clear()
    server.use(http.get('*/api/llm/info', () => HttpResponse.json({
      provider: 'Stub',
      model: 'Built-in rules',
      status: 'Healthy',
      message: 'Using the built-in rules engine; no external model is required.',
      fallbackToStub: false,
      maxTranscriptCharsPerChunk: 12000,
    })))
  })

  afterEach(() => {
    cleanup()
    sessionStorage.clear()
    server.resetHandlers()
  })

  /** Validates each required field in order and focuses the first invalid control. */
  it('shows inline validation and focuses the first invalid field', async () => {
    const user = userEvent.setup()
    renderPage()

    await user.click(screen.getByRole('button', { name: 'Extract action items' }))
    const title = screen.getByRole('textbox', { name: /Meeting title/ })
    expect(await screen.findByText('Enter a meeting title.')).toBeTruthy()
    expect(document.activeElement).toBe(title)
    expect(title.getAttribute('aria-describedby')).toBe('title-error')

    await user.type(title, 'Release readiness')
    await user.click(screen.getByRole('button', { name: 'Extract action items' }))
    const attendees = screen.getByRole('textbox', { name: 'Attendees' })
    expect(await screen.findByText('Add at least one attendee.')).toBeTruthy()
    expect(document.activeElement).toBe(attendees)

    await user.type(attendees, 'Priya{enter}')
    await user.click(screen.getByRole('button', { name: 'Extract action items' }))
    const transcript = screen.getByRole('textbox', { name: 'Transcript' })
    expect(await screen.findByText('Paste or upload a transcript.')).toBeTruthy()
    expect(document.activeElement).toBe(transcript)
  })

  /** Shows the configured engine and disables extraction when Ollama is unavailable without fallback. */
  it('shows the active Ollama engine and unavailable warning when fallback is off', async () => {
    server.use(http.get('*/api/llm/info', () => HttpResponse.json({
      provider: 'Ollama',
      model: 'llama3.1:8b',
      status: 'Degraded',
      message: "Model not installed: run 'ollama pull llama3.1:8b'",
      fallbackToStub: false,
      maxTranscriptCharsPerChunk: 12000,
    })))
    renderPage()

    expect(await screen.findByText('Analysis engine: Ollama llama3.1:8b')).toBeTruthy()
    expect(await screen.findByText(/Model not installed: run 'ollama pull llama3.1:8b'/)).toBeTruthy()
    await waitFor(() => expect(
      (screen.getByRole('button', { name: 'Extract action items' }) as HTMLButtonElement).disabled,
    ).toBe(true))
  })

  /** Shows degraded Ollama details and keeps extraction available when Stub fallback is enabled. */
  it('allows extraction when Stub fallback is configured', async () => {
    server.use(http.get('*/api/llm/info', () => HttpResponse.json({
      provider: 'Ollama',
      model: 'llama3.1:8b',
      status: 'Degraded',
      message: 'Cannot reach Ollama at http://localhost:11434. Is it running?',
      fallbackToStub: true,
      maxTranscriptCharsPerChunk: 12000,
    })))
    renderPage()

    expect(await screen.findByText('Analysis engine: Ollama llama3.1:8b')).toBeTruthy()
    expect(await screen.findByText(/Stub fallback is enabled/)).toBeTruthy()
    await waitFor(() => expect(
      (screen.getByRole('button', { name: 'Extract action items' }) as HTMLButtonElement).disabled,
    ).toBe(false))
  })

  /** Displays part-by-part progress while a multi-chunk Ollama request remains pending. */
  it('shows chunk-aware analysis progress for a long transcript', async () => {
    const user = userEvent.setup()
    server.use(
      http.get('*/api/llm/info', () => HttpResponse.json({
        provider: 'Ollama',
        model: 'llama3.1:8b',
        status: 'Healthy',
        message: 'Ollama is ready.',
        fallbackToStub: false,
        maxTranscriptCharsPerChunk: 20,
      })),
      http.post('*/api/meetings', async ({ request }) => {
        await new Promise<void>((resolve) => request.signal.addEventListener('abort', () => resolve(), { once: true }))
        return HttpResponse.json({ id: 'meeting-1' }, { status: 201 })
      }),
    )
    renderPage()
    await user.type(screen.getByRole('textbox', { name: /Meeting title/ }), 'Long transcript')
    await user.type(screen.getByRole('textbox', { name: 'Attendees' }), 'Priya{enter}')
    await user.type(screen.getByRole('textbox', { name: 'Transcript' }), 'This transcript line is long enough to split into three chunks.')
    await user.click(screen.getByRole('button', { name: 'Extract action items' }))

    expect(await screen.findByText('Analysing part 2 of 4', {}, { timeout: 5000 })).toBeTruthy()
    await user.click(screen.getByRole('button', { name: 'Cancel extraction' }))
    expect(await screen.findByText('Extraction cancelled. Your draft is still saved.')).toBeTruthy()
  })

  /** Rejects an unsupported extension and files larger than one mebibyte. */
  it('validates transcript upload extension and size', async () => {
    renderPage()
    const fileInput = screen.getByLabelText('Upload transcript file')

    fireEvent.change(fileInput, {
      target: { files: [new File(['transcript'], 'notes.pdf', { type: 'application/pdf' })] },
    })
    expect(await screen.findByText('Choose a .txt or .vtt transcript file.')).toBeTruthy()

    fireEvent.change(fileInput, {
      target: { files: [new File([new Uint8Array(1_048_577)], 'large.txt', { type: 'text/plain' })] },
    })
    expect(await screen.findByText('The transcript file must be 1 MB or smaller.')).toBeTruthy()
  })

  /** Saves meeting fields in session storage and offers to restore them after remount. */
  it('persists entered values and restores the saved draft', async () => {
    const user = userEvent.setup()
    const firstRender = renderPage()
    await user.type(screen.getByRole('textbox', { name: /Meeting title/ }), 'Release readiness')
    await user.type(screen.getByRole('textbox', { name: 'Attendees' }), 'Priya{enter}')
    await user.type(screen.getByRole('textbox', { name: 'Transcript' }), 'Priya will review limits.')

    await waitFor(() => {
      expect(JSON.parse(sessionStorage.getItem(draftStorageKey) ?? '{}')).toMatchObject({
        title: 'Release readiness',
        attendees: ['Priya'],
        transcript: 'Priya will review limits.',
      })
    })
    firstRender.unmount()

    renderPage()
    await user.click(screen.getByRole('button', { name: 'Restore draft' }))

    expect((screen.getByRole('textbox', { name: /Meeting title/ }) as HTMLInputElement).value).toBe('Release readiness')
    expect(screen.getByText('Priya')).toBeTruthy()
    expect((screen.getByRole('textbox', { name: 'Transcript' }) as HTMLTextAreaElement).value)
      .toBe('Priya will review limits.')
  })

  /** Shows accessible error feedback when meeting creation fails and retains form values. */
  it('keeps entered values after a failed submit and presents a retry action', async () => {
    const user = userEvent.setup()
    server.use(http.post('*/api/meetings', () => HttpResponse.json({
      title: 'Meeting title is invalid.',
      detail: 'The service could not process this meeting.',
      status: 400,
    }, { status: 400, headers: { 'Content-Type': 'application/problem+json' } })))
    renderPage()

    await user.type(screen.getByRole('textbox', { name: /Meeting title/ }), 'Release readiness')
    await user.type(screen.getByRole('textbox', { name: 'Attendees' }), 'Priya{enter}')
    await user.type(screen.getByRole('textbox', { name: 'Transcript' }), 'Priya will review limits.')
    await user.click(screen.getByRole('button', { name: 'Extract action items' }))

    expect(await screen.findByRole('alert')).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Retry' })).toBeTruthy()
    expect((screen.getByRole('textbox', { name: /Meeting title/ }) as HTMLInputElement).value)
      .toBe('Release readiness')
    expect((screen.getByRole('textbox', { name: 'Transcript' }) as HTMLTextAreaElement).value)
      .toBe('Priya will review limits.')
  })

  /** Locks editable controls during extraction and preserves the draft when the user cancels. */
  it('disables form fields while pending and cancels without losing entered text', async () => {
    const user = userEvent.setup()
    server.use(http.post('*/api/meetings', async ({ request }) => {
      await new Promise<void>((resolve) => request.signal.addEventListener('abort', () => resolve(), { once: true }))
      return HttpResponse.json({ id: 'meeting-1' }, { status: 201 })
    }))
    renderPage()

    await user.type(screen.getByRole('textbox', { name: /Meeting title/ }), 'Release readiness')
    await user.type(screen.getByRole('textbox', { name: 'Attendees' }), 'Priya{enter}')
    await user.type(screen.getByRole('textbox', { name: 'Transcript' }), 'Priya will review limits.')
    await user.click(screen.getByRole('button', { name: 'Extract action items' }))

    const title = screen.getByRole('textbox', { name: /Meeting title/ }) as HTMLInputElement
    await waitFor(() => expect(title.matches(':disabled')).toBe(true))
    await user.click(screen.getByRole('button', { name: 'Cancel extraction' }))

    expect(await screen.findByText('Extraction cancelled. Your draft is still saved.')).toBeTruthy()
    expect(title.value).toBe('Release readiness')
    expect(title.matches(':disabled')).toBe(false)
  })

  /** Runs axe against the rendered New Meeting page and asserts no accessibility violations. */
  it('has no axe accessibility violations', async () => {
    const { container } = renderPage()

    expect((await axe(container)).violations).toEqual([])
  })
})
