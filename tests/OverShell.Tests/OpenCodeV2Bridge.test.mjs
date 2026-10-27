import assert from "node:assert/strict"
import { readFile } from "node:fs/promises"
import { stripTypeScriptTypes } from "node:module"
import { test } from "node:test"
import vm from "node:vm"

const source = await readFile(new URL("../../src/OverShell.Core/Resources/integrations/opencode-v2-tui.ts", import.meta.url), "utf8")
const javascript = stripTypeScriptTypes(source)
const settle = () => new Promise((resolve) => setImmediate(resolve))
const plain = (value) => JSON.parse(JSON.stringify(value))

async function bridge(tab = "tab-a", enabled = true, rootPermissions = false) {
  let clock = 1800000000000
  let route = { type: "session", sessionID: "a" }
  const sessions = new Map([
    ["a", { id: "a", title: "Task A", location: { directory: "C:\\same-project" } }],
    ["b", { id: "b", title: "Task B", location: { directory: "C:\\same-project" } }],
  ])
  const statuses = new Map([["a", "running"], ["b", "idle"]])
  const permissions = new Map()
  const forms = new Map()
  const families = new Map()
  const reports = []
  const calls = []
  const toasts = []
  const intervals = new Set()
  const waiting = []
  let responseStatus = 200
  let readFailure = false
  const location = { directory: "C:\\same-project" }
  const context = {
    location,
    data: {
      location: { default: () => location },
      session: {
        get: (id) => sessions.get(id),
        family: (id) => families.get(id) ?? [id],
        status: (id) => statuses.get(id) ?? "idle",
        permission: { list: (id) => permissions.get(id) ?? [] },
        form: {
          list: (id) => forms.get(id) ?? [],
          reply: async (input, where) => { calls.push({ kind: "form", input: plain(input), location: plain(where) }) },
        },
      },
    },
    ui: {
      router: { current: () => { if (readFailure) { readFailure = false; throw new Error("cache unavailable") }; return route }, navigate: (next) => { route = next } },
      toast: { show: (toast) => toasts.push(plain(toast)) },
    },
    client: {
      session: {
        permission: { reply: async (input) => { calls.push({ kind: "permission", input: plain(input) }); return {} } },
        create: async (input) => { calls.push({ kind: "create", input: plain(input) }); return { id: "created" } },
        prompt: async (input) => { calls.push({ kind: "prompt", input: plain(input) }); return {} },
      },
    },
  }
  if (rootPermissions) {
    context.client.permission = context.client.session.permission
    delete context.client.session.permission
  }
  const runtime = vm.createContext({
    process: { env: enabled ? { OVERSHELL_ENDPOINT: "http://127.0.0.1:1234", OVERSHELL_TOKEN: "test-token", OVERSHELL_TAB_ID: tab } : {} },
    Date: class extends Date { static now() { return clock } },
    AbortController, AbortSignal, console,
    setInterval: (callback) => { intervals.add(callback); return callback },
    clearInterval: (callback) => intervals.delete(callback),
    setTimeout, clearTimeout,
    fetch: async (url, options) => {
      if (url.endsWith("/v1/report")) {
        reports.push(JSON.parse(options.body))
        const status = responseStatus
        responseStatus = 200
        return { ok: status === 200, status }
      }
      assert.match(url, new RegExp(`/v1/tabs/${tab}/commands\\?after=`))
      return new Promise((resolve, reject) => {
        const abort = () => { const index = waiting.indexOf(deliver); if (index >= 0) waiting.splice(index, 1); reject(new Error("aborted")) }
        const deliver = (commands) => { options.signal.removeEventListener("abort", abort); resolve({ ok: true, status: 200, json: async () => ({ commands }) }) }
        waiting.push(deliver)
        options.signal.addEventListener("abort", abort, { once: true })
      })
    },
  })
  const module = new vm.SourceTextModule(javascript, { context: runtime })
  await module.link((specifier) => {
    assert.equal(specifier, "@opencode/plugin/tui")
    return new vm.SyntheticModule(["Plugin"], function () { this.setExport("Plugin", { define: (definition) => definition }) }, { context: runtime })
  })
  await module.evaluate()
  const cleanup = module.namespace.default.setup(context)
  await settle()
  return {
    sessions, statuses, permissions, forms, families, reports, calls, toasts, intervals,
    route: (next) => { route = next },
    failNext: () => { responseStatus = 500 },
    failRead: () => { readFailure = true },
    advance: (milliseconds) => { clock += milliseconds },
    tick: async () => { for (const callback of [...intervals]) callback(); await settle() },
    command: async (command) => { assert.ok(waiting.length); waiting.shift()([command]); await settle() },
    dispose: async () => { await cleanup?.(); await settle() },
  }
}

test("does nothing outside OverShell", async () => {
  const client = await bridge("outside", false)
  assert.equal(client.intervals.size, 0)
  assert.equal(client.reports.length, 0)
  await client.dispose()
})

test("two clients in one repository report their own selected sessions", async () => {
  const one = await bridge("tab-a")
  const two = await bridge("tab-b")
  try {
    two.route({ type: "session", sessionID: "b" })
    await two.tick()
    assert.equal(one.reports.at(-1).tab, "tab-a")
    assert.equal(one.reports.at(-1).session.id, "a")
    assert.equal(two.reports.at(-1).tab, "tab-b")
    assert.equal(two.reports.at(-1).session.id, "b")
    assert.equal(one.reports.at(-1).session.title, "Task A")
    assert.equal(two.reports.at(-1).session.title, "Task B")
  } finally { await one.dispose(); await two.dispose() }
})

test("renames, existing-session navigation and home update without another agent turn", async () => {
  const client = await bridge()
  try {
    client.sessions.get("a").title = "Renamed while working"
    await client.tick()
    assert.equal(client.reports.at(-1).session.title, "Renamed while working")
    assert.equal(client.reports.at(-1).metadataOnly, true)
    client.route({ type: "session", sessionID: "b" })
    await client.tick()
    assert.equal(client.reports.at(-1).session.title, "Task B")
    assert.equal(client.reports.at(-1).metadataOnly, false)
    const count = client.reports.length
    client.sessions.get("a").title = "Unrelated background rename"
    await client.tick()
    assert.equal(client.reports.length, count)
    client.route({ type: "home" })
    await client.tick()
    assert.deepEqual(client.reports.at(-1).session, { id: null, title: null })
    assert.equal(client.reports.at(-1).state, "idle")
  } finally { await client.dispose() }
})

test("failed reports retry and cleanup stops timers and outstanding polls", async () => {
  const client = await bridge()
  client.sessions.get("a").title = "Retry me"
  client.failNext()
  await client.tick()
  const count = client.reports.length
  await client.tick()
  assert.equal(client.reports.length, count)
  client.advance(1000)
  await client.tick()
  assert.equal(client.reports.length, count + 1)
  assert.equal(client.reports.at(-1).session.title, "Retry me")
  await client.dispose()
  assert.equal(client.intervals.size, 0)
  assert.equal(client.reports.at(-1).state, "exited")
  await client.tick()
  assert.equal(client.reports.length, count + 2)
})

test("a transient cache error cannot escape the reporting loop", async () => {
  const client = await bridge()
  try {
    client.sessions.get("a").title = "After cache recovery"
    client.failRead()
    await client.tick()
    client.advance(1000)
    await client.tick()
    assert.equal(client.reports.at(-1).session.title, "After cache recovery")
  } finally { await client.dispose() }
})

test("the installed V2.0 permission namespace is supported", async () => {
  const client = await bridge("tab-a", true, true)
  try {
    client.permissions.set("a", [{ id: "own", sessionID: "a", action: "edit" }])
    await client.command({ seq: 1, kind: "permission", requestId: "own", response: "deny" })
    assert.deepEqual(client.calls.at(-1), { kind: "permission", input: { sessionID: "a", requestID: "own", decision: "reject" } })
  } finally { await client.dispose() }
})

test("permission replies are scoped to this client's session family", async () => {
  const client = await bridge()
  try {
    client.families.set("a", ["a", "child"])
    client.permissions.set("child", [{ id: "own", sessionID: "child", action: "edit" }])
    client.permissions.set("b", [{ id: "foreign", sessionID: "b", action: "edit" }])
    await client.tick()
    assert.equal(client.reports.at(-1).state, "blocked")
    await client.command({ seq: 1, kind: "permission", requestId: "foreign", response: "approve" })
    assert.equal(client.calls.length, 0)
    assert.equal(client.toasts.length, 1)
    await client.command({ seq: 2, kind: "permission", requestId: "own", response: "approve" })
    assert.deepEqual(client.calls.at(-1), { kind: "permission", input: { sessionID: "child", requestID: "own", decision: "once" } })
  } finally { await client.dispose() }
})

test("question replies use V2 field keys and reject an empty numeric answer", async () => {
  const client = await bridge()
  try {
    client.forms.set("a", [{ id: "form", sessionID: "a", title: "Name?", fields: [{ key: "name", type: "string" }] }])
    await client.command({ seq: 1, kind: "question", requestId: "form", response: "Moaid" })
    assert.deepEqual(client.calls.at(-1).input, { sessionID: "a", formID: "form", answer: { name: "Moaid" } })
    client.forms.get("a")[0].fields = [{ key: "count", type: "integer" }]
    await client.command({ seq: 2, kind: "question", requestId: "form", response: "" })
    assert.equal(client.calls.length, 1)
    assert.equal(client.toasts.length, 1)
  } finally { await client.dispose() }
})

test("a prompt from home creates and selects a session in this client", async () => {
  const client = await bridge()
  try {
    client.route({ type: "home" })
    await client.command({ seq: 1, kind: "prompt", response: "First prompt" })
    assert.deepEqual(client.calls.map((call) => call.kind), ["create", "prompt"])
    assert.deepEqual(client.calls.at(-1).input, { sessionID: "created", text: "First prompt" })
    await client.tick()
    assert.equal(client.reports.at(-1).session.id, "created")
  } finally { await client.dispose() }
})

test("visible form fields accept option labels and comma-separated or JSON choices", async () => {
  const client = await bridge()
  try {
    const options = [{ label: "First choice", value: "first" }, { label: "Second choice", value: "second" }, { label: "Choice, with comma", value: "comma" }]
    const field = { key: "choice", type: "string", options }
    client.forms.set("a", [{ id: "form", sessionID: "a", fields: [{ key: "internal", type: "string", hidden: true }, field] }])
    await client.command({ seq: 1, kind: "question", requestId: "form", response: "First choice" })
    assert.deepEqual(client.calls.at(-1).input.answer, { choice: "first" })
    field.type = "multiselect"
    await client.command({ seq: 2, kind: "question", requestId: "form", response: "First choice, Second choice" })
    assert.deepEqual(client.calls.at(-1).input.answer, { choice: ["first", "second"] })
    await client.command({ seq: 3, kind: "question", requestId: "form", response: '["Choice, with comma", "Second choice"]' })
    assert.deepEqual(client.calls.at(-1).input.answer, { choice: ["comma", "second"] })
    await client.command({ seq: 4, kind: "question", requestId: "form", response: "Choice, with comma" })
    assert.deepEqual(client.calls.at(-1).input.answer, { choice: ["comma"] })
    assert.equal(client.toasts.length, 0)
  } finally { await client.dispose() }
})

test("short boolean answers are supported and unknown answers are rejected", async () => {
  const client = await bridge()
  try {
    client.forms.set("a", [{ id: "form", sessionID: "a", fields: [{ key: "enabled", type: "boolean" }] }])
    let sequence = 0
    for (const [response, expected] of [["y", true], [" N ", false], ["1", true], ["0", false]]) {
      await client.command({ seq: ++sequence, kind: "question", requestId: "form", response })
      assert.deepEqual(client.calls.at(-1).input.answer, { enabled: expected })
    }
    await client.command({ seq: ++sequence, kind: "question", requestId: "form", response: "maybe" })
    assert.equal(client.calls.length, 4)
    assert.equal(client.toasts.length, 1)
  } finally { await client.dispose() }
})
