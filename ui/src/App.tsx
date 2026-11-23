import { useState } from 'react'
import { Link, NavLink, Route, Routes } from 'react-router-dom'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Clock3, Menu, Moon, Sun, X } from 'lucide-react'
import { simulateDays } from './api'
import './App.css'
import { Button, Tooltip, useToast } from './components/DesignSystem'
import { NotFoundPage } from './components/NotFoundPage'
import { useApiHealth, useVirtualClock } from './hooks'
import AuditPage from './pages/AuditPage'
import DashboardPage from './pages/DashboardPage'
import NewMeetingPage from './pages/NewMeetingPage'
import ReviewPage from './pages/ReviewPage'
import TrackerPage from './pages/TrackerPage'
import { queryKeys } from './queryKeys'
import { useTheme } from './theme/ThemeProvider'
import { formatDate } from './utils/format'

/** Renders the app navigation, routes, and shared demo controls. */
export default function App() {
  return (
    <div className="app-shell">
      <AppHeader />
      <a className="skip-link" href="#main-content">Skip to content</a>
      <main id="main-content">
        <Routes>
          <Route path="/" element={<NewMeetingPage />} />
          <Route path="/meetings/:id/review" element={<ReviewPage />} />
          <Route path="/tracker" element={<TrackerPage />} />
          <Route path="/dashboard" element={<DashboardPage />} />
          <Route path="/items/:id/audit" element={<AuditPage />} />
          <Route path="*" element={<NotFoundPage />} />
        </Routes>
      </main>
    </div>
  )
}

/** Provides responsive navigation, system status, and global demo controls. */
function AppHeader() {
  const queryClient = useQueryClient()
  const { showToast } = useToast()
  const { theme, toggleTheme } = useTheme()
  const [navOpen, setNavOpen] = useState(false)
  const simulate = useMutation({
    mutationFn: () => simulateDays(1),
    onSuccess: async (result) => {
      queryClient.setQueryData(queryKeys.clock, { currentDate: result.utcNow })
      await queryClient.invalidateQueries()
      // Report the reminder count returned by the completed follow-up pass.
      const reminderLabel = `${result.remindersSent} reminder${result.remindersSent === 1 ? '' : 's'} sent`
      showToast(`Moved to ${formatDate(result.utcNow)}. ${reminderLabel}.`, 'success')
    },
    onError: (error: Error) => showToast(error.message, 'error'),
  })

  return (
    <header className="topbar">
      <Link className="brand" to="/" onClick={() => setNavOpen(false)}>Meeting Actions</Link>
      <Button
        className="nav-toggle"
        variant="secondary"
        aria-expanded={navOpen}
        aria-controls="primary-navigation"
        aria-label={navOpen ? 'Close navigation menu' : 'Open navigation menu'}
        onClick={() => setNavOpen((open) => !open)}
      >
        {navOpen ? <X size={18} aria-hidden="true" /> : <Menu size={18} aria-hidden="true" />}
      </Button>
      <nav id="primary-navigation" className={navOpen ? 'primary-navigation nav-open' : 'primary-navigation'} aria-label="Main navigation">
        <NavLink to="/" onClick={() => setNavOpen(false)}>New meeting</NavLink>
        <NavLink to="/tracker" onClick={() => setNavOpen(false)}>Tracker</NavLink>
        <NavLink to="/dashboard" onClick={() => setNavOpen(false)}>Dashboard</NavLink>
      </nav>
      <div className="topbar-actions">
        <ApiStatusIndicator />
        <SimulatedDate />
        <Button variant="secondary" loading={simulate.isPending} onClick={() => simulate.mutate()}>
          Simulate tomorrow
        </Button>
        <Button
          variant="ghost"
          className="theme-toggle"
          onClick={toggleTheme}
          aria-label={`Switch to ${theme === 'light' ? 'dark' : 'light'} theme`}
        >
          {theme === 'light' ? <Moon size={16} aria-hidden="true" /> : <Sun size={16} aria-hidden="true" />}
          <span>{theme === 'light' ? 'Dark' : 'Light'}</span>
        </Button>
      </div>
    </header>
  )
}

/** Polls API health and exposes connection details to mouse and keyboard users. */
function ApiStatusIndicator() {
  const query = useApiHealth()
  const isOnline = query.isSuccess && query.data.status.toLowerCase() === 'healthy' && !query.isError
  const state = isOnline ? 'online' : query.isError ? 'offline' : 'checking'
  const updatedAt = query.dataUpdatedAt || query.errorUpdatedAt
  const lastChecked = updatedAt ? formatDateTime(updatedAt) : 'Not checked yet'
  const details = `API ${isOnline ? 'connected' : query.isError ? 'unavailable' : 'checking'}. Last checked: ${lastChecked}.${query.data?.version ? ` Version ${query.data.version}.` : ''}`

  return (
    <Tooltip label={details}>
      <span className={`api-status-dot api-status-${state}`} aria-hidden="true" />
    </Tooltip>
  )
}

/** Displays the backend's virtual clock in the global header. */
function SimulatedDate() {
  const query = useVirtualClock()
  return (
    <div className="simulated-date" aria-label={`Simulated date: ${query.data ? formatDate(query.data.currentDate) : 'loading'}`}>
      <Clock3 size={15} aria-hidden="true" />
      <span>Simulated date <strong>{query.data ? formatDate(query.data.currentDate) : 'Loading…'}</strong></span>
    </div>
  )
}

/** Formats the latest API check timestamp for the health tooltip. */
function formatDateTime(timestamp: number): string {
  return new Intl.DateTimeFormat('en-GB', { dateStyle: 'medium', timeStyle: 'short' }).format(timestamp)
}
