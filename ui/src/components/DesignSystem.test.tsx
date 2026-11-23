import { useState } from 'react'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { Badge, Button, Card, ChipInput, ConfidenceBar, Modal, Spinner, StatusBadge } from './DesignSystem'

/** Covers shared attendee-entry and modal accessibility behavior. */
describe('shared design-system controls', () => {
  afterEach(() => cleanup())

  /** Adds trimmed attendee chips, ignores case-insensitive duplicates, and removes by Backspace. */
  it('supports ChipInput add, duplicate, and remove keyboard interactions', async () => {
    const user = userEvent.setup()
    const onChange = vi.fn()
    /** Keeps ChipInput controlled while exposing updates for assertions. */
    function ControlledChipInput() {
      const [value, setValue] = useState<string[]>([])
      return <ChipInput label="Attendees" value={value} onChange={(next) => { setValue(next); onChange(next) }} />
    }

    render(<ControlledChipInput />)
    const input = screen.getByRole('textbox', { name: 'Attendees' })
    await user.type(input, ' Priya,')
    expect(screen.getByText('Priya')).toBeTruthy()
    await user.type(input, 'priya{enter}')
    expect(screen.getAllByText('Priya')).toHaveLength(1)
    await user.keyboard('{Backspace}')
    expect(screen.queryByText('Priya')).toBeNull()
    expect(onChange).toHaveBeenCalled()
  })

  /** Traps keyboard focus, closes on Escape, and returns focus to the opener. */
  it('traps modal focus and restores it after Escape dismissal', async () => {
    const user = userEvent.setup()
    /** Controls the dialog so its opener remains available for focus restoration. */
    function ControlledModal() {
      const [open, setOpen] = useState(false)
      return (
        <>
          <button type="button" onClick={() => setOpen(true)}>Open dialog</button>
          <Modal open={open} title="Confirm" onClose={() => setOpen(false)}>
            <button type="button">Continue</button>
          </Modal>
        </>
      )
    }
    render(<ControlledModal />)
    const opener = screen.getByRole('button', { name: 'Open dialog' })
    await user.click(opener)
    const closeButton = await screen.findByRole('button', { name: 'Close dialog' })
    await waitFor(() => expect(document.activeElement).toBe(closeButton))
    screen.getByRole('button', { name: 'Continue' }).focus()
    await user.tab()
    expect(document.activeElement).toBe(closeButton)
    await user.keyboard('{Escape}')
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(document.activeElement).toBe(opener)
  })

  /** Exposes loading state and prevents repeat activation while a button is busy. */
  it('renders a disabled loading button with an accessible status indicator', () => {
    render(<Button loading>Saving</Button>)

    expect(screen.getByRole('button', { name: /Saving/ }).hasAttribute('disabled')).toBe(true)
    expect(screen.getByRole('status', { name: 'Loading' })).toBeTruthy()
  })

  /** Presents friendly status labels and a numeric confidence progress bar. */
  it('renders status badges, confidence, and a semantic card surface', () => {
    render(
      <Card>
        <Badge tone="success">Saved</Badge>
        <StatusBadge status="InProgress" />
        <ConfidenceBar confidence={0.8} />
      </Card>,
    )

    expect(screen.getByText('Saved')).toBeTruthy()
    expect(screen.getByText('In progress')).toBeTruthy()
    expect(screen.getByRole('progressbar', { name: 'Extraction confidence' }).getAttribute('aria-valuenow')).toBe('80')
  })

  /** Makes the reusable spinner available to assistive technology. */
  it('renders a labelled spinner for standalone loading content', () => {
    render(<Spinner label="Loading meetings" />)

    expect(screen.getByRole('status', { name: 'Loading meetings' })).toBeTruthy()
  })
})
