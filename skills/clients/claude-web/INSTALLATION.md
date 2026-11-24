# ProjectMemory for Claude Web - Source and setup guidance

## Status and scope

This directory contains Claude Web Skill source, not an installed skill or connected MCP connector. Shared references live in `skills/common/references/`; a standalone `project-memory/` skill must contain local copies under `references/`. No installable archive is provided here.

These files do not establish a live Claude Web-to-MCP connection. Configure the skill and remote connector separately, then verify tool discovery, permissions, context isolation, and protected access before use.

## User-facing templates

- [`instructions/GLOBAL-INSTRUCTIONS.md`](instructions/GLOBAL-INSTRUCTIONS.md): merge with the account-wide Claude instructions under **Settings > Your Claude > Instructions**, if available. Do not erase existing personalization.
- [`instructions/PROJECT-INSTRUCTIONS.md`](instructions/PROJECT-INSTRUCTIONS.md): replace the illustrative IDs and apply the text under **Project instructions** in the appropriate Claude Project.
- `SKILL.md`: Claude Web skill entrypoint. The complete distributable skill must also contain the shared technical references and any supported assets.

## Installation approach

1. Assemble a **standalone** skill folder named `project-memory/` with `SKILL.md` and `references/` copied from the shared source; rewrite the source links from `../../common/references/` to package-local `references/`. Prepare and check this directory structure before upload. Do not upload this repository source directory directly as a complete skill.
2. Follow Claude's supported custom Skill upload flow through **Customize > Skills**. Claude's current custom-skill flow uses a ZIP containing the skill directory and may require enabling code execution. Exact UI and account permissions must be checked at installation time.
3. Configure the **separate remote MCP custom connector** via Claude's supported connector settings. Claude Web connects from Anthropic cloud infrastructure; an accessible and effectively protected external HTTPS route is required for cloud usage. Do not expose the private HTTP backend directly or invent a tunnel/credential setup.
4. Have an authorized operator explicitly register and bind the exact primary `chat:` (or deliberately chosen bound `git:`) context. Associated Git IDs are informational: they do not replace the primary ID or create bindings.
5. Verify actual connector discovery, permission prompts, search results, context isolation, and negative-access behavior in the configured client. **These source files do not perform those tests.**

## ProjectMemory conventions

Conversation language is unrestricted. Persist **new or revised durable notes in English**. The skill promotes selective proactive retrieval and careful hash-CAS updates, with explicit user authorization for deletion, administration, bulk reorganization, and changes to established decisions. A `context_id` is a routing selector, not authorization.

## Official client documentation

- Claude custom Skills: https://support.claude.com/en/articles/12512198-how-to-create-custom-skills
- Claude personalization: https://support.claude.com/en/articles/10185728-understanding-claude-s-personalization-features
- Claude Projects: https://support.claude.com/en/articles/9519177-how-can-i-create-and-manage-projects
- Claude remote MCP connectors: https://support.claude.com/en/articles/11175166-get-started-with-custom-connectors-using-remote-mcp
