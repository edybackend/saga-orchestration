// PreToolUse hook: the HTTP contract between the orchestrator and the participants is shared by separately
// deployed services. Removing or renaming a field breaks the other side, so it needs a new version instead.
import { readFileSync } from 'node:fs'

const input = JSON.parse(readFileSync(0, 'utf8'))
const filePath = (input.tool_input?.file_path ?? '').replaceAll('\\', '/')
if (!filePath.endsWith('src/TripBooking.Contracts/ParticipantContracts.cs')) process.exit(0)

const before = (() => {
  try {
    return readFileSync(filePath, 'utf8')
  } catch {
    return ''
  }
})()
const oldText = input.tool_input?.old_string
const after = oldText !== undefined ? before.replace(oldText, input.tool_input?.new_string ?? '') : (input.tool_input?.content ?? '')

// "record Name(field list)" → "Name.Field"; enum members → "enum Name.Member"
const members = (source) =>
  new Set([
    ...[...source.matchAll(/record\s+(\w+)\s*\(([^)]*)\)/g)].flatMap(([, record, args]) =>
      args.split(',').map((arg) => `${record}.${arg.trim().split(/\s+/)[1] ?? arg.trim()}`),
    ),
    ...[...source.matchAll(/enum\s+(\w+)\s*\{([^}]*)\}/g)].flatMap(([, name, body]) =>
      body.split(',').map((m) => m.trim()).filter(Boolean).map((m) => `enum ${name}.${m}`),
    ),
  ])

const removed = [...members(before)].filter((member) => !members(after).has(member))
if (removed.length > 0) {
  console.error(
    `Blocked: this edit removes or renames members of the participant contract (${removed.join(', ')}). ` +
      'The other service would break. Add an optional field instead, or ask the developer.',
  )
  process.exit(2)
}
