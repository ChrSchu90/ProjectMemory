# Current ProjectMemory MCP interface (contract 1.0)

## Contents

- [Envelope and transport](#envelope-and-transport)
- [Six project-scoped memory tools](#six-project-scoped-memory-tools)
- [Nine administrative tools](#nine-administrative-tools)
- [Result payloads](#result-payloads)
- [Use restrictions](#use-restrictions)

## Envelope and transport

The current .NET service exposes **exactly 15 public MCP tools**: 6 memory tools and 9 administrative tools. It uses a versioned structured application envelope, serialized with snake_case fields, and a matching JSON text fallback:

```json
{
  "contract_version": "1.0",
  "ok": true,
  "data": { "...": "tool-specific fields" },
  "error": null
}
```

The object above illustrates the **shape**, not a recorded invocation. On failure `ok` is `false`, `data` is `null`, and `error` contains `code` and a safe `message`. The MCP `isError` flag is set for application failures. Check both the MCP result and `ok`; successful transport alone does not establish a successful operation. Prefer structured content, use the JSON text fallback only if necessary, and never fabricate missing fields.

Input objects are closed; do not send unknown fields, legacy parameters, or raw CLI flags. Tool discovery in a connected client is authoritative at runtime, and client availability is not established by static packaging.

## Six project-scoped memory tools

All six require the same exact, already bound `context_id` for the active project.

| Tool | Required inputs | Supported optional inputs | Purpose |
| --- | --- | --- | --- |
| `list_memories` | `context_id` | `folder=""`, `offset=0`, `limit=50` | List exact paths recursively in ordinal order. Page size 1-200. |
| `search_memories` | `context_id`, `query` | `folder=""`, `file`, `match`, `files`, `output` | Bounded lexical search with typed options. |
| `read_memory` | `context_id`, `path` | `start_line=1`, `line_count=1048577` | Read full text by default, or a 1-based line range; always hash the entire original file. |
| `create_memory` | `context_id`, `path`, `content` | None | Exclusively create an absent Markdown file. |
| `update_memory` | `context_id`, `path`, `content`, `expected_content_hash` | None | Replace the **entire** file using SHA-256 CAS. |
| `delete_memory` | `context_id`, `path`, `expected_content_hash` | None | Delete only the named file using SHA-256 CAS; requires user approval. |

`path` is an exact project-relative, forward-slash-separated `.md` path, not a UUID, title, permalink, host path, or absolute project root. File paths have at most 16 components and 1,024 UTF-8 bytes overall; individual components have additional restrictions. Markdown content is at most 1,048,576 UTF-8 bytes. The expected hash is the complete raw file's SHA-256 digest, exactly 64 hexadecimal digits; use the returned hash rather than re-encoding or hashing a trimmed excerpt.

For `list_memories`, handle the `has_more` flag by following pages as needed. A line-range `read_memory` returns the full-file hash but **not** the complete file content; do not attempt a full-file update using just a partial read.

## Nine administrative tools

| Tool | Required inputs | Supported optional inputs | Effect / approval |
| --- | --- | --- | --- |
| `list_projects` | None | `offset=0`, `limit=50` | Read-only active/archived project inventory. |
| `create_project` | `project_name` | None | Creates an active project with a server-issued UUID; explicit approval required. |
| `archive_project` | `project_id` | None | Archives a project, preserving files/bindings; explicit approval required. |
| `reactivate_project` | `project_id` | None | Reactivates an archived project; explicit approval required. |
| `delete_project` | `project_id` | None | **Single-call, permanently destructive** project deletion; specific explicit approval required. |
| `resolve_context` | `context_id` | None | Read-only exact binding/project resolution. |
| `list_context_bindings` | None | `project_id`, `offset=0`, `limit=50` | Read-only bindings inventory; optional filter by UUID. |
| `bind_context` | `context_id`, `project_id` | None | Explicitly binds/affirms one key to an active project; approval required. |
| `unbind_context` | `context_id` | None | Removes one exact binding, preserving its project; approval required. |

`project_id` is a server-issued immutable UUID in hyphenated form. `project_name` is a display name of 1-255 characters, not a host path. Paged administration tools accept `limit` 1-200 and nonnegative `offset`.

**Annotations from the current source:** `update_memory`, `delete_memory`, `unbind_context`, and `delete_project` are marked `Destructive = true`; `delete_project` has **no second server-side confirmation challenge** and the client may not show a dialog. Other project lifecycle and binding modifications are not marked destructive by the MCP tool but still require explicit user authorization under this skill's policy. Never treat an annotation as the only authorization rule.

## Result payloads

The code declares these exact snake_case success field groups:

- `list_memories`: `data.paths` (string array), `offset`, `limit`, `has_more`.
- `read_memory`: `data.path`, `content_hash`, `content`.
- `create_memory`, `update_memory`, `delete_memory`: `data.path`, `content_hash`.
- `search_memories`: `data.view`, `matches`, `files`, `counts`, `context`, `offset`, `limit`, `has_more`, `limited_by_request`. See [Search](search.md) for nested records.
- `list_projects`: `data.projects`, `offset`, `limit`, `has_more`. Each project has `project_id`, `project_name`, `status` (`active` or `archived`).
- `create_project`: `data.project`.
- `archive_project`, `reactivate_project`: `data.project`, `changed`.
- `delete_project`: `data.project_id`.
- `resolve_context`: `data.context_id`, `project_id`, `project_name`.
- `list_context_bindings`: `data.bindings`, `offset`, `limit`, `has_more`; each binding has `context_id`, `project_id`, `active`.
- `bind_context`: `data.context_id`, `project_id`, `changed`.
- `unbind_context`: `data.context_id`, `changed`.

These are static source-derived shapes, **not** evidence of client execution. The search output contains only the selected projection and uses project-relative paths; no server-side note title, frontmatter schema, graph metadata, or semantic score is available.

## Use restrictions

- Scope every memory operation with the **exact** active bound `context_id` determined by the calling client's documented selection rules; do not silently switch between contexts.
- `resolve_context` is diagnostic, not a substitute for trust in the current instructions or a permission grant.
- Do not provision a project or binding from a missing context without a separately authorized administrative request.
- Do not assume a client's separate native memory feature is disabled or confuse it with this MCP service.
- No legacy Basic Memory operations, section-edit or move tools, title selectors, legacy checksum inputs, unrestricted file paths, semantic retrieval, or raw ripgrep commands exist in this public interface.
