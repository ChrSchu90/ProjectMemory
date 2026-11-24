# ProjectMemory Project Instructions - Template

> **SETUP REQUIRED:** The IDs below are **illustrative placeholders**, not registered or bound contexts. Replace them with the exact, case-sensitive IDs already registered in your ProjectMemory service. Remove unused Git entries. Merely listing an ID does **not** create or validate a binding.

## ProjectMemory contexts

**Memory context ID:** `chat:MyProject`

**Associated Git repository context IDs:**
- `git:github.com/SomeUser/Repo1`
- `git:github.com/SomeUser/Repo2`

The primary `Memory context ID` above is the **only context** to use for ChatGPT Web memory operations in this ChatGPT Project. The associated `git:` IDs illustrate how Codex, Hermes, or other coding agents can be separately registered and bound to the **same internal ProjectMemory project**. Each such binding must be explicitly set up and verified by an authorized operator. These entries are metadata for the intended repository integrations; they do not override the ChatGPT Web context and do not establish that a binding exists.

If this ChatGPT Project deliberately uses an already bound `git:` context as its primary identity, replace the **Memory context ID** value itself. Do not switch to an associated Git ID merely because a task mentions that repository.

## Agent behavior

Use the installed `project-memory` skill and the configured ProjectMemory MCP tools for relevant durable project knowledge. For all six project-scoped memory tools, pass the configured primary `Memory context ID` **exactly as written**, preserving case and punctuation.

If the primary ID is still a placeholder, cannot be resolved, is unbound, or the service/skill is unavailable, do not guess another context, create a project, or alter bindings. Continue the user's task without ProjectMemory when feasible and describe the limitation.

Search and read proactively for relevant context. Maintain confirmed, long-lived knowledge carefully; record worthwhile hypotheses as **provisional**, not approved. Respect the user's preferred note organization. Otherwise use concise, topic-oriented Markdown and avoid a single ever-growing catch-all note.

Write all **new and updated persistent memory prose in English**, without restricting the language used in conversation. For full-file updates, use the `content_hash` from a complete `read_memory` as `expected_content_hash`; let the server detect concurrent writes. On conflict, reread and reconcile rather than overwrite. Do not change an established decision without approval.

Obtain explicit user approval for memory deletion, project or binding administration, broad restructuring, or changing accepted decisions. Respect client-side tool confirmation requests and check actual MCP/application results before reporting success. Do not store credentials, tokens, or other secrets.
