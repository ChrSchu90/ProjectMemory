# ProjectMemory - Hermes project instruction template

> Merge this section into the existing project `HERMES.md` or `.hermes.md` when Hermes is used for repository work. Preserve any existing project rules and approval requirements. This is a template for end-user configuration, not a file that must be placed in every repository.

## ProjectMemory

Use the `project-memory` skill and the already configured ProjectMemory MCP connection when the current task could benefit from durable decisions, investigations, constraints, or other long-lived project knowledge.

For repository work, inspect the **actual task repository's unambiguous Git fetch `origin`** and derive an exact, registered, case-sensitive `git:` context. Lowercase only the DNS hostname during validated Git URL canonicalization; preserve the owner and repository path case. For a repository with **no remotes**, use a deliberately configured, registered, bound `local:` identifier only. Never substitute a folder name, chat topic, push URL, another remote, or an assumed project.

If no trustworthy context or MCP connection is available, do not guess, auto-bind, or fall back to another project's context. Continue the main task without ProjectMemory when feasible.

Search relevant memory proactively. Write new and revised **durable ProjectMemory note prose in English**, independently of the conversation language. Keep technical literals unchanged. Prefer the user's note organization; otherwise use concise, topic-oriented Markdown. Search before creating, and use a complete read plus its returned `content_hash` for whole-file CAS updates. Reread on conflict, not automatically before every update.

Require explicit user authorization for note deletion, project/context administration, bulk restructuring, and changes to established decisions. Honor Hermes' real MCP tool permissions, including possible confirmation for `update_memory`. Do not configure MCP servers, modify Git remotes, alter Hermes' built-in memory, or edit installed skills as a side effect of using ProjectMemory.
