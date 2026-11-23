import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { axe } from 'jest-axe'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { MemoryRouter } from 'react-router-dom'
import { getClock, getDashboard, getHealth, resetDemoData, simulateDays } from './api'
import App from './App'
import { ToastProvider } from './components/DesignSystem'
import type { ClockResponse, Dashboard, DemoResetResponse, HealthResponse, SimulationResponse } from './types'
import { ThemeProvider } from './theme/ThemeProvider'

vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./api')>()
  return {
    ...actual,
    getClock: vi.fn(),
    getDashboard: vi.fn(),
    getHealth: vi.fn(),
    resetDemoData: vi.fn(),
    simulateDays: vi.fn(),
  }
})

const healthyResponse: HealthResponse = {
  status: 'Healthy',
  version: '1.0.0',
  utcTime: '2026-11-17T00:00:00Z',
}
const clockResponse: ClockResponse = { currentDate: '2026-11-17T00:00:00Z' }
const dashboardResponse: Dashboard = { onTimeRate: 0, slipRate: 0, openItemsPerOwner: {} }

/** Mounts the full shell with isolated query state and an in-memory route. */
function renderApp(path = '/') {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, refetchInterval: false }, mutations: { retry: false } },
  })
  const result = render(
    <ThemeProvider>
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <MemoryRouter initialEntries={[path]}>
            <App />
          </MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>
    </ThemeProvider>,
  )
  return { ...result, queryClient }
}

/** Sets deterministic API responses and clears persisted presentation state. */
describe('application shell', () => {
  beforeEach(() => {
    localStorage.clear()
    document.documentElement.removeAttribute('data-theme')
    vi.clearAllMocks()
    vi.mocked(getHealth).mockResolvedValue(healthyResponse)
    vi.mocked(resetDemoData).mockResolvedValue({
      currentDate: '2026-11-23T00:00:00Z',
      meetingCount: 1,
      actionItemCount: 5,
    } satisfies DemoResetResponse)
    vi.mocked(getClock).mockResolvedValue(clockResponse)
    vi.mocked(getDashboard).mockResolvedValue(dashboardResponse)
    vi.mocked(simulateDays).mockResolvedValue({
      utcNow: '2026-11-18T00:00:00Z',
      advancedDays: 1,
      remindersSent: 2,
    } satisfies SimulationResponse)
  })

  afterEach(() => {
    cleanup()
    localStorage.clear()
    document.documentElement.removeAttribute('data-theme')
  })

  /** Marks the API status dot red when the health query cannot reach the server. */
  it('shows an offline API status when health checks fail', async () => {
    vi.mocked(getHealth).mockRejectedValue(new Error('API unavailable'))
    renderApp()

    await waitFor(
      () => expect(document.querySelector('.api-status-offline')).not.toBeNull(),
      { timeout: 4_000 },
    )
    expect(screen.getByRole('tooltip', { name: /API unavailable/ })).toBeTruthy()
  })

  /** Shows a pending spinner, reminder feedback, and refreshed simulated time. */
  it('shows a simulation toast and refreshes the clock query', async () => {
    const user = userEvent.setup()
    let resolveSimulation!: (result: SimulationResponse) => void
    vi.mocked(simulateDays).mockReturnValue(new Promise((resolve) => {
      resolveSimulation = resolve
    }))
    vi.mocked(getClock)
      .mockResolvedValueOnce(clockResponse)
      .mockResolvedValue({ currentDate: '2026-11-18T00:00:00Z' })
    renderApp()

    await screen.findByText('17 Nov 2026')
    const simulateButton = screen.getByRole('button', { name: /Simulate tomorrow/ })
    await user.click(simulateButton)
    expect(simulateButton.hasAttribute('disabled')).toBe(true)
    expect(screen.getByRole('status', { name: 'Loading' })).toBeTruthy()
    resolveSimulation({
      utcNow: '2026-11-18T00:00:00Z',
      advancedDays: 1,
      remindersSent: 2,
    })

    expect(await screen.findByText('Moved to 18 Nov 2026. 2 reminders sent.')).toBeTruthy()
    await waitFor(() => expect(vi.mocked(getClock).mock.calls.length).toBeGreaterThanOrEqual(2))
    expect(screen.getByText('18 Nov 2026')).toBeTruthy()
  })

  /** Confirms a development data reset, invalidates the clock, and shows its toast. */
  it('resets demo data only after confirmation and refreshes server queries', async () => {
    const user = userEvent.setup()
    vi.mocked(getClock)
      .mockResolvedValueOnce(clockResponse)
      .mockResolvedValue({ currentDate: '2026-11-23T00:00:00Z' })
    renderApp()

    await user.click(screen.getByRole('button', { name: 'Reset demo data' }))
    const dialog = await screen.findByRole('dialog', { name: 'Reset demo data' })
    await user.click(within(dialog).getByRole('button', { name: 'Reset demo data' }))

    expect(vi.mocked(resetDemoData)).toHaveBeenCalledOnce()
    expect(await screen.findByText('Demo data reset. 5 action items are ready.')).toBeTruthy()
    await waitFor(() => expect(vi.mocked(getClock).mock.calls.length).toBeGreaterThanOrEqual(2))
  })

  /** Persists the selected theme when the header control toggles it. */
  it('persists theme changes to localStorage', async () => {
    const user = userEvent.setup()
    renderApp()

    await user.click(screen.getByRole('button', { name: 'Switch to dark theme' }))

    await waitFor(() => expect(localStorage.getItem('meeting-actions-theme')).toBe('dark'))
    expect(document.documentElement.dataset.theme).toBe('dark')
  })

  /** Opens and closes navigation using the compact menu control at 360px. */
  it('supports the mobile navigation menu at a narrow viewport', async () => {
    const user = userEvent.setup()
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 360 })
    renderApp()

    await user.click(screen.getByRole('button', { name: 'Open navigation menu' }))
    const navigation = screen.getByRole('navigation', { name: 'Main navigation' })
    expect(navigation.classList.contains('nav-open')).toBe(true)
    expect(screen.getByRole('link', { name: 'New meeting' }).getAttribute('aria-current')).toBe('page')

    await user.click(screen.getByRole('link', { name: 'Meeting Actions' }))
    expect(screen.getByRole('button', { name: 'Open navigation menu' }).getAttribute('aria-expanded')).toBe('false')
  })

  /** Verifies the rendered shell and its initial page against axe accessibility rules. */
  it('has no axe accessibility violations on the shell', async () => {
    const { container } = renderApp()

    expect((await axe(container)).violations).toEqual([])
  })
})
