// OverShell integration v1 — written by `OverShell integrations install opencode`.
// Safe to delete. Reports this OpenCode session's state to the OverShell tab that launched
// it, over loopback, so the tab strip, sidebar and notifications know exactly when the
// agent is working, waiting for input, blocked on a permission or question, or failed - and,
// since v2, carries OverShell's answers back: a permission approved or denied from the inbox or
// a toast, a question answered, a prompt sent, all through OpenCode's own API rather than typed
// into the dialog. Outside OverShell the environment variables are absent and the plugin does nothing.
import type { Plugin } from "@opencode-ai/plugin"

const endpoint = process.env.OVERSHELL_ENDPOINT
const token = process.env.OVERSHELL_TOKEN
const tab = process.env.OVERSHELL_TAB_ID

export const OverShellPlugin: Plugin = async ({ directory, client }) => {
  if (!endpoint || !token || !tab) return {}

  const children = new Set<string>()
  let seq = 0
  let sessionId: string | undefined
  let title: string | undefined
  let failures = 0

  const report = (state: string, extra: Record<string, unknown> = {}) => {
    if (failures >= 5) return
    const body = {
      tab,
      source: "opencode",
      harness: "opencode",
      seq: ++seq,
      state,
      summary: title,
      session: sessionId ? { id: sessionId, resumeCommand: `opencode --session ${sessionId}` } : undefined,
      cwd: directory,
      ...extra,
    }
    fetch(`${endpoint}/v1/report`, {
      method: "POST",
      headers: { "content-type": "application/json", authorization: `Bearer ${token}` },
      body: JSON.stringify(body),
      signal: AbortSignal.timeout(2000),
    })
      .then(() => { failures = 0 })
      .catch(() => { failures++ })
  }

  // Sub-agent sessions are noise for the tab's state.
  const foreign = (props: { sessionID?: string }) => Boolean(props.sessionID && children.has(props.sessionID))

  // A resumed session (`opencode --continue`, `--session <id>` - what OverShell types after a
  // restart) never fires session.created, so the id is taken from the first event that names
  // a session we did not see born as a child. Without this a restored tab could never be
  // resumed by id again.
  const adopt = (id: string | undefined): boolean => {
    if (!id || sessionId || children.has(id)) return false
    sessionId = id
    return true
  }

  // ---- the way back: OverShell's commands for this session (v2) ----
  // The request ids of the permission / question currently open, so a reply from OverShell
  // can name one even when the command queue only says "approve".
  let openPermission: { id: string; sessionID: string } | undefined
  let openQuestion: { id: string; sessionID: string } | undefined

  const anyClient = client as unknown as Record<string, any>

  const replyPermission = async (requestID: string, sessionID: string, response: string) => {
    // The typed helper exists from v1.17 (POST /permission/{requestID}/reply); older servers
    // have the per-session route. Try the new one, fall back to the old.
    try {
      if (anyClient.permission?.reply) {
        await anyClient.permission.reply({ requestID, reply: response, directory })
        return true
      }
    } catch {
      // fall through
    }
    try {
      await anyClient.postSessionIdPermissionsPermissionId({ path: { id: sessionID, permissionID: requestID }, body: { response } })
      return true
    } catch {
      return false
    }
  }

  const replyQuestion = async (requestID: string, answers: string[]) => {
    try {
      if (anyClient.question?.reply) {
        await anyClient.question.reply({ requestID, answers, directory })
        return true
      }
    } catch {
      // fall through
    }
    return false
  }

  const sendPrompt = async (text: string) => {
    // Through the TUI's composer, so it shows up as if typed; a headless run has no TUI and
    // takes the session prompt instead.
    try {
      await anyClient.tui.appendPrompt({ body: { text } })
      await anyClient.tui.submitPrompt()
      return true
    } catch {
      // fall through
    }
    try {
      if (sessionId) {
        await anyClient.session.promptAsync({ path: { id: sessionId }, body: { parts: [{ type: "text", text }] } })
        return true
      }
    } catch {
      // fall through
    }
    return false
  }

  const execute = async (command: { kind: string; requestId?: string | null; response: string }) => {
    switch (command.kind) {
      case "permission": {
        const target = command.requestId
          ? { id: command.requestId, sessionID: openPermission?.sessionID ?? sessionId ?? "" }
          : openPermission
        if (!target) return
        const response = command.response === "approve" ? "once" : command.response === "deny" ? "reject" : command.response
        if (await replyPermission(target.id, target.sessionID, response)) {
          if (openPermission?.id === target.id) openPermission = undefined
        }
        return
      }
      case "question": {
        const target = command.requestId ? { id: command.requestId } : openQuestion
        if (!target) return
        if (await replyQuestion(target.id, [command.response])) {
          if (openQuestion?.id === target.id) openQuestion = undefined
        }
        return
      }
      case "prompt":
        await sendPrompt(command.response)
        return
    }
  }

  // A long poll per tab: held up to 25 s by OverShell, answered at once when something is
  // queued. Failures back off; the loop ends when the tab is gone.
  let after = 0
  let polling = true
  const poll = async () => {
    let backoff = 1000
    while (polling) {
      try {
        const res = await fetch(`${endpoint}/v1/tabs/${encodeURIComponent(tab)}/commands?after=${after}`, {
          headers: { authorization: `Bearer ${token}` },
          signal: AbortSignal.timeout(35000),
        })
        if (res.status === 401 || res.status === 404) return
        if (!res.ok) throw new Error(String(res.status))
        const data = (await res.json()) as { commands?: Array<{ seq: number; kind: string; requestId?: string | null; response: string }> }
        for (const command of data.commands ?? []) {
          after = Math.max(after, command.seq)
          await execute(command)
        }
        backoff = 1000
      } catch {
        await new Promise((r) => setTimeout(r, backoff))
        backoff = Math.min(backoff * 2, 30000)
      }
    }
  }
  void poll()

  return {
    event: async ({ event }) => {      const type = event.type as string
      const props = (event.properties ?? {}) as Record<string, unknown>

      switch (type) {
        case "session.created": {
          const info = props.info as { id: string; parentID?: string } | undefined
          if (!info) return
          if (info.parentID) {
            children.add(info.id)
            return
          }
          sessionId = info.id
          report("idle", { message: "session started" })
          return
        }
        case "session.deleted": {
          const id = (props.sessionID as string | undefined) ?? (props.info as { id?: string } | undefined)?.id
          if (id) children.delete(id)
          return
        }
        case "session.updated": {
          const info = props.info as { id: string; parentID?: string; title?: string } | undefined
          if (!info || info.parentID || (sessionId && info.id !== sessionId)) return
          if (info.title && !/^(New session|Child session) - /.test(info.title)) title = info.title
          if (adopt(info.id)) report("idle", { message: "session resumed" })
          return
        }
        case "session.status": {
          if (foreign(props as { sessionID?: string })) return
          adopt(props.sessionID as string | undefined)
          const status = props.status as { type?: string } | undefined
          if (status?.type === "busy") report("working")
          else if (status?.type === "idle") report("idle", { message: "turn finished" })
          return
        }
        case "session.idle":
        case "session.compacted":
          if (foreign(props as { sessionID?: string })) return
          adopt(props.sessionID as string | undefined)
          report("idle", { message: "turn finished" })
          return
        case "session.error":
          if (foreign(props as { sessionID?: string })) return
          report("error", { message: "Session encountered an error" })
          return
        case "permission.asked":
        case "permission.updated": {
          if (foreign(props as { sessionID?: string })) return
          const id = (props.id as string | undefined) ?? (props.requestID as string | undefined)
          if (id) openPermission = { id, sessionID: (props.sessionID as string | undefined) ?? sessionId ?? "" }
          report("blocked", { message: (props.title as string | undefined) ?? "Action requires your approval", requestId: id, requestKind: "permission" })
          return
        }
        case "permission.replied":
          if (foreign(props as { sessionID?: string })) return
          openPermission = undefined
          report("working", { message: "permission answered" })
          return
        case "question.asked": {
          if (foreign(props as { sessionID?: string })) return
          const id = (props.id as string | undefined) ?? (props.requestID as string | undefined)
          if (id) openQuestion = { id, sessionID: (props.sessionID as string | undefined) ?? sessionId ?? "" }
          report("blocked", { message: "OpenCode is asking a question", requestId: id, requestKind: "question" })
          return
        }
        case "question.replied":
        case "question.rejected":
          openQuestion = undefined
          report("working", { message: "question answered" })
          return      }
    },
  }
}
