# Repository context identity for coding agents

## Why this matters

The ProjectMemory server resolves **exact case-sensitive** `git:`, `chat:`, and `local:` identifiers to explicitly registered projects; it does not derive or normalize a submitted ID. The coding agent must select the intended repository's identity before calling any memory tool. A context ID is a **routing selector**, never a credential or per-client authorization grant.

## Read-only identification procedure

1. Locate the actual Git repository for the task, rather than assuming the current terminal folder is correct. For ordinary repositories, `git rev-parse --show-toplevel` is a useful read-only check. With nested repositories, submodules, or worktrees, verify which worktree/repository owns the files being modified.
2. Inspect configured remotes using read-only Git operations. `git remote` lists remote names. For the relevant repository, `git remote get-url --all origin` inspects **fetch** URLs. Do **not** use `git remote get-url --push origin`, `remote.origin.pushurl`, or a convenient remote other than `origin`. Git configuration can rewrite effective remote URLs; investigate ambiguity before deriving an ID.
3. Require one unambiguous, valid fetch origin. Multiple distinct fetch destinations, missing origin while other remotes exist, unsupported transports, uncertain host equivalence, and credentials or rewriting that cannot be safely interpreted **must fail closed**. Do not modify remote configuration to make it fit the memory identity.
4. Under a **reviewed Git host and transport equivalence policy**, canonicalize the URL into `git:<host>[:<nondefault-port>]/<repository-path>`: lowercase only the DNS host; preserve the case of every repository owner/path segment; remove standard transport/user-info/syntactic trailing `.git` details and standard ports. Do not fold repository-path case, invent host aliases, treat forks/renames as equal, or normalize an already provided `context_id`.
5. Require the resulting exact key to be explicitly registered and bound to the intended ProjectMemory project. The server validates on each scoped operation. A read-only `resolve_context` may help with diagnostics, but a failed resolution does not permit another context or automatic binding.

## Illustrative examples (not evidence of bindings)

| Unambiguous verified fetch `origin` | Canonical context |
| --- | --- |
| `https://github.com/SomeUser/Repo1.git` | `git:github.com/SomeUser/Repo1` |
| `git@github.com:SomeUser/Repo1.git` | `git:github.com/SomeUser/Repo1` |
| `ssh://git@github.com/SomeUser/Repo2.git` | `git:github.com/SomeUser/Repo2` |

The HTTPS/SSH/SCP examples demonstrate a **reviewed** conventional GitHub hostname and path. Do not assume that all arbitrary Git servers treat those transports or ports as identical. The example IDs are not registered automatically. `git:github.com/SomeUser/Repo1` and `git:github.com/someuser/repo1` remain **distinct** context IDs even if an upstream host is case-insensitive.

Never echo an origin URL containing credentials into user-visible output or persistent notes. Redact secrets before documenting a routing problem, and preserve technical identity spelling when reporting the sanitized final key.

## Local repositories without any remote

Use `local:` **only** when Git has **no configured remotes at all**, an operator/user has supplied an exact stable `local:` ID, and the ID has already been registered and bound. A repository with `upstream` but no `origin` does **not** qualify. A repository with an invalid or ambiguous `origin` does **not** qualify. A non-Git folder cannot be made a ProjectMemory project by guessing a local ID; require explicit user/operator identity and applicability confirmation.

For example, `local:MyOfflineWorkspace` is only a placeholder. Do not create a default key from the checkout folder, Git branch name, current task, or chat title. If conditions fail, continue the work without ProjectMemory and explain what must be configured.

## Multiple repositories and failure cases

Several repositories can each have their **own** exact `git:` IDs bound by an operator to the **same** internal project UUID. This is a many-to-one binding, not an automatic alias. Choose based on the repository relevant to the current operation; do not pool results across contexts or switch context solely because two repos share a subject. When a task explicitly spans multiple repositories, resolve each independently and keep the origin and scope of each memory call clear.

An origin rename, fork, repository relocation, or revised namespace may produce a **different** exact context key. Escalate re-registration or deliberate unbind/bind to an authorized operator. `bind_context` never silently retargets an existing key and `unbind_context` does not delete memory content. Never silently repair or guess a replacement context.

Git inspection commands are read-only; they do not authorize repository mutations, network access, or changes to MCP endpoint configuration. Client-specific permission prompts and the project's own agent/repository policies always apply.
