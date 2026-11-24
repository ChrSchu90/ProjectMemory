# Errors and recovery

## Read the application result

A tool can be delivered over MCP successfully while the **application** operation fails. Check the MCP `isError` flag and the structured or JSON-text envelope:

```json
{
  "contract_version": "1.0",
  "ok": false,
  "data": null,
  "error": {
    "code": "CONTEXT_UNBOUND",
    "message": "The exact context has no active binding."
  }
}
```

This is an **illustrative documented error shape**, not a recorded invocation. Do not treat `data=null` as a successful empty result or invent additional tool fields.

## Fixed application codes from the current source

| Error code | Meaning / safe response |
| --- | --- |
| `INVALID_CONTEXT` | Missing, malformed, or unsupported exact context; do not repair or guess an alternative. |
| `CONTEXT_UNBOUND` | No active binding for the supplied key; no implicit provisioning or fallback. |
| `PROJECT_NOT_FOUND` | Target project does not exist; ask an authorized operator to investigate. |
| `PROJECT_ARCHIVED` | Ordinary memory operations disabled; reactivation requires approval. |
| `PROJECT_BLOCKED` | Project deletion is pending; stop ordinary operations. |
| `RECOVERY_REQUIRED` | Deletion recovery remains blocked; refer to operator. |
| `INVALID_ARGUMENT` | Invalid or incompatible tool parameters; correct against the documented schema. |
| `ALREADY_EXISTS` | Creation/name collision; read and reconcile instead of overwrite. |
| `CONFLICT` | Current file hash or state differs; read current state and reconcile before safe retry. |
| `INVALID_PATH` | Invalid exact project-relative Markdown path; correct deliberately. |
| `UNSAFE_PATH` | Unsafe storage entry or path; stop and escalate, do not bypass validation. |
| `MEMORY_NOT_FOUND` | Exact note absent; check scope/name with user, not another project. |
| `CONTENT_TOO_LARGE` | Content over the 1 MiB UTF-8 limit; split coherently when appropriate. |
| `INVALID_ENCODING` | Markdown must be valid UTF-8 text; investigate content. |
| `STORAGE_UNAVAILABLE` | Server/storage problem; report without exposing infrastructure details. |
| `INVALID_PATTERN` | Search pattern or glob invalid; simplify/correct it. |
| `SCAN_LIMIT` | Scan budget reached; narrow folder/globs/depth. |
| `SEARCH_TIMEOUT` | Search deadline exceeded; narrow scope before retry. |
| `SEARCH_UNAVAILABLE` | Required ripgrep executable unavailable; notify operator. |
| `SEARCH_IO` | Search process communication error; not an empty result. |
| `SEARCH_PROTOCOL` | Search returned invalid protocol data; not an empty result. |
| `SEARCH_FAILED` | Search process failure; do not claim a completed search. |

Other failures may be transport, cancellation, client approval, authentication, or connectivity errors; do not reinterpret them as data absence. No such error grants permission to switch to another context or memory provider.

## Safe recovery patterns

**Hash conflict:** Reread the exact entire note in the same context. Compare its updated content with the intended revision, merge carefully, obtain a new `content_hash`, and retry only if the combined change is justified. Incompatible decisions or uncertain authority require user input.

**Ambiguous mutation outcome:** A timeout or disconnected response is not proof that a create/update/delete failed. Inspect the exact target via `read_memory` or appropriate inventory when possible and verify the current state. Do not blindly repeat a mutation or claim it succeeded.

**Unbound/archived context:** Stop memory operations in that context and continue the user's main task without memory if practical. Administrative read-only diagnostics (`resolve_context`, `list_projects`, `list_context_bindings`) may help an operator, but never authorize you to select a different project.

**Search limit or failure:** Narrow the search and retry under the same context, or describe the limitation. `has_more` indicates paginated results; `limited_by_request` indicates a requested per-file hit cap. Neither a partial result nor a failed scan is exhaustive.

**Client approval prompts:** Respect them. The `update_memory` tool is annotated destructive; approval may be required even for ordinary CAS updates. The service's `delete_project` is a single destructive call with no built-in confirmation step, and client dialogs are not guaranteed.
