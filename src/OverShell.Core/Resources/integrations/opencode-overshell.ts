// OverShell integration v1 — written by `OverShell integrations install opencode`.
// Safe to delete. Reports this OpenCode session's state to the OverShell tab that launched
// it, over loopback, so the tab strip, sidebar and notifications know exactly when the
// agent is working, waiting for input, blocked on a permission or question, or failed.
// Outside OverShell the environment variables are absent and the plugin does nothing.
import type { Plugin } from "@opencode-ai/plugin"

const endpoint = process.env.OVERSHELL_ENDPOINT
const token = process.env.OVERSHELL_TOKEN
const tab = process.env.OVERSHELL_TAB_ID

export const OverShellPlugin: Plugin = async ({ directory }) => {
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

  return {
    event: async ({ event }) => {
      const type = event.type as string
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
          return
        }
        case "session.status": {
          if (foreign(props as { sessionID?: string })) return
          const status = props.status as { type?: string } | undefined
          if (status?.type === "busy") report("working")
          else if (status?.type === "idle") report("idle", { message: "turn finished" })
          return
        }
        case "session.idle":
        case "session.compacted":
          if (foreign(props as { sessionID?: string })) return
          report("idle", { message: "turn finished" })
          return
        case "session.error":
          if (foreign(props as { sessionID?: string })) return
          report("error", { message: "Session encountered an error" })
          return
        case "permission.asked":
          if (foreign(props as { sessionID?: string })) return
          report("blocked", { message: "Action requires your approval" })
          return
        case "permission.replied":
          if (foreign(props as { sessionID?: string })) return
          report("working", { message: "permission answered" })
          return
        case "question.asked":
          if (foreign(props as { sessionID?: string })) return
          report("blocked", { message: "OpenCode is asking a question" })
          return
        case "question.replied":
        case "question.rejected":
          report("working", { message: "question answered" })
          return
      }
    },
  }
}
