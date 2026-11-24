// PreToolUse hook: the saga state machine (src/TripBooking.Saga/Domain) decides, it never does I/O or reads the
// clock. That is what makes every decision unit-testable and every restart deterministic. I/O belongs to the worker.
import { readFileSync } from 'node:fs'

const input = JSON.parse(readFileSync(0, 'utf8'))
const filePath = (input.tool_input?.file_path ?? '').replaceAll('\\', '/')
if (!/\/src\/TripBooking\.Saga\/Domain\/.*\.cs$/.test(filePath)) process.exit(0)

const written = input.tool_input?.new_string ?? input.tool_input?.content ?? ''
const forbidden = [
  [/\bHttpClient\b|\bIParticipantClient\b|\bawait\b|\basync\b/, 'I/O (HTTP calls or anything awaited)'],
  [/\bDapper\b|\bSqlite\w*\b|\bSagaStore\b/, 'database access'],
  [/\bDateTime(Offset)?\.(Utc)?Now\b|\bTimeProvider\b/, 'reading the clock (pass "now" in)'],
  [/\bTask\.Delay\b|\bThread\.Sleep\b/, 'waiting'],
]
const hit = forbidden.find(([pattern]) => pattern.test(written))
if (hit) {
  console.error(`Blocked: ${filePath} would add ${hit[1]} to the saga state machine. Keep Domain pure; do I/O in SagaWorker.`)
  process.exit(2)
}
