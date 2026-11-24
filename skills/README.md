# ProjectMemory client skills

This repository's `skills/` directory is the **maintained source** for ProjectMemory client-specific agent skills, shared technical references, and ready-to-adapt instruction templates. Its client folders are not self-contained standalone packages. This source layout does **not** change the ProjectMemory MCP server or configure any connection, credential, project, or binding.

## Client coverage

| Client | Source directory |
| --- | --- |
| ChatGPT Web | `clients/chatgpt-web/` |
| Codex CLI | `clients/codex/` |
| Claude Web | `clients/claude-web/` |
| Claude Code | `clients/claude-code/` |
| Hermes Agent | `clients/hermes/` |

These are source files and templates, not installed client integrations. A working integration requires a separately configured MCP connection, correct context bindings, and verification of client permissions and network protection.

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

**Important:** Client `SKILL.md` files link to `../../common/references/` for navigation **within this repository source tree**. This is intentional for single-source maintenance. Do not install a client source folder by itself. Run `bash skills/build-skills.sh` from the repository root to build standalone ZIPs under `skills/dist/`. The script copies shared references into each package, rewrites entrypoint links to `references/<filename>.md`, and checks the archives.

The shared icon is available under `common/assets/icon.svg`; include it in clients that support it when packaging.

## Client instructions and installation

### ChatGPT Web

Use the ChatGPT-specific [`SKILL.md`](clients/chatgpt-web/SKILL.md), [`agents/openai.yaml`](clients/chatgpt-web/agents/openai.yaml), [global instructions](clients/chatgpt-web/instructions/GLOBAL-INSTRUCTIONS.md), and [project template](clients/chatgpt-web/instructions/PROJECT-INSTRUCTIONS.md). A standalone skill archive must include local copies of the references and any supported assets. The project template uses an explicit primary `chat:` key and can list associated `git:` keys for repositories intentionally bound to the same internal project; these do not replace the primary key.

### Codex CLI

Use the [Codex entrypoint](clients/codex/SKILL.md), mergeable [`AGENTS.md` section](clients/codex/instructions/AGENTS.md), and [installation guidance](clients/codex/INSTALLATION.md). Select the exact repository from an unambiguous fetch `origin`; `local:` may be used only when there are no remotes and it is already explicitly configured and bound.

### Claude Web

Use the [Claude Web entrypoint](clients/claude-web/SKILL.md), [account-wide instructions](clients/claude-web/instructions/GLOBAL-INSTRUCTIONS.md), [project template](clients/claude-web/instructions/PROJECT-INSTRUCTIONS.md), and [installation guidance](clients/claude-web/INSTALLATION.md). Claude's Skills and remote MCP connector are configured separately. A remote cloud connector must be effectively protected; no connection is set up by this source.

### Claude Code

Use the [Claude Code entrypoint](clients/claude-code/SKILL.md), mergeable [`CLAUDE.md` section](clients/claude-code/instructions/CLAUDE.md), and [installation guidance](clients/claude-code/INSTALLATION.md). Git context derivation follows the same strict server-independent rules as Codex, adapted to Claude Code's project instruction and skill locations.

### Hermes Agent

Use the [Hermes entrypoint](clients/hermes/SKILL.md), the optional mergeable [`HERMES.md` project instructions](clients/hermes/instructions/HERMES.md), and [installation notes](clients/hermes/INSTALLATION.md). Hermes uses the active repository's verified Git fetch `origin` for coding tasks, with tightly constrained explicit identities for other task contexts. Do not replace Hermes' global `SOUL.md` with project instructions. The source does not install or verify a Hermes-to-MCP connection. Check actual tool discovery, permissions, and network protection in the target environment.

## Universal behavior and boundaries

- **No mandatory project methodology:** The skill never requires a planning document, an ADR system, fixed folders, frontmatter, note UUIDs, or a particular project structure. `note-conventions.md` suggests concise topic-oriented Markdown when users do not provide a preference.
- **No conversation language restrictions:** Source instructions/manuals are in English, and clients are guided to write **new or revised durable ProjectMemory note prose in English**. Technical identifiers and relevant quotations retain their exact spelling. The user may speak any language.
- **Memory handling:** Retrieve relevant context proactively. Save confirmed durable facts and explicitly labeled worthwhile hypotheses. Search before creating; perform full-file updates using the `content_hash` from a complete read as `expected_content_hash`. Do not reread automatically just before each update, but reread and reconcile after conflicts. Obtain explicit approval for deletion, project/context administration, bulk restructuring, and changes to established decisions. Respect real client permissions, including the destructive annotation on `update_memory`.
- **Routing is not access control:** Exact case-sensitive `chat:`, `git:`, and `local:` selectors require existing explicit bindings. Never guess, case-fold, alias, auto-register, or implicitly switch contexts. Unknown contexts fail closed.
- **Source-only status:** These files do not establish a live MCP connection. The service uses bounded lexical ripgrep search, **not** semantic search or reranking. Check real client permissions, context isolation, and protected network access before relying on an integration.

## Repository source and distribution

The `skills/` directory is the maintained source. The release packaging helper creates five client-specific `ProjectMemory-skill-<client>.zip` archives in the ignored `skills/dist/` directory. Each ZIP contains a top-level `project-memory/` skill folder with its entrypoint, local references, client instruction templates, and supported agent metadata/assets. The GitHub release workflow runs the same helper for valid release tags and attaches all five ZIPs alongside the server archives. The generated ZIPs are distribution files, not connected or verified client installations.

The source includes English documentation and templates, but users may customize their local instructions. It cannot enforce how users install, alter, or run their own agent configuration. No real account, live MCP or system privileges are implied.
