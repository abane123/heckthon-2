import { useCallback, useEffect, useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { useParams } from 'react-router-dom'
import { Button, Card, ConfidenceBar, ConfirmDialog, EmptyState, ErrorState, Modal, Skeleton, StatusBadge, Tooltip, useToast } from '../components/DesignSystem'
import { useApproveItem, useCreateTickets, useMeetingDetails, useMeetingItems, useRejectItem, useRestoreRejectedItem, useUnapproveItem, useUpdateItem } from '../hooks'
import type { ActionItem, TicketCreationFailure } from '../types'
import { queryKeys } from '../queryKeys'
import { formatDate } from '../utils/format'

type EditDraft = { title: string; ownerName: string; dueDate: string }
const emptyActionItems: ActionItem[] = []
const emptyAttendees: string[] = []

/** Provides organizer review, inline editing, approval controls, and ticket creation. */
export default function ReviewPage() {
  const { id = '' } = useParams()
  const queryClient = useQueryClient()
  const { showToast } = useToast()
  const meetingQuery = useMeetingDetails(id)
  const itemsQuery = useMeetingItems(id)
  const approve = useApproveItem()
  const reject = useRejectItem()
  const restore = useRestoreRejectedItem()
  const approveDraft = approve.mutate
  const approveDraftAsync = approve.mutateAsync
  const rejectDraft = reject.mutate
  const undo = useUnapproveItem()
  const update = useUpdateItem()
  const tickets = useCreateTickets()
  const itemRefs = useRef(new Map<string, HTMLElement>())
  const [editingId, setEditingId] = useState<string | null>(null)
  const [editDraft, setEditDraft] = useState<EditDraft>({ title: '', ownerName: '', dueDate: '' })
  const [editError, setEditError] = useState('')
  const [formError, setFormError] = useState('')
  const [confirmBulk, setConfirmBulk] = useState(false)
  const [confirmTickets, setConfirmTickets] = useState(false)
  const [shortcutsOpen, setShortcutsOpen] = useState(false)
  const [ticketResults, setTicketResults] = useState<ActionItem[]>([])
  const [ticketFailures, setTicketFailures] = useState<TicketCreationFailure[]>([])
  const [focusedIndex, setFocusedIndex] = useState(0)
  const editTitleRef = useRef<HTMLInputElement>(null)

  const items = itemsQuery.data ?? emptyActionItems
  const meetingAttendees = meetingQuery.data?.attendees ?? emptyAttendees
  const approvedItems = items.filter((item) => item.status === 'Approved')
  const draftItems = items.filter((item) => item.status === 'Draft')
  const rejectedItems = items.filter((item) => item.status === 'Rejected')
  const highConfidenceItems = draftItems.filter((item) => item.confidence >= 0.75 && item.needsInput.length === 0)
  const needsInputCount = items.filter((item) => item.needsInput.length > 0).length
  const lowConfidenceCount = items.filter((item) => item.confidence < 0.5).length

  /** Loads an item's editable values into the inline editor. */
  const beginEdit = useCallback((item: ActionItem) => {
    setEditingId(item.id)
    setEditError('')
    setEditDraft({
      title: item.title,
      ownerName: item.ownerName && meetingAttendees.includes(item.ownerName) ? item.ownerName : '',
      dueDate: item.dueDate?.slice(0, 10) ?? '',
    })
  }, [meetingAttendees])

  useEffect(() => {
    /** Maps organizer shortcuts to the currently focused action while preserving normal text entry. */
    function handleShortcut(event: KeyboardEvent) {
      const activeElement = document.activeElement
      if (activeElement instanceof HTMLElement
        && (activeElement.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(activeElement.tagName))) return
      if (event.key === '?') {
        setShortcutsOpen(true)
        return
      }
      if (items.length === 0) return
      if (event.key.toLowerCase() === 'j' || event.key.toLowerCase() === 'k') {
        event.preventDefault()
        const nextIndex = event.key.toLowerCase() === 'j'
          ? Math.min(items.length - 1, focusedIndex + 1)
          : Math.max(0, focusedIndex - 1)
        setFocusedIndex(nextIndex)
        itemRefs.current.get(items[nextIndex]?.id ?? '')?.focus()
        return
      }
      const item = items[focusedIndex]
      if (!item) return
      if (event.key.toLowerCase() === 'a' && item.status === 'Draft' && item.needsInput.length === 0) approveDraft(item.id)
      if (event.key.toLowerCase() === 'r' && item.status === 'Draft') rejectDraft(item.id)
      if (event.key.toLowerCase() === 'e' && item.status === 'Draft') beginEdit(item)
    }

    document.addEventListener('keydown', handleShortcut)
    return () => document.removeEventListener('keydown', handleShortcut)
  }, [approveDraft, beginEdit, focusedIndex, items, rejectDraft])

  useEffect(() => {
    if (editingId) editTitleRef.current?.focus()
  }, [editingId])

  /** Validates and optimistically saves an inline edit for a draft action. */
  async function saveEdit(item: ActionItem) {
    const title = editDraft.title.trim()
    if (!title) {
      setEditError('Enter an action title.')
      return
    }
    try {
      await update.mutateAsync({
        id: item.id,
        fields: {
          title,
          ownerName: editDraft.ownerName || null,
          dueDate: editDraft.dueDate ? new Date(`${editDraft.dueDate}T00:00:00`).toISOString() : null,
        },
      })
      setEditingId(null)
      showToast('Action item updated.', 'success')
    } catch (reason) {
      setEditError(reason instanceof Error ? reason.message : 'The action item could not be saved.')
    }
  }

  /** Approves eligible high-confidence drafts one at a time and reports partial outcomes. */
  async function approveHighConfidence() {
    setConfirmBulk(false)
    let approvedCount = 0
    let failedCount = 0
    for (const item of highConfidenceItems) {
      try {
        await approveDraftAsync(item.id)
        approvedCount += 1
      } catch {
        failedCount += 1
      }
    }
    await queryClient.invalidateQueries({ queryKey: queryKeys.items.meeting(id) })
    showToast(failedCount > 0
      ? `${approvedCount} approved; ${failedCount} could not be approved.`
      : `${approvedCount} high-confidence items approved.`, failedCount > 0 ? 'error' : 'success')
  }

  /** Creates approved tickets and retains returned ticket identifiers for confirmation. */
  async function createApprovedTickets() {
    setConfirmTickets(false)
    setFormError('')
    setTicketFailures([])
    try {
      const result = await tickets.mutateAsync(id)
      setTicketResults(result.createdItems.filter((item) => Boolean(item.externalTicketId)))
      setTicketFailures(result.failures)
      if (result.failures.length > 0) {
        setFormError(`${result.failures.length} ticket(s) could not be created. Successful tickets are listed below.`)
      }
    } catch (reason) {
      setFormError(reason instanceof Error ? reason.message : 'Some tickets could not be created.')
      const refreshed = queryClient.getQueryData<ActionItem[]>(queryKeys.items.meeting(id)) ?? []
      setTicketResults(refreshed.filter((item) => Boolean(item.externalTicketId)))
      setTicketFailures(approvedItems.map((item) => ({
        actionItemId: item.id,
        title: item.title,
        detail: 'The ticket request failed before a result was received. Retry to check the remaining approved items.',
      })))
    }
  }

  /** Copies a ticket identifier and reports clipboard failures instead of silently ignoring them. */
  async function copyTicketId(ticketId: string) {
    try {
      await navigator.clipboard.writeText(ticketId)
      showToast(`Copied ${ticketId}.`, 'success')
    } catch {
      showToast('The ticket ID could not be copied from this browser.', 'error')
    }
  }

  /** Renders one draft, approved, rejected, or ticketed action with its organizer controls. */
  function renderItem(item: ActionItem, rejected = false) {
    const isEditing = editingId === item.id
    const needsInputReason = item.needsInput.length > 0
      ? `Add ${item.needsInput.join(' and ')} before approving this item.`
      : 'Approve this action item.'
    return (
      <tr
        key={item.id}
        ref={(element) => {
          if (element) itemRefs.current.set(item.id, element)
          else itemRefs.current.delete(item.id)
        }}
        tabIndex={-1}
        className={`${item.confidence < 0.5 ? 'low-confidence ' : ''}${rejected ? 'rejected-row' : ''}`}
        aria-label={item.title}
      >
        <td data-label="Action">
          {isEditing ? (
            <form
              className="inline-edit"
              onSubmit={(event) => {
                event.preventDefault()
                void saveEdit(item)
              }}
            >
              <label>Title
                <input ref={editTitleRef} value={editDraft.title} aria-invalid={Boolean(editError)} aria-describedby={editError ? 'edit-error' : undefined}
                  onChange={(event) => setEditDraft((current) => ({ ...current, title: event.target.value }))} />
              </label>
              <label>Owner
                <select value={editDraft.ownerName} onChange={(event) => setEditDraft((current) => ({ ...current, ownerName: event.target.value }))}>
                  <option value="">Unassigned</option>
                  {meetingAttendees.map((attendee) => <option key={attendee} value={attendee}>{attendee}</option>)}
                </select>
              </label>
              <label>Due date
                <input type="date" value={editDraft.dueDate} onChange={(event) => setEditDraft((current) => ({ ...current, dueDate: event.target.value }))} />
              </label>
              {editError && <small id="edit-error" className="field-error" role="alert">{editError}</small>}
              <div className="row-actions">
                <Button type="submit" loading={update.isPending}>Save</Button>
                <Button type="button" variant="ghost" onClick={() => setEditingId(null)}>Cancel</Button>
              </div>
            </form>
          ) : (
            <div className="review-title">
              <strong>{item.title}</strong>
              {item.timestamp && <span className="timestamp-chip">{item.timestamp}</span>}
            </div>
          )}
        </td>
        <td data-label="Owner">
          <div className="owner-display">
            <span className="avatar" aria-hidden="true">{getInitials(item.ownerName)}</span>
            <span>{item.ownerName ?? 'Unassigned'}</span>
          </div>
        </td>
        <td data-label="Due">
          <strong>{formatDate(item.dueDate)}</strong>
          {item.dueDateText && <small className="block muted">{item.dueDateText}</small>}
        </td>
        <td data-label="Confidence"><ConfidenceBar confidence={item.confidence} /></td>
        <td data-label="Source">
          <blockquote className="source-quote">{item.sourceQuote}</blockquote>
        </td>
        <td data-label="Review">
          {item.needsInput.length > 0 && (
            <div className="needs-input-list">
              <span className="needs-input">Needs input</span>
              {item.needsInput.map((field) => <span className="chip chip-warning" key={field}>{field}</span>)}
            </div>
          )}
          <div className="row-actions">
            {item.status === 'Draft' && !isEditing && <>
              {item.needsInput.length > 0 ? (
                <Tooltip label={needsInputReason}><span><Button disabled onClick={() => approve.mutate(item.id)}>Approve</Button></span></Tooltip>
              ) : <Button onClick={() => approve.mutate(item.id)}>Approve</Button>}
              <Button variant="secondary" onClick={() => beginEdit(item)}>Edit</Button>
              <Button variant="ghost" onClick={() => reject.mutate(item.id)}>Reject</Button>
            </>}
            {item.status === 'Approved' && <>
              <StatusBadge status="Approved" />
              <Button variant="secondary" onClick={() => undo.mutate(item.id)}>Undo</Button>
            </>}
            {item.status === 'Rejected' && (
              <Button variant="secondary" loading={restore.isPending} onClick={() => restore.mutate(item.id)}>Restore</Button>
            )}
            {!['Draft', 'Approved', 'Rejected'].includes(item.status) && <StatusBadge status={item.status} />}
          </div>
        </td>
      </tr>
    )
  }

  const meeting = meetingQuery.data
  return (
    <section className="page review-page">
      <div className="page-heading review-heading">
        <div>
          <p className="eyebrow">Organizer review</p>
          <h1>{meeting?.title ?? 'Review action items'}</h1>
          {meeting && <p className="muted">{formatDate(meeting.meetingDate)} · {meeting.attendees.join(', ')}</p>}
        </div>
        <div className="review-summary" aria-label="Review summary">
          <SummaryCount label="Items found" count={items.length} />
          <SummaryCount label="Need input" count={needsInputCount} />
          <SummaryCount label="Low confidence" count={lowConfidenceCount} />
          <SummaryCount label="Approved" count={approvedItems.length} />
        </div>
      </div>
      <div className="review-bulk-actions">
        <Button variant="secondary" disabled={highConfidenceItems.length === 0 || approve.isPending}
          onClick={() => setConfirmBulk(true)}>Approve all high-confidence ({highConfidenceItems.length})</Button>
        <Button variant="ghost" onClick={() => setShortcutsOpen(true)}>Keyboard shortcuts</Button>
      </div>
      {meetingQuery.isLoading || itemsQuery.isLoading ? <Skeleton rows={5} /> : null}
      {(meetingQuery.isError || itemsQuery.isError) && (
        <ErrorState message={meetingQuery.error?.message ?? itemsQuery.error?.message ?? 'Could not load this review.'}
          onRetry={() => { void meetingQuery.refetch(); void itemsQuery.refetch() }} />
      )}
      {!meetingQuery.isLoading && !itemsQuery.isLoading && items.length === 0 && (
        <EmptyState title="No action items found" description="This transcript did not produce any reviewable action items." />
      )}
      {items.length > 0 && (
        <Card className="review-table-card">
          <div className="table-scroll">
            <table className="data-table review-table">
              <caption className="visually-hidden">Meeting action items for organizer review</caption>
              <thead><tr><th scope="col">Action</th><th scope="col">Owner</th><th scope="col">Due</th><th scope="col">Confidence</th><th scope="col">Source quote</th><th scope="col">Review</th></tr></thead>
              <tbody>{items.filter((item) => item.status !== 'Rejected').map((item) => renderItem(item))}</tbody>
            </table>
          </div>
        </Card>
      )}
      {rejectedItems.length > 0 && (
        <details className="rejected-items">
          <summary>Rejected items <span className="count">{rejectedItems.length}</span></summary>
          <Card className="review-table-card">
            <div className="table-scroll">
              <table className="data-table review-table">
                <caption className="visually-hidden">Rejected meeting action items</caption>
                <thead><tr><th scope="col">Action</th><th scope="col">Owner</th><th scope="col">Due</th><th scope="col">Confidence</th><th scope="col">Source quote</th><th scope="col">Review</th></tr></thead>
                <tbody>{rejectedItems.map((item) => renderItem(item, true))}</tbody>
              </table>
            </div>
          </Card>
        </details>
      )}
      {formError && <ErrorState title="Ticket creation issue" message={formError} />}
      {ticketResults.length > 0 && (
        <Card className="ticket-results" aria-live="polite">
          <h2>Tickets created</h2>
          <ul>{ticketResults.map((item) => (
            <li key={item.id}><span>{item.title}</span><code>{item.externalTicketId}</code>
              <Button variant="ghost" onClick={() => void copyTicketId(item.externalTicketId ?? '')}>Copy ID</Button>
            </li>
          ))}</ul>
        </Card>
      )}
      {ticketFailures.length > 0 && (
        <Card className="ticket-failures" role="alert">
          <h2>Tickets not created</h2>
          <ul>{ticketFailures.map((failure) => <li key={failure.actionItemId}><strong>{failure.title}</strong><span>{failure.detail}</span></li>)}</ul>
        </Card>
      )}
      <div className="review-sticky-bar" role="region" aria-label="Review actions">
        <span>{approvedItems.length} approved · {needsInputCount} need input · {rejectedItems.length} rejected</span>
        <Button disabled={approvedItems.length === 0} loading={tickets.isPending} onClick={() => setConfirmTickets(true)}>
          Create {approvedItems.length} tickets
        </Button>
      </div>
      <ConfirmDialog open={confirmBulk} title="Approve high-confidence items"
        message={<><p>Approve these {highConfidenceItems.length} items?</p><ul>{highConfidenceItems.map((item) => <li key={item.id}>{item.title}</li>)}</ul></>}
        confirmLabel={`Approve ${highConfidenceItems.length}`} onCancel={() => setConfirmBulk(false)} onConfirm={() => void approveHighConfidence()} />
      <ConfirmDialog open={confirmTickets} title="Create tickets"
        message={`Create tickets for ${approvedItems.length} approved action items?`}
        confirmLabel={`Create ${approvedItems.length} tickets`} onCancel={() => setConfirmTickets(false)} onConfirm={() => void createApprovedTickets()} />
      <Modal open={shortcutsOpen} title="Review keyboard shortcuts" onClose={() => setShortcutsOpen(false)}>
        <div className="modal-content shortcut-list">
          <p><kbd>J</kbd> / <kbd>K</kbd> Move to the next / previous item</p>
          <p><kbd>A</kbd> Approve a complete draft item</p>
          <p><kbd>R</kbd> Reject a draft item</p>
          <p><kbd>E</kbd> Edit a draft item</p>
          <p><kbd>?</kbd> Show this shortcut list</p>
        </div>
      </Modal>
    </section>
  )
}

/** Displays one labelled count in the review summary. */
function SummaryCount({ label, count }: { label: string; count: number }) {
  return <div className="summary-count"><strong>{count}</strong><span>{label}</span></div>
}

/** Derives accessible two-letter initials for an action owner's avatar. */
function getInitials(name: string | null): string {
  if (!name) return '—'
  return name.trim().split(/\s+/).slice(0, 2).map((part) => part[0]?.toLocaleUpperCase() ?? '').join('')
}
