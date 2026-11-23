import {
  useMutation,
  useQuery,
  useQueryClient,
} from '@tanstack/react-query'
import type { QueryKey } from '@tanstack/react-query'
import {
  approveItem,
  addMeetingAttendee,
  createMeeting,
  createTickets,
  getActionItems,
  getAudit,
  getClock,
  getDashboard,
  getHealth,
  getLlmInfo,
  getMeeting,
  getMeetingItems,
  getMeetings,
  getNextAgenda,
  rejectItem,
  restoreItem,
  sendReply,
  simulateDays,
  undoApproval,
  updateItem,
} from './api'
import type { ActionItem, ActionReplyInput, CreateMeetingInput, UpdateActionItemInput } from './types'
import { queryKeys } from './queryKeys'

type StatusContext = { snapshots: Array<[QueryKey, ActionItem[] | undefined]> }

/** Loads one meeting's action items with request cancellation support. */
export function useMeetingItems(meetingId: string) {
  return useQuery({
    queryKey: queryKeys.items.meeting(meetingId),
    queryFn: ({ signal }) => getMeetingItems(meetingId, signal),
    enabled: Boolean(meetingId),
  })
}

/** Loads one meeting's metadata and extracted items. */
export function useMeetingDetails(meetingId: string) {
  return useQuery({
    queryKey: queryKeys.meetings.detail(meetingId),
    queryFn: ({ signal }) => getMeeting(meetingId, signal),
    enabled: Boolean(meetingId),
  })
}

/** Adds a meeting attendee and refreshes meeting metadata and its draft actions. */
export function useAddMeetingAttendee() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ meetingId, name }: { meetingId: string; name: string }) =>
      addMeetingAttendee(meetingId, name),
    onSuccess: async (_meeting, variables) => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: queryKeys.meetings.detail(variables.meetingId) }),
        queryClient.invalidateQueries({ queryKey: queryKeys.items.meeting(variables.meetingId) }),
        queryClient.invalidateQueries({ queryKey: queryKeys.meetings.all }),
        queryClient.invalidateQueries({ queryKey: queryKeys.items.all }),
      ])
    },
  })
}

/** Loads all meetings for dashboard series context. */
export function useMeetings() {
  return useQuery({
    queryKey: queryKeys.meetings.all,
    queryFn: ({ signal }) => getMeetings(signal),
  })
}

/** Loads open actions carried forward from earlier meetings in a selected series. */
export function useNextAgenda(meetingId: string) {
  return useQuery({
    queryKey: queryKeys.meetings.nextAgenda(meetingId),
    queryFn: ({ signal }) => getNextAgenda(meetingId, signal),
    enabled: Boolean(meetingId),
  })
}

/** Loads all action items used by the tracker. */
export function useActionItems() {
  return useQuery({
    queryKey: queryKeys.items.all,
    queryFn: ({ signal }) => getActionItems(signal),
  })
}

/** Loads dashboard metrics. */
export function useDashboard() {
  return useQuery({
    queryKey: queryKeys.dashboard,
    queryFn: ({ signal }) => getDashboard(signal),
  })
}

/** Polls API health without forcing unrelated query re-renders. */
export function useApiHealth() {
  return useQuery({
    queryKey: queryKeys.health,
    queryFn: ({ signal }) => getHealth(signal),
    refetchInterval: 15_000,
    staleTime: 15_000,
    retry: 1,
  })
}

/** Loads the selected transcript-analysis engine and its availability details. */
export function useLlmInfo() {
  return useQuery({
    queryKey: queryKeys.llmInfo,
    queryFn: ({ signal }) => getLlmInfo(signal),
    staleTime: 30_000,
    retry: 1,
  })
}

/** Polls the simulated date at the same cadence as the header health status. */
export function useVirtualClock() {
  return useQuery({
    queryKey: queryKeys.clock,
    queryFn: ({ signal }) => getClock(signal),
    refetchInterval: 15_000,
    staleTime: 15_000,
  })
}

/** Loads an action item's audit trail. */
export function useAudit(itemId: string, enabled = true) {
  return useQuery({
    queryKey: queryKeys.items.audit(itemId),
    queryFn: ({ signal }) => getAudit(itemId, signal),
    enabled: enabled && Boolean(itemId),
  })
}

/** Creates a meeting and refreshes the meeting list after success. */
export function useCreateMeeting() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (input: CreateMeetingInput) => createMeeting(input),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.meetings.all }),
  })
}

/** Optimistically changes one action status and restores cache snapshots on failure. */
function useStatusMutation(mutationFn: (id: string) => Promise<ActionItem>, status: ActionItem['status']) {
  const queryClient = useQueryClient()
  return useMutation<ActionItem, Error, string, StatusContext>({
    mutationFn,
    onMutate: async (id) => {
      await Promise.all([
        queryClient.cancelQueries({ queryKey: queryKeys.items.all }),
        queryClient.cancelQueries({ queryKey: queryKeys.items.meetingRoot }),
      ])
      const snapshots = [
        ...queryClient.getQueriesData<ActionItem[]>({ queryKey: queryKeys.items.all }),
        ...queryClient.getQueriesData<ActionItem[]>({ queryKey: queryKeys.items.meetingRoot }),
      ]
      for (const [key, items] of snapshots) {
        if (items) {
          queryClient.setQueryData<ActionItem[]>(key, items.map((item) => item.id === id ? { ...item, status } : item))
        }
      }
      return { snapshots }
    },
    onError: (_error, _id, context) => {
      context?.snapshots.forEach(([key, data]) => queryClient.setQueryData(key, data))
    },
    onSettled: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: queryKeys.items.all }),
        queryClient.invalidateQueries({ queryKey: queryKeys.items.meetingRoot }),
        queryClient.invalidateQueries({ queryKey: queryKeys.meetings.all }),
        queryClient.invalidateQueries({ queryKey: queryKeys.dashboard }),
      ])
    },
  })
}

/** Approves an action item and updates cached tracker and review rows. */
export function useApproveItem() {
  return useStatusMutation(approveItem, 'Approved')
}

/** Reverses approval and updates cached tracker and review rows. */
export function useUnapproveItem() {
  return useStatusMutation(undoApproval, 'Draft')
}

/** Rejects an action item and updates cached tracker and review rows. */
export function useRejectItem() {
  return useStatusMutation(rejectItem, 'Rejected')
}

/** Restores a rejected item to draft and refreshes the organizer and tracker views. */
export function useRestoreRejectedItem() {
  return useStatusMutation(restoreItem, 'Draft')
}

/** Optimistically updates editable action fields and rolls back on API failure. */
export function useUpdateItem() {
  const queryClient = useQueryClient()
  return useMutation<ActionItem, Error, { id: string; fields: UpdateActionItemInput }, StatusContext>({
    mutationFn: ({ id, fields }) => updateItem(id, fields),
    onMutate: async ({ id, fields }) => {
      await Promise.all([
        queryClient.cancelQueries({ queryKey: queryKeys.items.all }),
        queryClient.cancelQueries({ queryKey: queryKeys.items.meetingRoot }),
      ])
      const snapshots = [
        ...queryClient.getQueriesData<ActionItem[]>({ queryKey: queryKeys.items.all }),
        ...queryClient.getQueriesData<ActionItem[]>({ queryKey: queryKeys.items.meetingRoot }),
      ]
      for (const [key, items] of snapshots) {
        if (items) {
          queryClient.setQueryData<ActionItem[]>(key, items.map((item) => item.id === id ? {
            ...item,
            ...fields,
            needsInput: item.needsInput.filter((entry) => {
              const normalized = entry.toLowerCase()
              if (normalized === 'owner') return !fields.ownerName
              if (normalized === 'due date') return !fields.dueDate
              return true
            }),
          } : item))
        }
      }
      return { snapshots }
    },
    onError: (_error, _variables, context) => {
      context?.snapshots.forEach(([key, data]) => queryClient.setQueryData(key, data))
    },
    onSettled: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: queryKeys.items.all }),
        queryClient.invalidateQueries({ queryKey: queryKeys.items.meetingRoot }),
        queryClient.invalidateQueries({ queryKey: queryKeys.meetings.all }),
        queryClient.invalidateQueries({ queryKey: queryKeys.dashboard }),
      ])
    },
  })
}

/** Creates tickets for approved items and refreshes review and tracker data. */
export function useCreateTickets() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (meetingId: string) => createTickets(meetingId),
    onSettled: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: queryKeys.items.all }),
        queryClient.invalidateQueries({ queryKey: queryKeys.items.meetingRoot }),
        queryClient.invalidateQueries({ queryKey: queryKeys.meetings.all }),
        queryClient.invalidateQueries({ queryKey: queryKeys.dashboard }),
      ])
    },
  })
}

/** Applies an owner reply and refreshes the item and dashboard queries. */
export function useSendReply() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ id, input }: { id: string; input: ActionReplyInput }) => sendReply(id, input),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: queryKeys.items.all }),
        queryClient.invalidateQueries({ queryKey: queryKeys.items.meetingRoot }),
        queryClient.invalidateQueries({ queryKey: queryKeys.dashboard }),
      ])
    },
  })
}

/** Advances virtual time and invalidates every server-backed view after success. */
export function useSimulateDays() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (days: number) => simulateDays(days),
    onSuccess: () => queryClient.invalidateQueries(),
  })
}
