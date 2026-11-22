# ProjectMemory client skills

This `skills/` directory is the **maintained source** for ProjectMemory client-specific agent skills, shared technical references, and ready-to-adapt instruction templates. Copy this directory to the root of the ProjectMemory repository. This source layout does **not** change the ProjectMemory MCP server or configure any connection, credential, project, or binding.

## Client coverage

| Client | Maintained source | Qualification |
| --- | --- | --- |
| ChatGPT Web | `clients/chatgpt-web/` | Offline source; initial T-013 package previously accepted, but no real client/MCP acceptance |
| Codex CLI | `clients/codex/` | Offline source; no real Codex/MCP acceptance |
| Claude Web | `clients/claude-web/` | Offline source; no real Claude Web/MCP acceptance |
| Claude Code | `clients/claude-code/` | Offline source; no real Claude Code/MCP acceptance |
| Hermes Agent | `clients/hermes/` | Owner-authorized early **offline source only**; official live integration/security acceptance stays post-release (D-031/T-016), pending central-plan reconciliation |

The first production release still requires **four real clients** (ChatGPT Web, Claude Web, Codex CLI, Claude Code). Offline skill files are not evidence of real client interoperability or protected ingress. The owner-held D-026/M4-B approval gate remains in effect.

## Maintained source structure

```text
skills/
├── README.md
├── common/
│   ├── assets/
│   │   └── icon.svg
│   └── references/
│       ├── context-identity.md
│       ├── errors.md
│       ├── git-identity.md
│       ├── mcp-interface.md
│       ├── note-conventions.md
│       ├── notes.md
│       ├── safety.md
│       └── search.md
└── clients/
    ├── chatgpt-web/
    │   ├── SKILL.md
    │   ├── agents/openai.yaml
    │   └── instructions/
    │       ├── GLOBAL-INSTRUCTIONS.md
    │       └── PROJECT-INSTRUCTIONS.md
    ├── codex/
    │   ├── SKILL.md
    │   ├── agents/openai.yaml
    │   ├── INSTALLATION.md
    │   └── instructions/AGENTS.md
    ├── claude-web/
    │   ├── SKILL.md
    │   ├── INSTALLATION.md
    │   └── instructions/
    │       ├── GLOBAL-INSTRUCTIONS.md
    │       └── PROJECT-INSTRUCTIONS.md
    ├── claude-code/
    │   ├── SKILL.md
    │   ├── INSTALLATION.md
    │   └── instructions/CLAUDE.md
    └── hermes/
        ├── SKILL.md
        ├── INSTALLATION.md
        └── instructions/HERMES.md
```

### Shared references

Maintain each technical reference **once** in `common/references/`. These files specify the 15 public MCP operations, the `1.0` result envelope, exact context routing, lexical ripgrep search, Markdown file lifecycle, CAS updates, error handling, optional note structure, safety and Git-origin identity. Keep only client-specific identity selection, interface behavior, and installation guidance in `clients/<client>/`.

**Important:** Client `SKILL.md` files currently link to `../../common/references/` for navigation **within this repository source tree**. This is intentional for single-source maintenance. It is **not** a directly installable client skill folder when copied by itself. Before installing or uploading a standalone skill, copy its referenced shared files into that skill's `references/` directory and rewrite these links to package-local `references/<filename>.md`. A future GitHub Actions workflow or simple Bash script may assemble and validate independently installable distributions, but **no build or test scripts are included now**.

The shared icon is available under `common/assets/icon.svg`; include it in clients that support it when packaging.

## Client instructions and future installation

### ChatGPT Web

Use the ChatGPT-specific [`SKILL.md`](clients/chatgpt-web/SKILL.md), [`agents/openai.yaml`](clients/chatgpt-web/agents/openai.yaml), [global instructions](clients/chatgpt-web/instructions/GLOBAL-INSTRUCTIONS.md), and [project template](clients/chatgpt-web/instructions/PROJECT-INSTRUCTIONS.md). A future installable skill archive must include copied local references and the icon. The project template uses an explicit primary `chat:` key and can list associated `git:` keys for repositories intentionally bound to the same internal project; these do not replace the primary key.

### Codex CLI

Use the [Codex entrypoint](clients/codex/SKILL.md), mergeable [`AGENTS.md` section](clients/codex/instructions/AGENTS.md), and [installation guidance](clients/codex/INSTALLATION.md). Select the exact repository from an unambiguous fetch `origin`; `local:` may be used only when there are no remotes and it is already explicitly configured and bound.

### Claude Web

Use the [Claude Web entrypoint](clients/claude-web/SKILL.md), [account-wide instructions](clients/claude-web/instructions/GLOBAL-INSTRUCTIONS.md), [project template](clients/claude-web/instructions/PROJECT-INSTRUCTIONS.md), and [installation guidance](clients/claude-web/INSTALLATION.md). Claude's Skills and remote MCP connector are configured separately. A remote cloud connector must be effectively protected; no connection is set up by this source.

### Claude Code

Use the [Claude Code entrypoint](clients/claude-code/SKILL.md), mergeable [`CLAUDE.md` section](clients/claude-code/instructions/CLAUDE.md), and [installation guidance](clients/claude-code/INSTALLATION.md). Git context derivation follows the same strict server-independent rules as Codex, adapted to Claude Code's project instruction and skill locations.

### Hermes Agent (early offline source; post-release acceptance)

Use the [Hermes entrypoint](clients/hermes/SKILL.md), the optional mergeable [`HERMES.md` project instructions](clients/hermes/instructions/HERMES.md), and [installation notes](clients/hermes/INSTALLATION.md). Hermes uses the active repository's verified Git fetch `origin` for coding tasks, with tightly constrained explicit identities for other task contexts. Do not replace Hermes' global `SOUL.md` with project instructions. The user has **now expressly authorized creating the Hermes offline source** so all five client entrypoints can be reviewed against the shared reference architecture. This authoring-timing change awaits reconciliation in the central planning chat; D-031's separately accepted **post-production live Hermes** scope remains unaffected. No real Hermes installation or integration is claimed.

## Universal behavior and boundaries

- **No mandatory project methodology:** The skill never requires `PLAN.md`, an ADR system, fixed folders, frontmatter, note UUIDs, or a particular project structure. `note-conventions.md` suggests concise topic-oriented Markdown when users do not provide a preference.
- **No conversation language restrictions:** Source instructions/manuals are in English, and clients are guided to write **new or revised durable ProjectMemory note prose in English**. Technical identifiers and relevant quotations retain their exact spelling. The user may speak any language.
- **Behavior B:** Retrieve relevant context proactively. Save confirmed durable facts and explicitly labeled worthwhile hypotheses. Search before creating; perform full-file updates using the `content_hash` from a complete read as `expected_content_hash`. Do not reread automatically just before each update, but reread and reconcile after conflicts. Obtain explicit approval for deletion, project/context administration, bulk restructuring, and changes to established decisions. Respect real client permissions, including the destructive annotation on `update_memory`.
- **Routing is not access control:** Exact case-sensitive `chat:`, `git:`, and `local:` selectors require existing explicit bindings. Never guess, case-fold, alias, auto-register, or implicitly switch contexts. Unknown contexts fail closed.
- **No live connection or production claim:** These are offline source files. The current MCP baseline is bounded lexical ripgrep, **not** semantic search or reranking. Skill source/packaging cannot replace M4-B's real-client, protected-ingress, security and approval checks.

## Repository migration and later distribution

Copy the `skills/` folder to the repository root. It contains **no** `build.py`, `test_build.py`, `tests/`, generated client ZIPs, GitHub Actions workflow, or Bash build helper. Leave packaging and release automation for a separately approved work step.

The owner reports having removed the old Basic Memory/proxy examples from `examples/agent settings/` in the working repository. Those legacy examples are no longer maintained and are **not** restored by this source overlay. The overlay does not delete or overwrite existing service code, Git history, release workflows, or `PLAN.md`.

The source includes English documentation and templates, but users may customize their local instructions. It cannot enforce how users install, alter, or run their own agent configuration. No real account, live MCP or system privileges are implied.
