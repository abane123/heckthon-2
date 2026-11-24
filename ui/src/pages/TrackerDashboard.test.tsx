import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse, delay } from 'msw'
import { setupServer } from 'msw/node'
import type { ReactElement } from 'react'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { axe } from 'jest-axe'
import type { ActionItem, AuditEvent, Dashboard, Meeting } from '../types'
import TrackerPage from './TrackerPage'
import DashboardPage from './DashboardPage'
import { ToastProvider } from '../components/DesignSystem'
import '../index.css'
import '../App.css'

/** Keeps all mocked HTTP requests aligned with the UI's configured API origin. */
const apiOrigin = 'http://localhost:5000'
/** Holds mutable test actions so reply tests can observe cache refreshes. */
let activeItems: ActionItem[] = []
/** Counts quick-reply requests observed by the mock API. */
let replyCalls = 0

/** Builds a stable action-item fixture with overrides for tracker scenarios. */
function createItem(overrides: Partial<ActionItem> = {}): ActionItem {
  return {
    id: 'item-1',
    meetingId: 'meeting-1',
    title: 'Review retry policy',
    ownerId: null,
    ownerName: 'Priya',
    suggestedOwnerName: null,
    dueDate: '2026-11-27T00:00:00Z',
    dueDateText: 'by Friday',
    status: 'Overdue',
    confidence: 0.8,
    sourceQuote: 'Priya will review the retry policy by Friday.',
    timestamp: '02:04',
    needsInput: [],
    externalTicketId: 'ADO-77',
    slipCount: 2,
    ...overrides,
  }
}

/** Provides one chronological audit event for the tracker drawer. */
const auditEvents: AuditEvent[] = [{
  id: 'audit-1',
  actionItemId: 'item-1',
  eventType: 'Action approved',
  actor: 'Organizer',
  createdAt: '2026-11-18T10:00:00Z',
}]
/** Provides deterministic roll-up values for dashboard assertions. */
const dashboard: Dashboard = {
  onTimeRate: 0.8,
  slipRate: 0.2,
  openItemsPerOwner: { Priya: 1, Alex: 1 },
}
/** Provides a meeting context for next-agenda requests. */
const latestMeeting: Meeting = {
  id: 'meeting-2',
  title: 'Release readiness',
  meetingDate: '2026-11-29T00:00:00Z',
  transcript: '',
  attendees: ['Priya', 'Alex'],
  items: [],
}

/** Intercepts the API routes used by the tracker and dashboard pages. */
const server = setupServer(
  http.get(`${apiOrigin}/api/items`, () => HttpResponse.json(activeItems)),
  http.get(`${apiOrigin}/api/dev/clock`, () => HttpResponse.json({ currentDate: '2026-11-30T00:00:00Z' })),
  http.get(`${apiOrigin}/api/items/:id/audit`, () => HttpResponse.json(auditEvents)),
  http.post(`${apiOrigin}/api/items/:id/reply`, async ({ params, request }) => {
    replyCalls += 1
    const body = await request.json() as { action: string }
    activeItems = activeItems.map((item) => item.id === params.id && body.action === 'Done'
      ? { ...item, status: 'Done' }
      : item)
    return HttpResponse.json(activeItems.find((item) => item.id === params.id))
  }),
  http.get(`${apiOrigin}/api/dashboard`, () => HttpResponse.json(dashboard)),
  http.get(`${apiOrigin}/api/meetings`, () => HttpResponse.json([latestMeeting])),
  http.get(`${apiOrigin}/api/meetings/:id/next-agenda`, () => HttpResponse.json([
    createItem({ id: 'carry-over', title: 'Carry-over action', slipCount: 3 }),
  ])),
)

/** Exposes the current query string so URL persistence is directly testable. */
function LocationSearch() {
  const location = useLocation()
  return <output data-testid="location-search">{location.search}</output>
}

/** Renders a page with isolated query state, toast support, and a memory URL. */
function renderPage(page: ReactElement, initialEntry: string) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false } },
  })
  const rendered = render(
    <QueryClientProvider client={queryClient}>
      <ToastProvider>
        <MemoryRouter initialEntries={[initialEntry]}>
          <Routes><Route path="*" element={page} /></Routes>
          <LocationSearch />
        </MemoryRouter>
      </ToastProvider>
    </QueryClientProvider>,
  )
  return { ...rendered, queryClient }
}

/** Starts and stops the shared MSW server around this page-test suite. */
beforeAll(() => server.listen({ onUnhandledRequest: 'error' }))
afterAll(() => server.close())
beforeEach(() => {
  activeItems = [createItem(), createItem({
    id: 'item-2',
    title: 'Check the cache',
    ownerName: 'Alex',
    status: 'InProgress',
    dueDate: '2026-12-03T00:00:00Z',
    externalTicketId: null,
    slipCount: 0,
  })]
  replyCalls = 0
})
afterEach(() => {
  cleanup()
  server.resetHandlers()
  vi.restoreAllMocks()
})

/** Covers URL-filtered tracking, audit/replies, pagination, and dashboard states. */
describe('TrackerPage', () => {
  /** Applies tracker filter values from the URL and preserves them across a fresh render. */
  it('syncs filters to the URL and restores them on reload', async () => {
    const user = userEvent.setup()
    const path = '/tracker?status=Overdue&owner=Priya&q=Retry&overdue=1'
    const firstRender = renderPage(<TrackerPage />, path)

    expect(await screen.findByRole('button', { name: 'Review retry policy' })).toBeTruthy()
    expect(screen.queryByRole('button', { name: 'Check the cache' })).toBeNull()
    const search = screen.getByRole('searchbox', { name: 'Search' })
    await user.clear(search)
    await user.type(search, 'Missing')
    await waitFor(() => expect(screen.getByTestId('location-search').textContent).toContain('q=Missing'))
    const updatedPath = `/tracker${screen.getByTestId('location-search').textContent}`
    firstRender.unmount()

    renderPage(<TrackerPage />, updatedPath)
    expect((screen.getByRole('searchbox', { name: 'Search' }) as HTMLInputElement).value).toBe('Missing')
  })

  /** Toggles status chips and writes repeated status values to the current URL. */
  it('syncs status chip selections to the URL', async () => {
    const user = userEvent.setup()
    renderPage(<TrackerPage />, '/tracker')

    await screen.findByRole('button', { name: 'Review retry policy' })
    const overdueChip = screen.getByRole('button', { name: 'Overdue 1' })
    await user.click(overdueChip)

    await waitFor(() => expect(screen.getByTestId('location-search').textContent).toContain('status=Overdue'))
    expect(overdueChip.getAttribute('aria-pressed')).toBe('true')
  })

  /** Hides unreviewed Draft rows by default and exposes them through the URL-backed toggle. */
  it('hides drafts until Include drafts is enabled', async () => {
    const user = userEvent.setup()
    activeItems = [
      createItem({ id: 'draft-1', title: 'Unreviewed action', status: 'Draft' }),
      createItem({ id: 'active-1', title: 'Approved action', status: 'Approved' }),
    ]
    renderPage(<TrackerPage />, '/tracker')

    expect(await screen.findByRole('button', { name: 'Approved action' })).toBeTruthy()
    expect(screen.queryByRole('button', { name: 'Unreviewed action' })).toBeNull()
    await user.click(screen.getByRole('checkbox', { name: 'Include drafts' }))

    expect(await screen.findByRole('button', { name: 'Unreviewed action' })).toBeTruthy()
    expect(screen.getByTestId('location-search').textContent).toContain('drafts=1')
  })

  /** Filters by meeting title and persists the selected meeting identifier in the URL. */
  it('filters tracker items by meeting', async () => {
    const user = userEvent.setup()
    activeItems = [
      createItem({ id: 'meeting-one-item', title: 'Release checklist', meetingId: 'meeting-1' }),
      createItem({ id: 'meeting-two-item', title: 'Smoke tests', meetingId: 'meeting-2' }),
    ]
    server.use(http.get(`${apiOrigin}/api/meetings`, () => HttpResponse.json([
      { ...latestMeeting, id: 'meeting-1', title: 'Planning' },
      { ...latestMeeting, id: 'meeting-2', title: 'Release readiness' },
    ])))
    renderPage(<TrackerPage />, '/tracker')

    await screen.findByRole('button', { name: 'Release checklist' })
    await user.selectOptions(screen.getByRole('combobox', { name: 'Meeting' }), 'meeting-2')

    expect(await screen.findByRole('button', { name: 'Smoke tests' })).toBeTruthy()
    expect(screen.queryByRole('button', { name: 'Release checklist' })).toBeNull()
    expect(screen.getByTestId('location-search').textContent).toContain('meeting=meeting-2')
  })

  /** Opens action details, displays the audit timeline, and copies a ticket ID. */
  it('opens the audit drawer with the source timestamp and copyable ticket ID', async () => {
    const user = userEvent.setup()
    const writeText = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } })
    renderPage(<TrackerPage />, '/tracker')

    await user.click(await screen.findByRole('button', { name: 'Review retry policy' }))
    const drawer = await screen.findByRole('dialog', { name: 'Action details' })
    expect(within(drawer).getByText('ADO-77')).toBeTruthy()
    expect(within(drawer).getByText('Transcript time 02:04')).toBeTruthy()
    expect(within(drawer).getByText('Action approved')).toBeTruthy()
    expect(within(drawer).getByText('Organizer')).toBeTruthy()
    await user.click(within(drawer).getByRole('button', { name: 'Copy ticket ID' }))

    expect(writeText).toHaveBeenCalledWith('ADO-77')
    expect(await screen.findByText('Copied ADO-77.')).toBeTruthy()
  })

  /** Sends a quick reply, refreshes the item query, and updates the open drawer. */
  it('updates the tracker item after a quick reply', async () => {
    const user = userEvent.setup()
    renderPage(<TrackerPage />, '/tracker')

    await user.click(await screen.findByRole('button', { name: 'Check the cache' }))
    const drawer = await screen.findByRole('dialog', { name: 'Action details' })
    await user.click(within(drawer).getByRole('button', { name: 'Done' }))

    expect(await screen.findByText('Owner reply recorded.')).toBeTruthy()
    await waitFor(() => expect(within(drawer).getByText('Done', { selector: '.badge' })).toBeTruthy())
    expect(replyCalls).toBe(1)
  })

  /** Shows at most 50 tracker rows and reveals the next page on demand. */
  it('paginates tracker rows beyond fifty items', async () => {
    const user = userEvent.setup()
    activeItems = Array.from({ length: 51 }, (_, index) =>
      createItem({ id: `page-${index}`, title: `Review item ${index}`, status: 'Draft' }))
    const { container } = renderPage(<TrackerPage />, '/tracker?drafts=1')

    await screen.findByRole('button', { name: 'Review item 0' })
    expect(container.querySelectorAll('.tracker-table tbody tr')).toHaveLength(50)
    await user.click(screen.getByRole('button', { name: 'Load 50 more' }))
    expect(container.querySelectorAll('.tracker-table tbody tr')).toHaveLength(51)
  })

  /** Keeps tracker filter controls and grouped rows accessible. */
  it('has no axe accessibility violations', async () => {
    const { container } = renderPage(<TrackerPage />, '/tracker')
    await screen.findByRole('button', { name: 'Review retry policy' })

    expect((await axe(container)).violations).toEqual([])
  })
})

/** Covers dashboard loading, empty/error handling, chart fallback, and agenda. */
describe('DashboardPage', () => {
  /** Shows skeleton placeholders while server data is pending. */
  it('shows loading placeholders before dashboard requests complete', async () => {
    server.use(http.get(`${apiOrigin}/api/dashboard`, async () => {
      await delay(100)
      return HttpResponse.json(dashboard)
    }))
    renderPage(<DashboardPage />, '/dashboard')

    expect(screen.getAllByLabelText('Loading content').length).toBeGreaterThan(0)
    expect(await screen.findByRole('heading', { name: 'Dashboard' })).toBeTruthy()
  })

  /** Shows an empty state and create-meeting link when there are no actions. */
  it('shows an empty state when no action items exist', async () => {
    activeItems = []
    renderPage(<DashboardPage />, '/dashboard')

    expect(await screen.findByRole('heading', { name: 'No meeting data yet' })).toBeTruthy()
    expect(screen.getByRole('link', { name: 'Create a meeting' })).toBeTruthy()
  })

  /** Shows the retryable error state when a dashboard request fails. */
  it('shows a retry action when dashboard requests fail', async () => {
    server.use(http.get(`${apiOrigin}/api/items`, () =>
      HttpResponse.json({ title: 'Unavailable', detail: 'Action items are offline.' }, { status: 503 })))
    renderPage(<DashboardPage />, '/dashboard')

    expect(await screen.findByText('Action items are offline.')).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Retry' })).toBeTruthy()
  })

  /** Exposes chart values in a semantic table and shows carried-over agenda history. */
  it('renders the chart table fallback and next-agenda slip history', async () => {
    const { container } = renderPage(<DashboardPage />, '/dashboard')

    expect(await screen.findByRole('heading', { name: 'Open items per owner' })).toBeTruthy()
    const chart = container.querySelector('.owner-chart')
    expect(chart?.querySelector('table caption')?.textContent).toBe('Open action items per owner')
    expect(chart?.querySelector('table tbody')?.textContent).toContain('Priya1')
    expect(await screen.findByText('Carry-over action')).toBeTruthy()
    expect(screen.getByText('3 slips')).toBeTruthy()
  })

  /** Shows null dashboard rates as No data yet and retains metric formulas in tooltips. */
  it('shows No data yet when rate denominators are missing', async () => {
    server.use(http.get(`${apiOrigin}/api/dashboard`, () => HttpResponse.json({
      onTimeRate: null,
      slipRate: null,
      openItemsPerOwner: { Priya: 1 },
    })))
    renderPage(<DashboardPage />, '/dashboard')

    expect(await screen.findAllByText('No data yet')).toHaveLength(2)
    expect(screen.getByRole('tooltip', { name: /Completed items with no recorded slips/ })).toBeTruthy()
    expect(screen.getByRole('tooltip', { name: /Due items with one or more recorded slips/ })).toBeTruthy()
  })

  /** Keeps dashboard metrics, chart, and agenda accessible to assistive technology. */
  it('has no axe accessibility violations', async () => {
    const { container } = renderPage(<DashboardPage />, '/dashboard')
    await screen.findByRole('heading', { name: 'Open items per owner' })

    expect((await axe(container)).violations).toEqual([])
  })
})
