# ProjectMemory - Claude Project Instructions Template

> SETUP REQUIRED: All identifiers below are **illustrative placeholders**. Replace them with exact, case-sensitive IDs already registered and explicitly bound in ProjectMemory. Remove unused entries. Listing an ID does not register or bind it.

## ProjectMemory contexts

**Memory context ID:** `chat:MyProject`

**Associated Git repository context IDs:**
- `git:github.com/SomeUser/Repo1`
- `git:github.com/SomeUser/Repo2`

The **primary Memory context ID** above is the only key used for Claude Web memory operations in this Claude Project. The associated `git:` keys demonstrate how coding agents working in separate repositories may be bound to the **same internal ProjectMemory project**. They do not override the primary context or prove that any binding exists.

If this Claude Project intentionally uses an existing bound `git:` key as its primary identity, replace the **Memory context ID** itself rather than selecting a Git key dynamically from repository mentions.

## Agent behavior

Use the installed `project-memory` skill and the configured ProjectMemory MCP tools when persistent project knowledge is relevant. Pass the configured primary ID **exactly** to every project-scoped memory tool. Do not guess IDs or silently choose from associated repository keys.

Retrieve relevant context proactively; search before creating notes. Save only reusable, durable knowledge. Distinguish confirmed findings from explicitly labeled hypotheses or proposed decisions. Do not change an established decision without user approval.

Write newly created or revised persistent memory prose **in English**, while allowing the conversation to use any language. Prefer the user's existing note organization; otherwise group related information into concise topic-oriented Markdown notes without a prescribed project methodology.

For updates, use the complete `read_memory` content and its `content_hash` with `expected_content_hash` for the full replacement. An immediate extra read is not normally necessary. On conflict, reread and reconcile; do not overwrite blindly.

Obtain explicit user approval before deletion, administration, broad restructuring, or modifying established decisions. Respect all actual Claude tool prompts, including on `update_memory`. Check the MCP `isError` indicator and application `ok` before claiming an operation succeeded. Never store tokens, passwords, or other secrets.

If the primary context remains a placeholder, is unbound, or the connector is missing, continue the task without ProjectMemory where possible and state the limitation instead of improvising a binding or using another project.
