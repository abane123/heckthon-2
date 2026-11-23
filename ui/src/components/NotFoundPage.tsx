import { Link } from 'react-router-dom'

/** Provides a friendly destination for unknown application routes. */
export function NotFoundPage() {
  return (
    <section className="empty-state" aria-labelledby="not-found-title">
      <h1 id="not-found-title">Page not found</h1>
      <p>The page may have moved or the address may be incorrect.</p>
      <Link to="/">Return to new meeting</Link>
    </section>
  )
}
