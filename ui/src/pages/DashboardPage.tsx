import { useMemo } from 'react'
import { Link } from 'react-router-dom'
import { Card, EmptyState, ErrorState, Skeleton, StatCard } from '../components/DesignSystem'
import { useActionItems, useDashboard, useMeetings, useNextAgenda } from '../hooks'
import type { ActionStatus } from '../types'
import { formatDate, formatPercent } from '../utils/format'

/** Lists the statuses included in open-work totals and owner workload charts. */
const openWorkStatuses: ActionStatus[] = ['Approved', 'Created', 'InProgress', 'Overdue', 'Escalated']

/** Summarizes follow-up metrics, owner workloads, and the next meeting agenda. */
export default function DashboardPage() {
  const dashboardQuery = useDashboard()
  const itemsQuery = useActionItems()
  const meetingsQuery = useMeetings()
  const latestMeeting = useMemo(() => [...(meetingsQuery.data ?? [])].sort((first, second) =>
    new Date(second.meetingDate).getTime() - new Date(first.meetingDate).getTime())[0], [meetingsQuery.data])
  const agendaQuery = useNextAgenda(latestMeeting?.id ?? '')
  const items = itemsQuery.data ?? []
  const dashboard = dashboardQuery.data
  const openCount = items.filter((item) => openWorkStatuses.includes(item.status)).length
  const escalatedCount = items.filter((item) => item.status === 'Escalated').length
  const ownerCounts = Object.entries(dashboard?.openItemsPerOwner ?? {}).sort((first, second) => second[1] - first[1])
  const isLoading = dashboardQuery.isLoading || itemsQuery.isLoading || meetingsQuery.isLoading
  const error = dashboardQuery.error ?? itemsQuery.error ?? meetingsQuery.error

  /** Retries all dashboard data requests after a visible load failure. */
  function retryDashboard() {
    void dashboardQuery.refetch()
    void itemsQuery.refetch()
    void meetingsQuery.refetch()
  }

  return (
    <section className="page dashboard-page">
      <div className="page-heading">
        <div><p className="eyebrow">Follow-up health</p><h1>Dashboard</h1><p className="muted">A live view of delivery and outstanding work.</p></div>
      </div>
      {isLoading && <Skeleton rows={4} />}
      {error && <ErrorState message={error.message} onRetry={retryDashboard} />}
      {!isLoading && !error && items.length === 0 && (
        <EmptyState title="No meeting data yet" description="Create a meeting to see action-item and follow-up metrics."
          action={<Link to="/">Create a meeting</Link>} />
      )}
      {!isLoading && !error && dashboard && items.length > 0 && <>
        <div className="dashboard-stat-grid">
          <StatCard label="On-time rate" value={dashboard.onTimeRate === null ? 'No data yet' : formatPercent(dashboard.onTimeRate)}
            explanation="Completed items with no recorded slips divided by all completed items." />
          <StatCard label="Slip rate" value={dashboard.slipRate === null ? 'No data yet' : formatPercent(dashboard.slipRate)}
            explanation="Due items with one or more recorded slips divided by all items with a due date." />
          <StatCard label="Open items" value={openCount.toLocaleString('en-GB')}
            explanation="Approved, ticket-created, in-progress, overdue, or escalated items. Draft, rejected, and completed items are excluded." />
          <StatCard label="Escalated items" value={escalatedCount.toLocaleString('en-GB')} explanation="Open items whose workflow status is Escalated." />
        </div>
        <div className="dashboard-content-grid">
          <Card className="owner-chart-card">
            <h2>Open items per owner</h2>
            {ownerCounts.length === 0 ? <p className="muted">No open items by owner.</p> : <OwnerBarChart data={ownerCounts} />}
          </Card>
          <Card className="agenda-card">
            <div className="agenda-heading"><div><p className="eyebrow">Carry over</p><h2>Next meeting agenda</h2></div>
              {latestMeeting && <span className="meeting-series">{latestMeeting.title} · {formatDate(latestMeeting.meetingDate)}</span>}
            </div>
            {!latestMeeting && <p className="muted">Create another meeting to view carried-over actions.</p>}
            {latestMeeting && agendaQuery.isLoading && <Skeleton rows={2} />}
            {latestMeeting && agendaQuery.isError && <ErrorState message={agendaQuery.error.message} onRetry={() => void agendaQuery.refetch()} />}
            {latestMeeting && !agendaQuery.isLoading && !agendaQuery.isError && (agendaQuery.data?.length ?? 0) === 0 && (
              <p className="muted">No open actions are carried over from an earlier meeting in this series.</p>
            )}
            <ul className="agenda-list">{agendaQuery.data?.map((item) => (
              <li key={item.id}>
                <div><strong>{item.title}</strong><span>{item.ownerName ?? 'Unassigned'} · Due {formatDate(item.dueDate)}</span></div>
                <span className="agenda-slips">{item.slipCount} {item.slipCount === 1 ? 'slip' : 'slips'}</span>
              </li>
            ))}</ul>
          </Card>
        </div>
      </>}
    </section>
  )
}

/** Renders an accessible SVG owner chart with a screen-reader data table. */
function OwnerBarChart({ data }: { data: Array<[string, number]> }) {
  const chartWidth = 720
  const rowHeight = 42
  const chartHeight = Math.max(130, data.length * rowHeight + 20)
  const labelWidth = 180
  const maxBarWidth = chartWidth - labelWidth - 60
  const maximum = Math.max(1, ...data.map(([, count]) => count))

  return (
    <figure className="owner-chart">
      <svg viewBox={`0 0 ${chartWidth} ${chartHeight}`} role="img" aria-labelledby="owner-chart-title owner-chart-description">
        <title id="owner-chart-title">Open action items per owner</title>
        <desc id="owner-chart-description">{data.map(([owner, count]) => `${owner}: ${count}`).join('; ')}</desc>
        {data.map(([owner, count], index) => {
          const y = 12 + index * rowHeight
          const width = Math.max(count > 0 ? 6 : 0, (count / maximum) * maxBarWidth)
          return (
            <g key={owner}>
              <text x="0" y={y + 22} className="chart-label">{owner}</text>
              <rect x={labelWidth} y={y + 5} width={width} height="24" rx="5" className="chart-bar" />
              <text x={labelWidth + width + 10} y={y + 22} className="chart-value">{count}</text>
            </g>
          )
        })}
      </svg>
      <table className="visually-hidden">
        <caption>Open action items per owner</caption>
        <thead><tr><th scope="col">Owner</th><th scope="col">Open items</th></tr></thead>
        <tbody>{data.map(([owner, count]) => <tr key={owner}><th scope="row">{owner}</th><td>{count}</td></tr>)}</tbody>
      </table>
    </figure>
  )
}
