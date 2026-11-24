import { useMemo, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { Button, Card, Drawer, EmptyState, ErrorState, Skeleton, StatusBadge, useToast } from '../components/DesignSystem'
import { useActionItems, useAudit, useMeetings, useSendReply, useVirtualClock } from '../hooks'
import type { ActionItem, ActionReplyInput, ActionStatus } from '../types'
import { formatDate, formatRelativeDays } from '../utils/format'

const statusOptions: ActionStatus[] = ['Draft', 'Approved', 'Created', 'InProgress', 'Overdue', 'Escalated', 'Done', 'Rejected']
const pageSize = 50
const noActionItems: ActionItem[] = []

/** Represents tracker filters stored in the URL query string. */
type TrackerFilters = {
  statuses: ActionStatus[]
  owner: string
  meetingId: string
  search: string
  overdueOnly: boolean
  includeDrafts: boolean
}

/** Provides URL-synced tracker filters, status groups, and action audit drawers. */
export default function TrackerPage() {
  const [searchParams, setSearchParams] = useSearchParams()
  const query = useActionItems()
  const meetingsQuery = useMeetings()
  const clockQuery = useVirtualClock()
  const { showToast } = useToast()
  const items = query.data ?? noActionItems
  const filters = readTrackerFilters(searchParams)
  const filterKey = searchParams.toString()
  const [pagination, setPagination] = useState({ filterKey, count: pageSize })
  const visibleCount = pagination.filterKey === filterKey ? pagination.count : pageSize
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [reassignTo, setReassignTo] = useState('')
  const owners = Array.from(new Set(items.map((item) => item.ownerName).filter((name): name is string => Boolean(name)))).sort()
  const meetings = meetingsQuery.data ?? []
  const meetingTitles = new Map(meetings.map((meeting) => [meeting.id, meeting.title]))
  const referenceDate = clockQuery.data?.currentDate ?? new Date().toISOString()
  const filteredItems = useMemo(() => filterItems(items, filters, new Date(referenceDate)), [filters, items, referenceDate])
  const selectedItem = items.find((item) => item.id === selectedId)
  const auditQuery = useAudit(selectedItem?.id ?? '', Boolean(selectedItem))
  const reply = useSendReply()
  const visibleItems = filteredItems.slice(0, visibleCount)
  const groups = statusOptions.filter((status) => visibleItems.some((item) => item.status === status))

  /** Applies a filter update to the URL while preserving all other active filters. */
  function updateFilters(next: Partial<TrackerFilters>) {
    const updated = new URLSearchParams(searchParams)
    const value = { ...filters, ...next }
    updated.delete('status')
    value.statuses.forEach((status) => updated.append('status', status))
    setOptionalParam(updated, 'owner', value.owner)
    setOptionalParam(updated, 'meeting', value.meetingId)
    setOptionalParam(updated, 'q', value.search)
    setOptionalParam(updated, 'overdue', value.overdueOnly ? '1' : '')
    setOptionalParam(updated, 'drafts', value.includeDrafts ? '1' : '')
    setSearchParams(updated, { replace: true })
  }

  /** Sends one supported quick reply and keeps the selected audit drawer current. */
  async function sendQuickReply(input: ActionReplyInput) {
    if (!selectedItem) return
    try {
      await reply.mutateAsync({ id: selectedItem.id, input })
      showToast('Owner reply recorded.', 'success')
    } catch (reason) {
      showToast(reason instanceof Error ? reason.message : 'The reply could not be sent.', 'error')
    }
  }

  /** Copies an existing external ticket identifier and reports clipboard failures. */
  async function copyTicketId(ticketId: string) {
    try {
      await navigator.clipboard.writeText(ticketId)
      showToast(`Copied ${ticketId}.`, 'success')
    } catch {
      showToast('The ticket ID could not be copied from this browser.', 'error')
    }
  }

  /** Sends a reassignment reply only after the organizer selects a known owner. */
  function reassignItem() {
    if (!reassignTo) {
      showToast('Choose an owner before reassigning.', 'error')
      return
    }
    void sendQuickReply({ action: 'Reassign', reassignTo })
  }

  return (
    <section className="page tracker-page">
      <div className="page-heading">
        <div><p className="eyebrow">All meetings</p><h1>Action tracker</h1><p className="muted">Filter, group, and follow up on every extracted action.</p></div>
      </div>
      <Card className="tracker-filter-bar">
        <div className="tracker-status-filter">
          <span className="filter-label">Status</span>
          <div className="status-filter-chips" role="group" aria-label="Filter by status">
            {statusOptions.map((status) => {
              const selected = filters.statuses.includes(status)
              const count = items.filter((item) => item.status === status).length
              return (
                <Button key={status} type="button" variant={selected ? 'primary' : 'secondary'}
                  aria-pressed={selected} onClick={() => updateFilters({
                    statuses: selected ? filters.statuses.filter((value) => value !== status) : [...filters.statuses, status],
                  })}>
                  {statusLabel(status)} <span className="status-filter-count">{count}</span>
                </Button>
              )
            })}
          </div>
        </div>
        <label>Owner
          <select value={filters.owner} onChange={(event) => updateFilters({ owner: event.target.value })}>
            <option value="">All owners</option>{owners.map((owner) => <option key={owner} value={owner}>{owner}</option>)}
          </select>
        </label>
        <label>Meeting
          <select value={filters.meetingId} onChange={(event) => updateFilters({ meetingId: event.target.value })}>
            <option value="">All meetings</option>{meetings.map((meeting) => (
              <option key={meeting.id} value={meeting.id}>{meeting.title}</option>
            ))}
          </select>
        </label>
        <label>Search
          <input type="search" value={filters.search} onChange={(event) => updateFilters({ search: event.target.value })} placeholder="Search action or ticket" />
        </label>
        <label className="checkbox-filter"><input type="checkbox" checked={filters.overdueOnly} onChange={(event) => updateFilters({ overdueOnly: event.target.checked })} /> Overdue only</label>
        <label className="checkbox-filter"><input type="checkbox" checked={filters.includeDrafts}
          onChange={(event) => updateFilters({ includeDrafts: event.target.checked })} /> Include drafts</label>
        <Button variant="ghost" onClick={() => setSearchParams({}, { replace: true })}>Clear filters</Button>
      </Card>
      {(query.isLoading || meetingsQuery.isLoading) && <Skeleton rows={5} />}
      {(query.isError || meetingsQuery.isError) && (
        <ErrorState message={query.error?.message ?? meetingsQuery.error?.message ?? 'Could not load tracker data.'}
          onRetry={() => { void query.refetch(); void meetingsQuery.refetch() }} />
      )}
      {!query.isLoading && !meetingsQuery.isLoading && filteredItems.length === 0 && (
        <EmptyState title={items.length === 0 ? 'No action items yet' : 'No matching actions'}
          description={items.length === 0 ? 'Create a meeting to extract and track action items.' : 'Change or clear your filters to see other actions.'}
          action={items.length === 0 ? <Link to="/">Create a meeting</Link> : <Button variant="secondary" onClick={() => setSearchParams({}, { replace: true })}>Clear filters</Button>} />
      )}
      {filteredItems.length > 0 && (
        <>
          <p className="muted result-count">Showing {visibleItems.length} of {filteredItems.length} actions</p>
          {groups.map((status) => {
            const groupItems = visibleItems.filter((item) => item.status === status)
            return (
              <section className={`status-group ${status === 'Escalated' ? 'escalated-group' : ''}`} key={status}>
                <h2><StatusBadge status={status} /> <span className="count">{filteredItems.filter((item) => item.status === status).length}</span></h2>
                <Card className="tracker-table-card">
                  <div className="table-scroll">
                    <table className="data-table tracker-table">
                      <caption className="visually-hidden">{statusLabel(status)} action items</caption>
                      <thead><tr><th scope="col">Action</th><th scope="col">Meeting</th><th scope="col">Ticket</th><th scope="col">Owner</th><th scope="col">Due date</th><th scope="col">Slips</th><th scope="col">Status</th></tr></thead>
                      <tbody>{groupItems.map((item) => <TrackerRow key={item.id} item={item}
                        meetingTitle={meetingTitles.get(item.meetingId) ?? '—'}
                        onOpen={() => { setSelectedId(item.id); setReassignTo(item.ownerName ?? '') }} />)}</tbody>
                    </table>
                  </div>
                </Card>
              </section>
            )
          })}
          {visibleCount < filteredItems.length && <div className="load-more"><Button variant="secondary" onClick={() => setPagination({ filterKey, count: visibleCount + pageSize })}>Load 50 more</Button></div>}
        </>
      )}
      <Drawer open={Boolean(selectedItem)} title="Action details" onClose={() => setSelectedId(null)}>
        {selectedItem && (
          <div className="drawer-content">
            <StatusBadge status={selectedItem.status} />
            <h3>{selectedItem.title}</h3>
            <dl className="item-details">
              <div><dt>Owner</dt><dd>{selectedItem.ownerName ?? 'Unassigned'}</dd></div>
              <div><dt>Due date</dt><dd>{formatDate(selectedItem.dueDate)} {selectedItem.status === 'Overdue' && <span className="overdue-label">{formatRelativeDays(selectedItem.dueDate)}</span>}</dd></div>
              <div><dt>Ticket</dt><dd>{selectedItem.externalTicketId
                ? <span className="copyable-ticket"><code>{selectedItem.externalTicketId}</code>
                  <Button variant="ghost" onClick={() => void copyTicketId(selectedItem.externalTicketId ?? '')}>Copy ticket ID</Button>
                </span>
                : 'Not created'}</dd></div>
              <div><dt>Slip count</dt><dd>{selectedItem.slipCount}</dd></div>
            </dl>
            <blockquote className="source-quote">{selectedItem.sourceQuote}</blockquote>
            {selectedItem.timestamp && <span className="timestamp-chip">Transcript time {selectedItem.timestamp}</span>}
            <Link to={`/items/${selectedItem.id}/audit`} onClick={() => setSelectedId(null)}>Open full audit trail</Link>
            <section className="drawer-audit">
              <h4>Audit timeline</h4>
              {auditQuery.isLoading && <Skeleton rows={3} />}
              {auditQuery.isError && <ErrorState message={auditQuery.error.message} onRetry={() => void auditQuery.refetch()} />}
              {auditQuery.data?.length === 0 && <p className="muted">No audit events have been recorded.</p>}
              <ol className="timeline">{auditQuery.data?.map((event) => (
                <li key={event.id}><strong>{event.eventType}</strong><span>{event.actor}</span><time>{formatDateTime(event.createdAt)}</time></li>
              ))}</ol>
            </section>
            <section className="quick-replies">
              <h4>Quick reply</h4>
              <Button loading={reply.isPending} onClick={() => void sendQuickReply({ action: 'Done' })}>Done</Button>
              <Button variant="secondary" loading={reply.isPending} onClick={() => void sendQuickReply({ action: 'PushToFriday' })}>Push to Friday</Button>
              <Button variant="secondary" loading={reply.isPending} onClick={() => void sendQuickReply({ action: 'Blocked' })}>Blocked</Button>
              <label>Reassign to
                <select value={reassignTo} onChange={(event) => setReassignTo(event.target.value)}>
                  <option value="">Choose an owner</option>{owners.map((owner) => <option key={owner} value={owner}>{owner}</option>)}
                </select>
              </label>
              <Button variant="secondary" loading={reply.isPending} onClick={reassignItem}>Reassign</Button>
            </section>
          </div>
        )}
      </Drawer>
    </section>
  )
}

/** Renders a clickable tracker row and emphasizes overdue or escalated work. */
function TrackerRow({ item, meetingTitle, onOpen }: { item: ActionItem; meetingTitle: string; onOpen: () => void }) {
  const overdue = item.status === 'Overdue'
  return (
    <tr className={`${item.status === 'Escalated' ? 'tracker-escalated' : ''}${overdue ? ' tracker-overdue' : ''}`}>
      <td data-label="Action"><Button variant="ghost" className="tracker-title" onClick={onOpen}>{item.title}</Button>
        {overdue && <small className="block overdue-label">{formatRelativeDays(item.dueDate)}</small>}</td>
      <td data-label="Meeting">{meetingTitle}</td>
      <td data-label="Ticket">{item.externalTicketId ?? '—'}</td>
      <td data-label="Owner">{item.ownerName ?? 'Unassigned'}</td>
      <td data-label="Due date">{formatDate(item.dueDate)}</td>
      <td data-label="Slips"><span className={item.slipCount > 0 ? 'slip-count' : ''}>{item.slipCount}</span></td>
      <td data-label="Status"><StatusBadge status={item.status} /></td>
    </tr>
  )
}

/** Reads filter values from the current URL and validates status names. */
function readTrackerFilters(params: URLSearchParams): TrackerFilters {
  return {
    statuses: params.getAll('status').filter(isActionStatus),
    owner: params.get('owner') ?? '',
    meetingId: params.get('meeting') ?? '',
    search: params.get('q') ?? '',
    overdueOnly: params.get('overdue') === '1',
    includeDrafts: params.get('drafts') === '1',
  }
}

/** Updates or removes a scalar filter parameter without losing repeated status values. */
function setOptionalParam(params: URLSearchParams, key: string, value: string) {
  if (value) params.set(key, value)
  else params.delete(key)
}

/** Narrows a URL value to a supported action status. */
function isActionStatus(value: string): value is ActionStatus {
  return statusOptions.includes(value as ActionStatus)
}

/** Applies tracker status, owner, meeting, text, deadline, and draft filters. */
function filterItems(items: ActionItem[], filters: TrackerFilters, referenceDate: Date): ActionItem[] {
  const today = new Date(referenceDate)
  today.setHours(0, 0, 0, 0)
  const search = filters.search.trim().toLocaleLowerCase()
  return items.filter((item) => {
    if (filters.statuses.length > 0 && !filters.statuses.includes(item.status)) return false
    if (filters.owner && item.ownerName !== filters.owner) return false
    if (filters.meetingId && item.meetingId !== filters.meetingId) return false
    if (!filters.includeDrafts && item.status === 'Draft') return false
    if (search && !`${item.title} ${item.externalTicketId ?? ''}`.toLocaleLowerCase().includes(search)) return false
    if (filters.overdueOnly && (!item.dueDate || new Date(item.dueDate) >= today || ['Done', 'Rejected'].includes(item.status))) return false
    return true
  })
}

/** Maps workflow enum identifiers to concise human-facing labels. */
function statusLabel(status: ActionStatus): string {
  const labels: Record<ActionStatus, string> = {
    Draft: 'Awaiting review',
    Approved: 'Organizer approved',
    Created: 'Ticket created',
    InProgress: 'In progress',
    Overdue: 'Overdue',
    Escalated: 'Escalated',
    Done: 'Completed',
    Rejected: 'Rejected',
  }
  return labels[status]
}

/** Formats an audit timestamp without exposing an ISO string. */
function formatDateTime(value: string): string {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : new Intl.DateTimeFormat('en-GB', { dateStyle: 'medium', timeStyle: 'short' }).format(date)
}
