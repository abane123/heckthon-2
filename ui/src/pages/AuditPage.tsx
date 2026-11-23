import { Link, useParams } from 'react-router-dom'
import { EmptyState, ErrorState, Skeleton } from '../components/DesignSystem'
import { useAudit } from '../hooks'

/** Displays the recorded audit events for one action item. */
export default function AuditPage() {
  const { id = '' } = useParams()
  const query = useAudit(id)
  return (
    <section className="page narrow-page">
      <div className="page-heading">
        <div><p className="eyebrow">Action history</p><h1>Audit trail</h1></div>
        <Link to="/tracker">Back to tracker</Link>
      </div>
      {query.isLoading && <Skeleton rows={4} />}
      {query.isError && <ErrorState message={query.error.message} onRetry={() => void query.refetch()} />}
      {query.data?.length === 0 && <EmptyState title="No audit events" description="No changes have been recorded for this action item." />}
      <ol className="timeline">{query.data?.map((event) => (
        <li key={event.id}><strong>{event.eventType}</strong><span>{event.actor}</span><time>{formatDateTime(event.createdAt)}</time></li>
      ))}</ol>
    </section>
  )
}

/** Formats an audit timestamp using a localized date and time. */
function formatDateTime(value: string): string {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : new Intl.DateTimeFormat('en-GB', { dateStyle: 'medium', timeStyle: 'short' }).format(date)
}
