# Markdown note operations and safe updates

## Contents

- [Storage model](#storage-model)
- [Create without duplicates](#create-without-duplicates)
- [Full-file CAS update](#full-file-cas-update)
- [Deletion and uncertain outcomes](#deletion-and-uncertain-outcomes)
- [Durable documentation](#durable-documentation)

## Storage model

Every memory is a plain UTF-8 `.md` file addressed by its **exact project-relative path** within a selected server-owned project UUID root. Valid file paths are slash-separated, at most 16 components and 1,024 UTF-8 bytes in total; no component may be empty or start/end with a dot or space, and components use permitted letters, numbers, spaces, dots, hyphens, and underscores. Each component is at most 255 UTF-8 bytes. Do not supply absolute paths, `..`, a host storage root, titles, note UUIDs, or permalinks as selectors.

No mandatory frontmatter, structured observation schema, database note index, graph, note lifecycle state, or server-side note ranking exists. User documentation conventions take priority; see [Note conventions](note-conventions.md) for optional defaults.

## Create without duplicates

1. Work only in the correctly bound context selected according to the active client's identity rules.
2. Identify durable content worth retaining; do not store the chat transcript, transient guesses, redundant copies of external source documents, or secrets.
3. Search/list for existing notes on the subject. Read an appropriate existing note and **update** it instead of creating a near duplicate.
4. If no suitable note exists, choose a descriptive topic-based relative `.md` path that fits the user's conventions and call `create_memory(context_id, path, content)` with complete English Markdown, at most 1,048,576 UTF-8 bytes.
5. Check the application envelope for success. `ALREADY_EXISTS` means the name now exists; inspect it and decide whether to incorporate the new finding, not overwrite blindly.

## Full-file CAS update

`update_memory` replaces **all** file bytes. The agent must preserve any unrelated valid content that should remain; there is no patch, section edit, move, or merge tool.

1. Use `read_memory(context_id, path)` for the **complete** current content and `content_hash`. A partial line-range read returns the full-file hash but not enough content for a safe full-file replacement.
2. Prepare the full, coherent replacement in English, preserving unrelated content, exact technical literals, evidence, and any important supersession rationale. Avoid silent changes to confirmed project decisions.
3. Call `update_memory(context_id, path, content, expected_content_hash)` with **exactly the hash from that complete read**. The hash represents SHA-256 of the original raw bytes, not a hash of normalized text, a line excerpt, or regenerated Markdown.
4. Do **not** perform a redundant second read immediately before each update merely as a ritual: the server checks CAS while holding the project's write guard. Another writer changing the file will yield `CONFLICT`.
5. On `CONFLICT`, reread the full current file, compare with the intended change, reconcile without losing others' edits, and use the new hash for any safe retry. Ask when incompatible edits or changed decisions require judgement. Never retry by reusing a stale hash or replace content blindly.
6. Inspect `ok`, `error`, and returned `content_hash`. Read back when the change is important or a result is uncertain. A client confirmation may still be required because the current tool is marked destructive, even for a routine document update.

**Example procedure only, not an executed call:**

```text
read_memory(context_id="chat:MyProject", path="architecture/context-routing.md")
  -> data.content and data.content_hash (from the entire file)
update_memory(context_id="chat:MyProject",
              path="architecture/context-routing.md",
              content=<complete revised English Markdown>,
              expected_content_hash=<hash returned by full read>)
```

Do not submit placeholder context IDs, placeholder hash strings, or the pseudo-call notation verbatim.

## Deletion and uncertain outcomes

`delete_memory(context_id, path, expected_content_hash)` deletes one exact file after raw-byte CAS. Obtain explicit user approval for the **named file** and intended loss; then read the target to get its current hash. Refuse ambiguous bulk deletion or unapproved reorganization. Project deletion is a distinct single destructive administrative call and may remove all notes and bindings; approval must identify the actual target project UUID.

If a call times out, the client disconnects, or confirmation is ambiguous, **do not assume success or failure and blindly repeat** a non-idempotent creation, replacement, or deletion. Where possible, reread the exact path in the same context to verify the visible state and discuss ambiguity before further mutation. Failure to verify means the outcome remains unknown.

Manual edits, bulk imports, backup and restore of the private `/data` volume are **operator tasks performed only when the service container is stopped**. Do not suggest editing service storage concurrently with a running service.

## Durable documentation

Store confirmed findings, constraints, decisions with rationale, meaningful open issues, and useful investigation checkpoints in English. Relevant unresolved hypotheses may be recorded **clearly as provisional**, not as accepted facts. An agent may not independently replace a previously accepted architectural decision with a newly suggested one.

Prefer smaller, topic-oriented notes over a growing single document when no user convention exists. Never force a project plan, an ADR methodology, metadata tags, or a repository layout as a prerequisite to using memory.
