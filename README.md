# ProjectMemory

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE) [![GHCR](https://img.shields.io/badge/GHCR-project--memory--blue?logo=github)](https://github.com/ChrSchu90/ProjectMemory/pkgs/container/project-memory) [![Build](https://github.com/ChrSchu90/ProjectMemory/actions/workflows/build.yml/badge.svg)](https://github.com/ChrSchu90/ProjectMemory/actions/workflows/build.yml)

> [!IMPORTANT]
> ProjectMemory is under active development. 
> Automated tests cover the server and MCP workflows, but individual client integrations and deployment environments should be validated before relying on them for important data.

**Lightweight, self-hosted project memory for AI assistants via the Model Context Protocol (MCP).**

ProjectMemory helps AI assistants retain useful context between conversations and work sessions: architectural decisions, implementation details, constraints, open questions, and lessons learned. Its main purpose is to keep that knowledge **attached to the right project**.

Rather than building one large, general-purpose memory of everything an assistant has encountered, ProjectMemory keeps unrelated projects separate. This is especially useful when you work on several complex codebases, each with its own decisions, terminology, and technical assumptions.

## Project-first, not general-purpose memory

A decision that makes sense for one project may be wrong for another. Mixing those notes can lead to misleading answers or suggestions based on the wrong codebase.

With ProjectMemory, each project has its own collection of notes. A chat, Git repository, or local workspace can be **explicitly linked** to a project. Multiple contexts can point to the same project when they belong together, while unrelated projects keep their memories separate.

ProjectMemory does not guess which project a request belongs to. An unknown or unbound context is rejected instead of silently searching a default or global memory.

## Simple by design

- **Plain Markdown:** Memories are ordinary, human-readable `.md` files in project folders. No special note database, mandatory metadata headers, or proprietary format.
- **Small local database:** Embedded SQLite stores project information, context bindings, and internal recovery state—not the text of individual memories. No external database server is needed.
- **Direct text search:** [ripgrep](https://github.com/BurntSushi/ripgrep) searches the Markdown files, with simple text queries and optional advanced filters or regular expressions. There are no embeddings, vector databases, or semantic search models.
- **Self-hosted and lightweight:** A single .NET service, suitable for a private Docker deployment. The server does not need a GPU, external AI provider, or separately hosted search service.
- **One endpoint, multiple projects:** Trusted MCP clients can work with different projects through the same service without configuring a separate server for every project.
- **Careful updates:** Project-level coordination and content-hash checks help prevent concurrent changes from silently overwriting each other.

The result is a memory store you can understand, inspect, and back up without introducing a complex search or data platform.

## How it works

1. **Create a project** to hold its memories.
2. **Bind a context** such as `chat:my-app`, `git:github.com/team/my-app`, or `local:my-app` to that project. Context IDs are explicit and case-sensitive.
3. **Use an MCP client** to list, search, read, create, update, or delete notes within the selected project.
4. **Return later** from a linked chat or repository and retrieve the same project-specific context.

The same project can have several context bindings. For example, a coding assistant working in a repository and a web chat discussing its architecture can share the project's notes without mixing unrelated notes into a project-scoped search.

ProjectMemory also provides administrative tools for managing bindings and archiving, reactivating, or deleting projects. There is no automatic project creation, implicit context switching, or fallback to general memory.

### Where the data lives

The persistent data directory contains a small SQLite database and separate Markdown trees for each project. For example:

```text
/data/
├── ProjectMemory.db          # Projects and context bindings
└── projects/
    └── <project-id>/
        ├── architecture.md
        └── decisions/
            └── storage.md
```

Notes are identified by their project-relative paths, not by database records. You can inspect the files with ordinary text tools. **Stop the service before editing or moving files directly**, so external changes do not bypass its coordination and safety checks.

## Getting started

ProjectMemory is built with .NET and can run as a single Docker container. The Docker Compose file is located at [`examples/compose.yml`](examples/compose.yml). From the repository root, a minimal example is:

```bash
docker run --rm --user 1000:1000 \
  -p 8000:8000 \
  -v projectmemory-data:/data \
  ghcr.io/chrschu90/project-memory:1
```

The MCP endpoint is `http://127.0.0.1:8000/mcp`; the readiness endpoint is `/health/ready`. Connect a compatible MCP client and explicitly create and bind a project before working with its memories.

Client-specific skill sources and instruction templates are available under [`skills/`](skills/README.md). They require their own client configuration; the source folders are not ready-made standalone installations.

### Operating safely

ProjectMemory is designed for a **private, trusted environment**, not as a multi-user authorization system. A context ID selects a project; it is **not** an authentication credential or a per-client permission boundary. Protect any remotely accessible endpoint with appropriate network restrictions and, where needed, a bearer token (`ProjectMemory__BearerToken`) and an operator-managed HTTPS gateway or tunnel. Never expose a token-free endpoint publicly without an effective external access boundary. Connected AI clients may also send retrieved note content to their own model providers.

Only one running ProjectMemory instance should use a given `/data` directory. For backups or restores, **stop the service and copy the entire data directory**, including SQLite, Markdown, and recovery files. Backups are operator-managed; ProjectMemory does not provide a backup service.
