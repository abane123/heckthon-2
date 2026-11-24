import { useEffect, useRef, useState } from 'react'
import type { ChangeEvent, DragEvent, FormEvent } from 'react'
import { useMutation } from '@tanstack/react-query'
import { useNavigate } from 'react-router-dom'
import { Button, Card, ChipInput, ErrorState } from '../components/DesignSystem'
import { createMeeting, getSampleTranscript } from '../api'
import { useLlmInfo } from '../hooks'
import type { CreateMeetingInput } from '../types'

const draftStorageKey = 'meeting-actions-new-meeting-draft'
const transcriptLimitBytes = 1_048_576
const progressLabels = ['Reading transcript', 'Extracting items', 'Resolving owners and dates']

type MeetingDraft = { title: string; date: string; attendees: string[]; transcript: string }
type FieldErrors = Partial<Record<keyof MeetingDraft, string>>

/** Provides transcript entry, validation, draft recovery, and extraction submission. */
export default function NewMeetingPage() {
  const navigate = useNavigate()
  const llmInfoQuery = useLlmInfo()
  const titleRef = useRef<HTMLInputElement>(null)
  const dateRef = useRef<HTMLInputElement>(null)
  const transcriptRef = useRef<HTMLTextAreaElement>(null)
  const fileInputRef = useRef<HTMLInputElement>(null)
  const abortRef = useRef<AbortController | null>(null)
  const [title, setTitle] = useState('')
  const [date, setDate] = useState(() => new Date().toISOString().slice(0, 10))
  const [attendees, setAttendees] = useState<string[]>([])
  const [transcript, setTranscript] = useState('')
  const [errors, setErrors] = useState<FieldErrors>({})
  const [formError, setFormError] = useState('')
  const [draftReady, setDraftReady] = useState(false)
  const [restoreAvailable, setRestoreAvailable] = useState(() => Boolean(sessionStorage.getItem(draftStorageKey)))
  const [draggingFile, setDraggingFile] = useState(false)
  const [progressStep, setProgressStep] = useState(0)
  const [progressPart, setProgressPart] = useState(1)
  const llmInfo = llmInfoQuery.data
  const ollamaSelected = llmInfo?.provider.toLowerCase() === 'ollama'
  const ollamaUnavailable = Boolean(ollamaSelected && llmInfo?.status !== 'Healthy')
  const extractionDisabled = ollamaUnavailable && !llmInfo?.fallbackToStub
  const estimatedChunkCount = ollamaSelected
    ? estimateTranscriptChunkCount(transcript, llmInfo?.maxTranscriptCharsPerChunk ?? 12000)
    : 1

  const submit = useMutation({
    mutationFn: ({ input, signal }: { input: CreateMeetingInput; signal: AbortSignal }) => createMeeting(input, signal),
    onSuccess: (meeting) => {
      sessionStorage.removeItem(draftStorageKey)
      navigate(`/meetings/${meeting.id}/review`)
    },
    onError: (reason: Error) => {
      setFormError(reason.name === 'AbortError' ? 'Extraction cancelled. Your draft is still saved.' : reason.message)
      abortRef.current = null
    },
  })

  useEffect(() => {
    if (!draftReady) return
    const draft: MeetingDraft = { title, date, attendees, transcript }
    sessionStorage.setItem(draftStorageKey, JSON.stringify(draft))
  }, [attendees, date, draftReady, title, transcript])

  useEffect(() => {
    if (!submit.isPending) return
    const timerId = window.setInterval(() => {
      if (progressStep === 0) {
        setProgressStep(1)
      } else if (progressStep === 1 && progressPart < estimatedChunkCount) {
        setProgressPart((current) => Math.min(current + 1, estimatedChunkCount))
      } else {
        setProgressStep(2)
      }
    }, 850)
    return () => window.clearInterval(timerId)
  }, [estimatedChunkCount, progressPart, progressStep, submit.isPending])

  /** Marks the current form as edited and persists it for this browser session. */
  function markDraftChanged() {
    setDraftReady(true)
    setRestoreAvailable(false)
  }

  /** Restores the previously saved meeting form after validating its stored shape. */
  function restoreDraft() {
    const saved = sessionStorage.getItem(draftStorageKey)
    if (!saved) return
    try {
      const restored = JSON.parse(saved) as Partial<MeetingDraft>
      if (typeof restored.title !== 'string' || typeof restored.date !== 'string'
        || !Array.isArray(restored.attendees) || !restored.attendees.every((name) => typeof name === 'string')
        || typeof restored.transcript !== 'string') {
        throw new Error('The saved draft has an invalid format.')
      }
      setTitle(restored.title)
      setDate(restored.date)
      setAttendees(restored.attendees)
      setTranscript(restored.transcript)
      setDraftReady(true)
      setRestoreAvailable(false)
      setFormError('')
    } catch (reason) {
      setFormError(reason instanceof Error ? reason.message : 'The saved draft could not be restored.')
    }
  }

  /** Updates transcript text after checking an uploaded transcript's size and format. */
  async function loadFile(file: File) {
    const extension = file.name.split('.').pop()?.toLowerCase()
    if (extension !== 'txt' && extension !== 'vtt') {
      setErrors((current) => ({ ...current, transcript: 'Choose a .txt or .vtt transcript file.' }))
      return
    }
    if (file.size > transcriptLimitBytes) {
      setErrors((current) => ({ ...current, transcript: 'The transcript file must be 1 MB or smaller.' }))
      return
    }
    setTranscript(await file.text())
    setErrors((current) => ({ ...current, transcript: undefined }))
    setFormError('')
    markDraftChanged()
  }

  /** Handles native file-picker selection and reports unexpected read failures. */
  async function handleFileChange(event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0]
    if (!file) return
    try {
      await loadFile(file)
    } catch {
      setErrors((current) => ({ ...current, transcript: 'The selected transcript could not be read.' }))
    } finally {
      event.target.value = ''
    }
  }

  /** Accepts a dragged transcript file into the same validated file-loading path. */
  async function handleDrop(event: DragEvent<HTMLDivElement>) {
    event.preventDefault()
    setDraggingFile(false)
    const file = event.dataTransfer.files[0]
    if (!file) return
    try {
      await loadFile(file)
    } catch {
      setErrors((current) => ({ ...current, transcript: 'The selected transcript could not be read.' }))
    }
  }

  /** Validates required fields, focuses the first invalid control, and submits extraction. */
  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const nextErrors: FieldErrors = {}
    if (!title.trim()) nextErrors.title = 'Enter a meeting title.'
    if (!date) nextErrors.date = 'Choose the meeting date.'
    if (attendees.length === 0) nextErrors.attendees = 'Add at least one attendee.'
    if (!transcript.trim()) nextErrors.transcript = 'Paste or upload a transcript.'
    setErrors(nextErrors)
    setFormError('')
    if (Object.keys(nextErrors).length > 0) {
      if (nextErrors.title) titleRef.current?.focus()
      else if (nextErrors.date) dateRef.current?.focus()
      else if (nextErrors.attendees) document.getElementById('meeting-attendees')?.focus()
      else transcriptRef.current?.focus()
      return
    }

    setProgressStep(0)
    setProgressPart(1)
    abortRef.current = new AbortController()
    submit.mutate({
      input: { title: title.trim(), date: new Date(`${date}T00:00:00`).toISOString(), attendees, transcript },
      signal: abortRef.current.signal,
    })
  }

  /** Loads the bundled sample transcript into the editable and persisted form. */
  async function loadSample() {
    try {
      setTranscript(await getSampleTranscript())
      setErrors((current) => ({ ...current, transcript: undefined }))
      setFormError('')
      markDraftChanged()
    } catch (reason) {
      setFormError(reason instanceof Error ? reason.message : 'The sample transcript could not be loaded.')
    }
  }

  const transcriptBytes = new TextEncoder().encode(transcript).byteLength
  const transcriptOverLimit = transcriptBytes > transcriptLimitBytes
  const currentProgressLabels = progressLabels.map((label, index) =>
    index === 1 && ollamaSelected && estimatedChunkCount > 1
      ? `Analysing part ${Math.min(progressPart, estimatedChunkCount)} of ${estimatedChunkCount}`
      : label)

  return (
    <section className="page">
      <div className="page-heading">
        <div>
          <p className="eyebrow">Capture and extract</p>
          <h1>New meeting</h1>
          <p className="muted">Paste a transcript and review the action items it contains.</p>
        </div>
      </div>
      <div className="new-meeting-layout">
        <Card className="meeting-form-card">
          <form className="meeting-form" onSubmit={handleSubmit} onChange={markDraftChanged} noValidate>
            <fieldset className="meeting-inputs" disabled={submit.isPending}>
              <legend className="visually-hidden">Meeting details</legend>
              {restoreAvailable && (
                <div className="restore-banner" role="status">
                  <span>A saved meeting draft is available in this session.</span>
                  <Button variant="secondary" type="button" onClick={restoreDraft}>Restore draft</Button>
                </div>
              )}
              <div className="form-grid meeting-fields">
                <label htmlFor="meeting-title">Meeting title
                  <input ref={titleRef} id="meeting-title" value={title} onChange={(event) => {
                    setTitle(event.target.value)
                    setErrors((current) => ({ ...current, title: undefined }))
                  }}
                    placeholder="Release readiness" aria-invalid={Boolean(errors.title)} aria-describedby={errors.title ? 'title-error' : 'title-hint'} />
                  {errors.title ? <small id="title-error" className="field-error">{errors.title}</small> : <small id="title-hint" className="field-hint">Use the recurring series name when applicable.</small>}
                </label>
                <label htmlFor="meeting-date">Meeting date
                  <input ref={dateRef} id="meeting-date" type="date" value={date} onChange={(event) => {
                    setDate(event.target.value)
                    setErrors((current) => ({ ...current, date: undefined }))
                  }}
                    aria-invalid={Boolean(errors.date)} aria-describedby={errors.date ? 'date-error' : 'date-hint'} />
                  {errors.date ? <small id="date-error" className="field-error">{errors.date}</small> : <small id="date-hint" className="field-hint">Used to resolve relative deadlines.</small>}
                </label>
              </div>
              <ChipInput id="meeting-attendees" label="Attendees" value={attendees} onChange={(value) => {
                setAttendees(value)
                setErrors((current) => ({ ...current, attendees: value.length ? undefined : current.attendees }))
              }}
                hint="Type a name and press Enter or comma to add it."
                error={errors.attendees} />
              <div className="field">
                <label htmlFor="meeting-transcript">Transcript</label>
                <div
                  className={`transcript-dropzone ${draggingFile ? 'transcript-dropzone-active' : ''}`}
                  onDragOver={(event) => { event.preventDefault(); setDraggingFile(true) }}
                  onDragLeave={(event) => { if (event.currentTarget === event.target) setDraggingFile(false) }}
                  onDrop={handleDrop}
                >
                  <textarea ref={transcriptRef} id="meeting-transcript" rows={14} value={transcript}
                    onChange={(event) => {
                      setTranscript(event.target.value)
                      setErrors((current) => ({ ...current, transcript: undefined }))
                    }} placeholder="Paste the meeting transcript here…"
                    aria-invalid={Boolean(errors.transcript)}
                    aria-describedby={['transcript-hint', 'transcript-count', errors.transcript ? 'transcript-error' : ''].filter(Boolean).join(' ')} />
                  <div className="transcript-tools">
                    <input ref={fileInputRef} className="visually-hidden" type="file" accept=".txt,.vtt,text/plain,text/vtt"
                      aria-label="Upload transcript file" onChange={handleFileChange} />
                    <Button variant="secondary" type="button" onClick={() => fileInputRef.current?.click()}>Upload file</Button>
                    <Button variant="ghost" type="button" onClick={() => void loadSample()}>Load sample transcript</Button>
                    <span id="transcript-count" className="muted">{transcript ? transcript.split(/\r\n|\r|\n/).length : 0} lines · {transcript.length.toLocaleString('en-GB')} characters</span>
                  </div>
                </div>
                <small id="transcript-hint" className="field-hint">Drop a .txt or .vtt file here, or paste text. Maximum upload size: 1 MB.</small>
                {errors.transcript && <small id="transcript-error" className="field-error" role="alert">{errors.transcript}</small>}
                {transcriptOverLimit && <small className="field-warning" role="status">This pasted transcript exceeds the 1 MB upload limit and may not be accepted by the backend.</small>}
              </div>
            </fieldset>
            {formError && <ErrorState title={submit.isError ? 'Extraction failed' : 'Could not continue'} message={formError}
              onRetry={submit.isError ? () => {
                const form = document.querySelector<HTMLFormElement>('.meeting-form')
                form?.requestSubmit()
              } : undefined} />}
            {submit.isPending && (
              <ol className="progress-stepper" aria-label="Transcript processing steps">
                {currentProgressLabels.map((label, index) => (
                  <li key={label} className={index < progressStep ? 'step-done' : index === progressStep ? 'step-current' : ''}>
                    <span aria-hidden="true">{index < progressStep ? '✓' : index + 1}</span>{label}
                  </li>
                ))}
              </ol>
            )}
            <div className="button-row">
              {submit.isPending && <Button type="button" variant="ghost" onClick={() => abortRef.current?.abort()}>Cancel extraction</Button>}
              <Button type="submit" loading={submit.isPending} disabled={extractionDisabled}>Extract action items</Button>
            </div>
            <p className="analysis-engine" aria-live="polite">
              Analysis engine: {llmInfoQuery.isLoading
                ? 'Checking…'
                : !llmInfo
                  ? 'Unavailable'
                  : ollamaSelected ? `Ollama ${llmInfo.model}` : 'Built-in rules'}
            </p>
            {ollamaUnavailable && llmInfo && (
              <p className="llm-warning" role="status">
                Ollama is unavailable. {llmInfo.message}{' '}
                {llmInfo.fallbackToStub
                  ? 'Stub fallback is enabled.'
                  : 'Extract is disabled until Ollama is available or Stub fallback is enabled.'}
              </p>
            )}
          </form>
        </Card>
        <Card className="how-it-works">
          <p className="eyebrow">What happens next</p>
          <h2>How it works</h2>
          <ol className="how-steps">
            <li><strong>Paste</strong><span>Add the meeting transcript and attendees.</span></li>
            <li><strong>Extract</strong><span>Potential action items are identified with source quotes.</span></li>
            <li><strong>Review</strong><span>Confirm owners, dates, and confidence before approval.</span></li>
            <li><strong>Track</strong><span>Approved actions become tickets and follow-up reminders.</span></li>
          </ol>
          <h3>Tips for a useful transcript</h3>
          <ul className="transcript-tips">
            <li>Include speaker names and timestamps where possible.</li>
            <li>Keep the original wording so each action can be traced to its source.</li>
          </ul>
        </Card>
      </div>
    </section>
  )
}

/** Estimates the server's speaker-line chunks so pending UI progress reflects multi-part analysis. */
function estimateTranscriptChunkCount(transcript: string, maxCharacters: number): number {
  const maxLength = Math.max(1, maxCharacters)
  const lines = transcript.split(/\r\n|\r|\n/).filter((line) => line.length > 0)
  if (lines.length === 0) return 1

  let chunkCount = 0
  let current: string[] = []
  const joinedLength = (nextLine: string) => current.reduce((length, line) => length + line.length, 0)
    + Math.max(0, current.length - 1) + (current.length === 0 ? 0 : 1) + nextLine.length

  for (const line of lines) {
    if (line.length > maxLength) {
      if (current.length > 0) {
        chunkCount += 1
        current = []
      }
      chunkCount += Math.ceil(line.length / maxLength)
      continue
    }

    if (joinedLength(line) > maxLength) {
      chunkCount += 1
      current = current.slice(-2)
      while (current.length > 0 && joinedLength(line) > maxLength) {
        current.shift()
      }
    }
    current.push(line)
  }

  if (current.length > 0) chunkCount += 1
  return Math.max(1, chunkCount)
}
