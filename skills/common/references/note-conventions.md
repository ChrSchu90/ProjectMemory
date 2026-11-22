# Optional durable note conventions

These are **suggestions** for cases where the user has not supplied a preferred organization or writing style. They are not required syntax, a project-management methodology, or a server-enforced schema. Always follow the user's existing, valid organization when one is available.

## Language and precision

Write all **new and revised persistent memory prose in English** irrespective of the conversation language. Use concise, unambiguous titles and headings. Keep code, exact identifiers, repository names and paths, commands, APIs, original error text, and relevant literal quotations unchanged. Do not silently translate entire historical collections; adapt only material being intentionally revised.

Memory text being in English is an agent documentation convention that may simplify later English-oriented retrieval or reranking experiments. ProjectMemory currently exposes **lexical search only**; no ranking or language enforcement feature is implied.

## One coherent topic per note

- Choose a stable descriptive `.md` filename and a small set of topical folders if useful. For example: `architecture/context-routing.md`, `operations/backup-restore.md`, or `investigations/search-limits.md`.
- Keep one coherent subject per note. If independent subjects make a note unwieldy, propose or create focused files as appropriate, respecting user preferences and any required approval for broad restructuring.
- Prefer updated, synthesized reference material over chronological message dumps, duplicate source documents, raw logs, and a single ever-growing catch-all file.
- Search before adding a topic. Link to an **existing** related note with an ordinary Markdown relative link when that helps; links are not server-side graph relations.
- Use only the sections that add value. Do not create empty headings, arbitrary tags, frontmatter, schema definitions, or placeholder files just to fit a template.

## Suggested note anatomy (optional)

```markdown
# Context Routing

## Summary
The memory service routes every operation using an exact bound context ID.

## Confirmed Behavior
- Context IDs are case-sensitive.
- Missing bindings do not trigger project creation or fallback.

## Rationale
Explicit routing prevents accidental cross-project selection.

## Open Questions
- Verify the integration's live behavior in each approved client.

## Related Notes
- [Memory Operations](../operations/memory-operations.md)
```

This example is **not** a predefined required project structure, confirmed live-client result, or evidence that the referenced file exists.

## Represent evidence and uncertainty

- Distinguish **verified facts**, **accepted decisions**, **observations**, **hypotheses**, and **proposals** in ordinary prose when the distinction matters.
- Record source paths, dates, relevant versions, or validation evidence where helpful; do not invent citations, test results, or acceptance statuses.
- Describe an accepted decision and its rationale accurately; do not silently promote a recommendation into an approved change.
- Capture the current state and concise history when a decision is explicitly superseded; avoid an unnecessary replay of the full conversation.
- A provisional finding may be saved for later work only when it is genuinely reusable and clearly marked provisional.

## No mandatory workflow

A user's project might have no formal planning documents, ADRs, fixed folders, or decision records. Do not impose any of those. The ProjectMemory skill guides **safe storage and retrieval**, not how the user should plan their work.
