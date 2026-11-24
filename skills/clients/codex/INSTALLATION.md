# ProjectMemory Skill for Codex CLI

## Status

This directory contains Codex CLI Skill source. Shared technical references and the icon are maintained under `skills/common/`. It is not a packaged installation, and the source files do not configure or verify a working Codex-to-MCP connection.

## Source organization

- `skills/clients/codex/SKILL.md`: Codex-specific entrypoint and repository identity behavior.
- `skills/clients/codex/agents/openai.yaml`: client UI metadata.
- `skills/clients/codex/instructions/AGENTS.md`: a **mergeable** ProjectMemory instruction section; do not overwrite existing project rules.
- `skills/common/references/`: shared technical documentation. Coding-specific Git identity guidance is in `git-identity.md`.

The relative links in the source entrypoint refer to `../../common/references/`. They work inside the repository source tree, **not** if only `SKILL.md` is copied to an installed skill location. For a self-contained installation, copy the referenced common files into `project-memory/references/` and rewrite the links to package-local `references/` paths.

## Install a complete Skill

Codex supports skill directories such as:

- Project-scoped: `<repository>/.agents/skills/project-memory/SKILL.md`.
- User-scoped: `~/.agents/skills/project-memory/SKILL.md`.

A complete installation must include its entrypoint, client metadata, and the local `references/` files. Do not install the repository source directory by itself as if it were a standalone Skill. If Codex does not discover new skills in the current session, use its documented refresh or start a new session.

## Integrate instructions

Merge the ProjectMemory text from `skills/clients/codex/instructions/AGENTS.md` into the appropriate existing `AGENTS.md` scope while preserving higher-priority project, security, and Git instructions. The skill does **not** require English conversations; new and revised durable ProjectMemory note prose should be written in English.

## Configure routing independently

1. Configure an effectively protected ProjectMemory MCP connection using the supported Codex settings. The skill does not install, authenticate, or expose a server.
2. Inspect the **actual task repository's unambiguous fetch `origin`**. Example: `https://github.com/SomeUser/Repo1.git` can derive `git:github.com/SomeUser/Repo1` under a reviewed host/transport policy. Preserve the owner/path case; lowercase the DNS host only.
3. Require the resulting key to be explicitly registered and bound by an authorized operator. If the repository has **no remotes at all**, an already configured and bound `local:` key is permitted. Having other remotes but no `origin` is not a no-remotes case.
4. If routing or tool access is unavailable, continue the task without ProjectMemory rather than guessing an alternative ID or altering bindings.

**No credentials, actual service endpoint, real bindings, or client tests are included in this source.** Never expose token-disabled HTTP publicly without an independently verified protection boundary. Before use, verify tool discovery and permissions in the configured client.

## Safety and compatibility

The service's current interface has 15 public MCP tools and application envelope version `1.0`. Notes are exact-path Markdown, whole-file SHA-256 CAS protects update/delete, and advanced search is bounded lexical ripgrep, not semantic. Check MCP `isError` and application `ok`. `update_memory` may trigger client confirmation because it is annotated destructive. `delete_project(project_id)` is one destructive call without a guaranteed human dialog; obtain task-specific authorization before attempting it.

## Language and documentation

All distributed Skill source and instruction templates are in English. Conversation language is unrestricted; durable English note prose is an agent convention and is not server-enforced.
