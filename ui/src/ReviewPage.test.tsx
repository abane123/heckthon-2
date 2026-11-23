import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { axe } from 'jest-axe'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { approveItem, createTickets, getMeeting, getMeetingItems, rejectItem, restoreItem, undoApproval, updateItem } from './api'
import type { ActionItem, Meeting, TicketCreationResponse } from './types'
import ReviewPage from './pages/ReviewPage'
import { ToastProvider } from './components/DesignSystem'

vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./api')>()
  return {
    ...actual,
    getMeeting: vi.fn(),
    getMeetingItems: vi.fn(),
    approveItem: vi.fn(),
    createTickets: vi.fn(),
    rejectItem: vi.fn(),
    restoreItem: vi.fn(),
    undoApproval: vi.fn(),
    updateItem: vi.fn(),
  }
})

const item: ActionItem = {
  id: 'item-1',
  meetingId: 'meeting-1',
  title: 'Review rate limits',
  ownerId: null,
  ownerName: null,
  dueDate: null,
  dueDateText: 'before the release',
  status: 'Draft',
  confidence: 0.48,
  sourceQuote: 'Someone should look at the rate limits.',
  timestamp: '02:04',
  needsInput: ['Owner', 'Due date'],
  externalTicketId: null,
  slipCount: 0,
}
const meeting: Meeting = {
  id: 'meeting-1',
  title: 'Release readiness',
  meetingDate: '2026-11-17T00:00:00Z',
  transcript: '',
  attendees: ['Priya', 'Alex'],
  items: [item],
}

/** Covers the review table and its organizer approval action. */
describe('ReviewPage', () => {
  afterEach(() => cleanup())

  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(getMeeting).mockResolvedValue(meeting)
    vi.mocked(getMeetingItems).mockResolvedValue([item])
    vi.mocked(approveItem).mockResolvedValue({ ...item, status: 'Approved' })
    vi.mocked(createTickets).mockResolvedValue({ createdItems: [], failures: [] })
    vi.mocked(rejectItem).mockResolvedValue({ ...item, status: 'Rejected' })
    vi.mocked(restoreItem).mockResolvedValue(item)
    vi.mocked(undoApproval).mockResolvedValue(item)
    vi.mocked(updateItem).mockImplementation(async (itemId, fields) => ({
      ...item,
      id: itemId,
      ...fields,
      status: 'Draft',
      needsInput: [],
    }))
  })

  /** Renders extracted details and approves the selected draft item. */
  it('renders an extracted item and calls approve when selected', async () => {
    const resolvedItem = { ...item, ownerName: 'Priya', dueDate: '2026-11-20T00:00:00Z', needsInput: [] }
    vi.mocked(getMeetingItems).mockResolvedValue([resolvedItem])
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <MemoryRouter initialEntries={['/meetings/meeting-1/review']}>
            <Routes><Route path="/meetings/:id/review" element={<ReviewPage />} /></Routes>
          </MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>,
    )

    expect(await screen.findByText('Review rate limits')).toBeTruthy()
    expect(screen.getByText(/Someone should look at the rate limits/)).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Approve' }).hasAttribute('disabled')).toBe(false)
    expect(screen.getByRole('button', { name: /Create.*tickets/ }).hasAttribute('disabled')).toBe(true)
    fireEvent.click(screen.getByRole('button', { name: 'Approve' }))

    await waitFor(() => expect(vi.mocked(approveItem).mock.calls[0]?.[0]).toBe('item-1'))
  })

  /** Shows confidence tiers for a resolved action and a vague unowned action. */
  it('renders high and low confidence tiers from extracted scores', async () => {
    vi.mocked(getMeetingItems).mockResolvedValue([
      { ...item, id: 'resolved', title: 'Review limits', confidence: 0.95, ownerName: 'Priya', dueDate: '2026-11-20T00:00:00Z' },
      { ...item, id: 'unowned', title: 'Look at rate limits', confidence: 0.3 },
    ])
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <MemoryRouter initialEntries={['/meetings/meeting-1/review']}>
            <Routes><Route path="/meetings/:id/review" element={<ReviewPage />} /></Routes>
          </MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>,
    )

    expect(await screen.findByText('Review limits')).toBeTruthy()
    expect(screen.getByText(/high confidence/)).toBeTruthy()
    expect(screen.getByText(/low confidence/)).toBeTruthy()
  })

  /** Shows undo controls for approved items and returns the item to draft review. */
  it('shows an approved badge, an enabled ticket button, and undo', async () => {
    vi.mocked(getMeetingItems).mockResolvedValue([{ ...item, status: 'Approved' }])
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <MemoryRouter initialEntries={['/meetings/meeting-1/review']}>
            <Routes><Route path="/meetings/:id/review" element={<ReviewPage />} /></Routes>
          </MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>,
    )

    expect(await screen.findByText('Review rate limits')).toBeTruthy()
    expect(screen.getByText('Approved', { selector: '.badge' })).toBeTruthy()
    const createButton = screen.getByRole('button', { name: /Create.*tickets/ })
    expect(createButton.hasAttribute('disabled'), createButton.outerHTML).toBe(false)
    fireEvent.click(screen.getByRole('button', { name: 'Undo' }))
    await waitFor(() => expect(vi.mocked(undoApproval).mock.calls[0]?.[0]).toBe('item-1'))
  })

  /** Approves a draft then uses Undo to return it to the draft workflow state. */
  it('approves an item and then calls unapprove from its approved state', async () => {
    const user = userEvent.setup()
    let activeItem: ActionItem = { ...item, needsInput: [] }
    vi.mocked(getMeetingItems).mockImplementation(async () => [activeItem])
    vi.mocked(approveItem).mockImplementation(async () => {
      activeItem = { ...activeItem, status: 'Approved' }
      return activeItem
    })
    vi.mocked(undoApproval).mockImplementation(async () => {
      activeItem = { ...activeItem, status: 'Draft' }
      return activeItem
    })
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <MemoryRouter initialEntries={['/meetings/meeting-1/review']}>
            <Routes><Route path="/meetings/:id/review" element={<ReviewPage />} /></Routes>
          </MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>,
    )

    await screen.findByText('Review rate limits')
    await user.click(screen.getByRole('button', { name: 'Approve' }))
    await screen.findByRole('button', { name: 'Undo' })
    await user.click(screen.getByRole('button', { name: 'Undo' }))

    await waitFor(() => expect(vi.mocked(undoApproval).mock.calls[0]?.[0]).toBe('item-1'))
    await screen.findByRole('button', { name: 'Approve' })
  })

  /** Expands rejected items and restores a selected action to editable draft review. */
  it('renders rejected items as collapsible and calls Restore', async () => {
    const user = userEvent.setup()
    vi.mocked(getMeetingItems).mockResolvedValue([{ ...item, status: 'Rejected' }])
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <MemoryRouter initialEntries={['/meetings/meeting-1/review']}>
            <Routes><Route path="/meetings/:id/review" element={<ReviewPage />} /></Routes>
          </MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>,
    )

    await user.click(await screen.findByText(/Rejected items/))
    expect(screen.getByText('Review rate limits').closest('tr')?.className).toContain('rejected-row')
    await user.click(screen.getByRole('button', { name: 'Restore' }))

    await waitFor(() => expect(vi.mocked(restoreItem).mock.calls[0]?.[0]).toBe('item-1'))
  })

  /** Confirms the eligible count before bulk-approving complete high-confidence items. */
  it('requires confirmation before approving all high-confidence drafts', async () => {
    const user = userEvent.setup()
    vi.mocked(getMeetingItems).mockResolvedValue([
      { ...item, confidence: 0.9, needsInput: [] },
    ])
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <MemoryRouter initialEntries={['/meetings/meeting-1/review']}>
            <Routes><Route path="/meetings/:id/review" element={<ReviewPage />} /></Routes>
          </MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>,
    )

    await user.click(await screen.findByRole('button', { name: 'Approve all high-confidence (1)' }))
    const dialog = await screen.findByRole('dialog', { name: 'Approve high-confidence items' })
    expect(dialog.textContent).toContain('Approve these 1 items?')
    await user.click(screen.getByRole('button', { name: 'Approve 1' }))

    await waitFor(() => expect(vi.mocked(approveItem).mock.calls[0]?.[0]).toBe('item-1'))
  })

  /** Saves inline edits with Enter and offers only meeting attendees in the owner selector. */
  it('saves an inline edit from the keyboard with attendee-limited ownership', async () => {
    const user = userEvent.setup()
    vi.mocked(getMeetingItems).mockResolvedValue([item])
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <MemoryRouter initialEntries={['/meetings/meeting-1/review']}>
            <Routes><Route path="/meetings/:id/review" element={<ReviewPage />} /></Routes>
          </MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>,
    )

    await user.click(await screen.findByRole('button', { name: 'Edit' }))
    const title = screen.getByRole('textbox', { name: 'Title' })
    await user.clear(title)
    await user.type(title, 'Review retry behavior')
    const owner = screen.getByRole('combobox', { name: 'Owner' })
    expect(Array.from((owner as HTMLSelectElement).options).map((option) => option.textContent))
      .toEqual(['Unassigned', 'Priya', 'Alex'])
    await user.selectOptions(owner, 'Priya')
    await user.tab()
    await user.tab()
    await user.keyboard('{Enter}')

    await waitFor(() => expect(vi.mocked(updateItem).mock.calls[0]?.[1]).toMatchObject({
      title: 'Review retry behavior',
      ownerName: 'Priya',
    }))
  })

  /** Keeps successful ticket IDs visible while presenting a provider failure for another item. */
  it('shows successful ticket IDs and per-item partial failures', async () => {
    const user = userEvent.setup()
    const approvedItems = [
      { ...item, id: 'item-1', title: 'Review rate limits', status: 'Approved' as const, needsInput: [] },
      { ...item, id: 'item-2', title: 'Check rollout', status: 'Approved' as const, needsInput: [] },
    ]
    vi.mocked(getMeetingItems).mockResolvedValue(approvedItems)
    const response: TicketCreationResponse = {
      createdItems: [{ ...approvedItems[0]!, status: 'Created', externalTicketId: 'ADO-123' }],
      failures: [{
        actionItemId: 'item-2',
        title: 'Check rollout',
        detail: 'The ticket provider could not create this item. You can retry it.',
      }],
    }
    vi.mocked(createTickets).mockResolvedValue(response)
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <MemoryRouter initialEntries={['/meetings/meeting-1/review']}>
            <Routes><Route path="/meetings/:id/review" element={<ReviewPage />} /></Routes>
          </MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>,
    )

    await user.click(await screen.findByRole('button', { name: 'Create 2 tickets' }))
    const dialog = await screen.findByRole('dialog', { name: 'Create tickets' })
    await user.click(within(dialog).getByRole('button', { name: 'Create 2 tickets' }))

    expect(await screen.findByText('ADO-123')).toBeTruthy()
    expect(screen.getByText('The ticket provider could not create this item. You can retry it.')).toBeTruthy()
  })

  /** Runs axe against review content rendered for a draft action item. */
  it('has no axe accessibility violations', async () => {
    vi.mocked(getMeetingItems).mockResolvedValue([{ ...item, needsInput: [] }])
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const { container } = render(
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <MemoryRouter initialEntries={['/meetings/meeting-1/review']}>
            <Routes><Route path="/meetings/:id/review" element={<ReviewPage />} /></Routes>
          </MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>,
    )

    await screen.findByText('Review rate limits')
    expect((await axe(container)).violations).toEqual([])
  })
})
