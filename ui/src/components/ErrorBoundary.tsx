import { Component } from 'react'
import type { ErrorInfo, ReactNode } from 'react'

type ErrorBoundaryProps = { children: ReactNode }
type ErrorBoundaryState = { hasError: boolean }

/** Catches render errors and presents a safe recovery action. */
export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  /** Initializes the boundary in its normal rendering state. */
  public state: ErrorBoundaryState = { hasError: false }

  /** Switches to the fallback when a child component throws. */
  public static getDerivedStateFromError(): ErrorBoundaryState {
    return { hasError: true }
  }

  /** Records render errors without exposing internal details in the page. */
  public componentDidCatch(error: Error, info: ErrorInfo): void {
    console.error('UI rendering failed', error, info.componentStack)
  }

  /** Renders the app or a friendly page-level recovery view. */
  public render(): ReactNode {
    if (this.state.hasError) {
      return (
        <main className="error-boundary" role="alert">
          <h1>Something went wrong</h1>
          <p>We could not display this page. Reload to try again.</p>
          <button type="button" onClick={() => window.location.reload()}>Reload</button>
        </main>
      )
    }
    return this.props.children
  }
}
