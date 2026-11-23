import { createContext, useContext, useEffect, useId, useRef, useState } from 'react'
import type { ButtonHTMLAttributes, HTMLAttributes, KeyboardEvent as ReactKeyboardEvent, ReactNode, TableHTMLAttributes } from 'react'
import { createPortal } from 'react-dom'
import { AlertCircle, Check, ChevronDown, Info, LoaderCircle, X } from 'lucide-react'
import type { ActionStatus } from '../types'

/** Defines the supported visual styles for the shared Button component. */
export type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger'

/** Defines props accepted by the shared Button component. */
export type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: ButtonVariant
  loading?: boolean
}

/** Provides a consistent button with touch targets and a loading state. */
export function Button({ variant = 'primary', loading = false, disabled, children, className = '', ...props }: ButtonProps) {
  return (
    <button
      {...props}
      className={`button button-${variant} ${className}`.trim()}
      disabled={disabled || loading}
      aria-busy={loading || undefined}
    >
      {loading && <Spinner size="small" label="Loading" />}
      {children}
    </button>
  )
}

/** Renders compact contextual text with a colour variant. */
export function Badge({ children, tone = 'neutral' }: { children: ReactNode; tone?: 'neutral' | 'success' | 'warning' | 'danger' | 'info' }) {
  return <span className={`badge badge-${tone}`}>{children}</span>
}

const statusLabels: Record<ActionStatus, string> = {
  Draft: 'Draft',
  Approved: 'Approved',
  Created: 'Ticket created',
  InProgress: 'In progress',
  Overdue: 'Overdue',
  Escalated: 'Escalated',
  Done: 'Done',
  Rejected: 'Rejected',
}

const statusTones: Record<ActionStatus, 'neutral' | 'success' | 'warning' | 'danger' | 'info'> = {
  Draft: 'neutral',
  Approved: 'info',
  Created: 'info',
  InProgress: 'info',
  Overdue: 'warning',
  Escalated: 'danger',
  Done: 'success',
  Rejected: 'neutral',
}

/** Displays a user-friendly label for a workflow status. */
export function StatusBadge({ status }: { status: ActionStatus }) {
  return <Badge tone={statusTones[status]}>{statusLabels[status]}</Badge>
}

/** Displays confidence as a labelled, accessible progress bar. */
export function ConfidenceBar({ confidence }: { confidence: number }) {
  const boundedConfidence = Math.min(1, Math.max(0, confidence))
  const percent = Math.round(boundedConfidence * 100)
  const tier = boundedConfidence >= 0.75 ? 'high' : boundedConfidence >= 0.5 ? 'medium' : 'low'
  return (
    <div className={`confidence confidence-${tier}`}>
      <div className="confidence-track" role="progressbar" aria-label="Extraction confidence"
        aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent}>
        <span style={{ width: `${percent}%` }} />
      </div>
      <span>{percent}% · {tier} confidence</span>
    </div>
  )
}

/** Provides a bordered surface for related content. */
export function Card({ className = '', ...props }: HTMLAttributes<HTMLElement>) {
  return <section {...props} className={`card ${className}`.trim()} />
}

/** Renders a removable or informational chip. */
export function Chip({ children, onRemove, className = '' }: { children: ReactNode; onRemove?: () => void; className?: string }) {
  return (
    <span className={`chip ${className}`.trim()}>
      {children}
      {onRemove && <button type="button" className="chip-remove" aria-label={`Remove ${String(children)}`} onClick={onRemove}><X size={14} /></button>}
    </span>
  )
}

/** Defines props accepted by the accessible attendee ChipInput. */
export type ChipInputProps = {
  id?: string
  label: string
  value: string[]
  onChange: (value: string[]) => void
  hint?: string
  error?: string
  disabled?: boolean
}

/** Lets users add attendees with Enter or comma and remove them with Backspace. */
export function ChipInput({ id, label, value, onChange, hint, error, disabled }: ChipInputProps) {
  const generatedId = useId()
  const inputId = id ?? generatedId
  const [draft, setDraft] = useState('')
  const hintId = `${inputId}-hint`
  const errorId = `${inputId}-error`

  /** Adds a trimmed, non-duplicate attendee to the value list. */
  function addDraft() {
    const attendee = draft.trim().replace(/,$/, '').trim()
    if (attendee && !value.some((current) => current.toLocaleLowerCase() === attendee.toLocaleLowerCase())) {
      onChange([...value, attendee])
    }
    setDraft('')
  }

  /** Handles keyboard shortcuts for adding and removing attendee chips. */
  function handleKeyDown(event: ReactKeyboardEvent<HTMLInputElement>) {
    if (event.key === 'Enter' || event.key === ',') {
      event.preventDefault()
      addDraft()
    } else if (event.key === 'Backspace' && draft.length === 0 && value.length > 0) {
      onChange(value.slice(0, -1))
    }
  }

  return (
    <div className="field">
      <label htmlFor={inputId}>{label}</label>
      <div className={`chip-input ${error ? 'chip-input-error' : ''}`}>
        {value.map((attendee) => (
          <Chip key={attendee} onRemove={() => onChange(value.filter((current) => current !== attendee))}>{attendee}</Chip>
        ))}
        <input
          id={inputId}
          value={draft}
          disabled={disabled}
          onChange={(event) => {
            if (event.target.value.includes(',')) {
              const parts = event.target.value.split(',')
              const attendees = parts.slice(0, -1).map((part) => part.trim()).filter(Boolean)
              const uniqueAttendees = attendees.filter((attendee, index) =>
                attendees.findIndex((candidate) => candidate.toLocaleLowerCase() === attendee.toLocaleLowerCase()) === index
                && !value.some((current) => current.toLocaleLowerCase() === attendee.toLocaleLowerCase()))
              if (uniqueAttendees.length > 0) onChange([...value, ...uniqueAttendees])
              setDraft(parts.at(-1)?.trim() ?? '')
            } else {
              setDraft(event.target.value)
            }
          }}
          onKeyDown={handleKeyDown}
          onBlur={addDraft}
          aria-invalid={Boolean(error)}
          aria-describedby={[hint ? hintId : '', error ? errorId : ''].filter(Boolean).join(' ') || undefined}
          aria-label={label}
        />
      </div>
      {hint && <small id={hintId} className="field-hint">{hint}</small>}
      {error && <small id={errorId} className="field-error" role="alert">{error}</small>}
    </div>
  )
}

/** Defines props accepted by a modal dialog. */
export type ModalProps = {
  open: boolean
  title: string
  onClose: () => void
  children: ReactNode
  className?: string
}

/** Renders an accessible modal with focus containment and Escape dismissal. */
export function Modal({ open, title, onClose, children, className = '' }: ModalProps) {
  const panelRef = useRef<HTMLDivElement>(null)
  const titleId = useId()
  const closeRef = useRef(onClose)

  useEffect(() => {
    closeRef.current = onClose
  }, [onClose])

  useEffect(() => {
    if (!open) return
    const previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null
    const panel = panelRef.current
    const focusableSelector = 'button:not([disabled]), a[href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'

    /** Keeps keyboard focus in the open dialog and handles its dismissal key. */
    function handleKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') {
        closeRef.current()
        return
      }
      if (event.key !== 'Tab' || !panel) return
      const focusable = Array.from(panel.querySelectorAll<HTMLElement>(focusableSelector))
      if (focusable.length === 0) {
        event.preventDefault()
        panel.focus()
        return
      }
      const first = focusable[0]
      const last = focusable[focusable.length - 1]
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault()
        last?.focus()
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault()
        first?.focus()
      }
    }

    document.addEventListener('keydown', handleKeyDown)
    const focusTimer = window.setTimeout(() => (panel?.querySelector<HTMLElement>(focusableSelector) ?? panel)?.focus(), 0)
    return () => {
      window.clearTimeout(focusTimer)
      document.removeEventListener('keydown', handleKeyDown)
      previousFocus?.focus()
    }
  }, [open])

  if (!open) return null
  return createPortal(
    <div className="modal-backdrop">
      <div ref={panelRef} className={`modal-panel ${className}`.trim()} role="dialog" aria-modal="true" aria-labelledby={titleId} tabIndex={-1}>
        <header className="modal-header"><h2 id={titleId}>{title}</h2><Button variant="ghost" aria-label="Close dialog" onClick={onClose}><X size={18} /></Button></header>
        {children}
      </div>
    </div>,
    document.body,
  )
}

/** Presents an explicit confirmation before a consequential action. */
export function ConfirmDialog({ open, title, message, confirmLabel = 'Confirm', onConfirm, onCancel }: {
  open: boolean
  title: string
  message: ReactNode
  confirmLabel?: string
  onConfirm: () => void
  onCancel: () => void
}) {
  // Complex confirmation content may contain lists or headings, so only strings belong in a paragraph.
  const messageContent = typeof message === 'string' ? <p>{message}</p> : message
  return (
    <Modal open={open} title={title} onClose={onCancel} className="confirm-dialog">
      <div className="modal-content">{messageContent}</div>
      <footer className="modal-actions"><Button variant="secondary" onClick={onCancel}>Cancel</Button><Button onClick={onConfirm}>{confirmLabel}</Button></footer>
    </Modal>
  )
}

/** Presents slide-over content using the same focus and keyboard behavior as Modal. */
export function Drawer({ open, title, onClose, children }: ModalProps) {
  return <Modal open={open} title={title} onClose={onClose} className="drawer-panel">{children}</Modal>
}

type ToastMessage = { id: number; message: string; tone: 'success' | 'error' | 'info' }
type ToastContextValue = { showToast: (message: string, tone?: ToastMessage['tone']) => void }

/** Provides an aria-live toast region and a small imperative message API. */
export function ToastProvider({ children }: { children: ReactNode }) {
  const [messages, setMessages] = useState<ToastMessage[]>([])
  const nextId = useRef(0)

  /** Adds a temporary toast that automatically disappears after a short delay. */
  function showToast(message: string, tone: ToastMessage['tone'] = 'success') {
    const id = ++nextId.current
    setMessages((current) => [...current, { id, message, tone }])
    window.setTimeout(() => setMessages((current) => current.filter((entry) => entry.id !== id)), 5000)
  }

  return (
    <ToastContext.Provider value={{ showToast }}>
      {children}
      <div className="toast-region" aria-live="polite" aria-atomic="false">
        {messages.map((entry) => <div key={entry.id} className={`toast toast-${entry.tone}`} role="status">{entry.message}</div>)}
      </div>
    </ToastContext.Provider>
  )
}

const ToastContext = createContext<ToastContextValue | null>(null)

/** Returns a toast-dispatch function from the nearest ToastProvider. */
export function useToast(): ToastContextValue {
  const context = useContext(ToastContext)
  if (!context) throw new Error('useToast must be used within a ToastProvider.')
  return context
}

/** Shows an animated loading indicator with a screen-reader label. */
export function Spinner({ size = 'medium', label = 'Loading' }: { size?: 'small' | 'medium' | 'large'; label?: string }) {
  return <LoaderCircle className={`spinner spinner-${size}`} aria-label={label} role="status" />
}

/** Renders a content placeholder while query data is being loaded. */
export function Skeleton({ rows = 3 }: { rows?: number }) {
  return <div className="skeleton-list" aria-label="Loading content" role="status">
    {Array.from({ length: rows }, (_, index) => <span className="skeleton-row" key={index} />)}
  </div>
}

/** Explains an empty collection and optionally offers a next action. */
export function EmptyState({ title, description, action }: { title: string; description: string; action?: ReactNode }) {
  return <div className="empty-state" role="status"><Check size={24} aria-hidden="true" /><h2>{title}</h2><p>{description}</p>{action}</div>
}

/** Presents a friendly error and an optional retry action. */
export function ErrorState({ title = 'Could not load this information', message, onRetry }: { title?: string; message: string; onRetry?: () => void }) {
  return <div className="error-state" role="alert"><AlertCircle size={20} aria-hidden="true" /><div><strong>{title}</strong><p>{message}</p>{onRetry && <Button variant="secondary" onClick={onRetry}>Retry</Button>}</div></div>
}

/** Displays a dashboard metric and its explanatory tooltip. */
export function StatCard({ label, value, explanation }: { label: string; value: string; explanation: string }) {
  return <Card className="stat-card"><span>{label}</span><strong>{value}</strong><Tooltip label={explanation}><Info size={15} aria-hidden="true" /></Tooltip></Card>
}

/** Adds an accessible keyboard-focusable explanation to its child. */
export function Tooltip({ label, children }: { label: string; children: ReactNode }) {
  return (
    <details className="tooltip">
      <summary aria-label={label}><span aria-hidden="true">{children}</span></summary>
      <span className="tooltip-content" role="tooltip">{label}</span>
    </details>
  )
}

/** Defines a labelled, typed column in a reusable table. */
export type TableColumn<Row> = { key: string; label: string; render: (row: Row) => ReactNode }

/** Renders a semantic table with a sticky header and keyed rows. */
export function Table<Row extends { id: string }>({
  columns,
  rows,
  caption,
  className = '',
  ...props
}: TableHTMLAttributes<HTMLTableElement> & { columns: TableColumn<Row>[]; rows: Row[]; caption: string }) {
  return (
    <div className="table-scroll">
      <table {...props} className={`data-table ${className}`.trim()}>
        <caption className="visually-hidden">{caption}</caption>
        <thead><tr>{columns.map((column) => <th scope="col" key={column.key}>{column.label}</th>)}</tr></thead>
        <tbody>{rows.map((row) => <tr key={row.id}>{columns.map((column) => <td key={column.key}>{column.render(row)}</td>)}</tr>)}</tbody>
      </table>
    </div>
  )
}

/** Provides an expandable text control for compact explanatory content. */
export function Disclosure({ label, children }: { label: string; children: ReactNode }) {
  return <details className="disclosure"><summary>{label}<ChevronDown size={16} aria-hidden="true" /></summary>{children}</details>
}

/** Provides a generic close button for custom dialog and card headings. */
export function CloseButton({ onClick }: { onClick: () => void }) {
  return <Button variant="ghost" aria-label="Close" onClick={onClick}><X size={18} /></Button>
}
