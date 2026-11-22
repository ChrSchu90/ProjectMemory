# Context identity and routing

## Exact identity is mandatory

Every project-scoped memory call requires a configured, **exact, case-sensitive** `context_id`. The current service accepts only these lowercase namespace prefixes:

- `chat:` for an explicitly named chat/client project context.
- `git:` for an explicitly identified repository context.
- `local:` for an explicitly registered workspace with **no Git remotes**, when the client can establish that condition.

The suffix contains 1-255 visible ASCII characters. Preserve the **entire** supplied value, including suffix case, punctuation, and path case. Do not trim, case-fold, transliterate, silently repair, synthesize, or translate an incoming context ID. `chat:MyProject` and `chat:myproject` are different keys. The server does not canonicalize an already supplied key.

A registered binding maps one exact context key to one server-issued internal project UUID. Multiple different keys can explicitly bind to the **same** project UUID; this is a many-to-one configuration, not a way to infer or create aliases. Project UUIDs identify administration targets, whereas note identity is an exact project-relative Markdown path.

## Web chat clients: choose an explicitly configured context

1. Read the exact configured project memory context ID from the active client/project instructions (for ChatGPT Web, the **Memory context ID** in ChatGPT Project Instructions).
2. Use it for `list_memories`, `search_memories`, `read_memory`, `create_memory`, `update_memory`, and `delete_memory` throughout that task.
3. Normally use a pre-registered, bound `chat:` identity. An explicitly selected and bound `git:` identity is also allowed when the active instructions deliberately designate that exact key.
4. Do **not** derive a web-client context from a visible project title, subject of conversation, saved assistant preference, document title, or a Git identity merely listed as an associated repository.
5. If the identity is missing, still a template placeholder, invalid, ambiguous, or unbound, stop **ProjectMemory operations**. Continue the user's main task without memory if feasible and explain that the binding must be set up by an authorized operator.

**Illustrative template values only, not registered or bound identities:**

```text
Memory context ID: chat:MyProject
Associated repository contexts:
  git:github.com/SomeUser/Repo1
  git:github.com/SomeUser/Repo2
```

These three illustrative values may be independently bound to one internal project, but listing them neither establishes bindings nor authorizes switching away from the explicitly selected primary web-chat context.

Use `resolve_context(context_id)` for read-only routing diagnostics when the tool is available; a separate resolve call is not required before each memory operation because the server revalidates on access. `list_context_bindings` is also read-only administration; its output must never become an excuse to select another project by guesswork.

## Repository-oriented coding clients

These are **coding-client derivation rules**, not an implicit fallback for a web chat context:

- Identify the actual working repository and inspect its **fetch** `origin` remote. Require an unambiguous remote identity; do not take a remote for a different workspace repository.
- Canonicalize the Git URL carefully: lowercase the DNS hostname only; preserve the case of the repository path. Any canonical identity must match a binding explicitly registered by the operator.
- Do not derive identity from a folder name, display name, push-only remote, remembered mapping, or assumed Git provider convention.
- Use `local:` only when **no Git remotes exist**, the operator has explicitly provided the local key, and that key is registered and bound. A missing or ambiguous `origin` in a repository that has remotes is not permission for a `local:` fallback.

Do not apply Git URL canonicalization to an **already provided** `context_id`; the server treats it as an opaque exact key.

## Trust and failures

`context_id` is **routing metadata only**, not proof of caller identity, project entitlement, or authorization. The approved architecture assumes protected access to a trusted personal deployment; application-level per-user or per-project permission checks are not provided by a context string.

An archived project rejects ordinary memory operations. Errors such as `INVALID_CONTEXT`, `CONTEXT_UNBOUND`, `PROJECT_ARCHIVED`, `PROJECT_BLOCKED`, and `PROJECT_NOT_FOUND` are not permissions to probe other IDs. Do not auto-create projects, bind contexts, unbind an existing context, or move it to another project.
