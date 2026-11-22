# Searching ProjectMemory

## Contents

- [Search strategy](#search-strategy)
- [Inputs and option groups](#inputs-and-option-groups)
- [Output projections](#output-projections)
- [Limits, errors, and completeness](#limits-errors-and-completeness)

## Search strategy

`search_memories` performs **project-scoped lexical full-text search** over eligible ordinary Markdown files. It is implemented by a bounded read-only `/usr/bin/rg` subprocess using an internal ripgrep JSONL stream. It does **not** provide embeddings, semantic search, reranking, a note graph, an index service, or an arbitrary command shell.

Start simply with `context_id` and a meaningful `query`; the defaults use **case-insensitive literal** matching and a `lines` view. Search for alternate English wording, identifiers, acronyms, and relevant synonyms; translate natural-language search terms as appropriate without changing the user's communication language. If needed, use a filename inventory via `list_memories` to locate specific topic notes. Read authoritative matches through `read_memory` rather than treating clipped excerpts as complete documents.

## Inputs and option groups

```json
{
  "context_id": "chat:MyProject",
  "query": "authentication",
  "match": { "syntax": "literal", "case": "ignore" },
  "files": { "include": ["**/*.md"] },
  "output": { "view": "lines", "before": 1, "after": 2, "limit": 20 }
}
```

**Illustrative input only:** `chat:MyProject` is an unbound example, not an asserted working connection. Do not submit a placeholder as an actual context.

Top-level fields:

| Field | Meaning |
| --- | --- |
| `context_id` | Required, exact bound key. |
| `query` | Required, nonblank pattern (at most 512 UTF-16 code units; no control characters). |
| `folder` | Optional exact subtree, empty string by default. |
| `file` | Optional exact `.md` file path, exclusive with nonempty `folder` and nondefault directory filters. |
| `match` | Optional pattern controls below. |
| `files` | Optional directory filtering controls below. |
| `output` | Optional result projection and pagination below. |

**`match` options** (omitted values are defaults):

| Name | Values / default | Notes |
| --- | --- | --- |
| `syntax` | `literal` (default), `regex` | Literal is the straightforward default. |
| `engine` | `default` (default), `pcre2` | Explicit PCRE2 only with regex; for lookaround or backreferences. |
| `case` | `ignore` (default), `sensitive`, `smart` | Ripgrep smart-case behavior when requested. |
| `boundary` | `any` (default), `word`, `line` | Match anywhere, whole word, or whole line. |
| `alternatives` | Up to 8 OR patterns | Same pattern rules and matching options; max 512 characters each. |
| `invert` | `false` (default) or `true` | Select nonmatching records; incompatible with `multiline=true`. |
| `multiline` | `false` (default) or `true` | Regex only. Use escaped newline expressions where needed. |

**`files` options**:

| Name | Default | Notes |
| --- | --- | --- |
| `include` | Absent | Up to 8 include globs (OR), each at most 256 characters. |
| `exclude` | Absent | Up to 8 exclude globs; exclusion wins. |
| `max_depth` | `16` | Range 1-16 relative to the selected folder. |
| `include_hidden` | `true` | Only eligible addressable files. Dot-prefixed path components remain invalid memory addresses. |
| `respect_ignore_files` | `false` | Opt into selected `.gitignore`, `.ignore`, `.rgignore` files from the search directory. |

**`output` options**:

| Name | Default | Notes |
| --- | --- | --- |
| `view` | `lines` | `lines`, `files`, or `counts`. |
| `before` / `after` | `0` / `0` | 0-8 context lines each; only valid for `lines`. |
| `offset` | `0` | 0-100000, after sorting/projection. |
| `limit` | `50` | 1-200 projected rows per page. |
| `max_hits_per_file` | Unset | Optional cap of 1-1000 **match records per file**, before projection. |

The service validates glob patterns and incompatibilities. Do not pass CLI flags, `rg` commands, raw JSONL, shell expressions, unsafe paths, unrecognized fields, or obsolete legacy pagination fields.

## Output projections

All three views share `data.view`, `offset`, `limit`, `has_more`, and `limited_by_request`:

- **`lines`**: `data.matches` contains objects with `path`, `line`, `end_line`, `excerpt`, `excerpt_truncated`, `submatches`. Each submatch has `start_byte` and `end_byte` as half-open byte offsets **within the original UTF-8 match record**, not absolute file byte offsets. `data.context` contains deduplicated surrounding lines (`path`, `line`, `text`, `truncated`) for this page when requested.
- **`files`**: `data.files` contains matching project-relative path strings, sorted for pagination.
- **`counts`**: `data.counts` contains `{path, records, occurrences}`. `records` counts matching records; `occurrences` counts individual submatches, which may differ. If `max_hits_per_file` is set, counts reflect the retained records, not all possible hits.

`has_more` means there are additional **eligible projected rows** beyond this page. `limited_by_request` means the per-file record cap discarded matches; a page with `has_more=false` is **not** necessarily exhaustive if this flag is true. Paging queries are live and are not immutable snapshots: concurrent authorized writes may change results between pages. Combine pages only with that limitation in mind.

## Limits, errors, and completeness

Search is bounded by project scope, exact path validation, Markdown-only selection, file-size and scan budgets, a ten-second deadline (including search admission), and four concurrent search slots. The source currently limits full scan traversal to 4,096 inspected directory entries, a five-second directory scan budget, and 16 MiB of eligible file bytes. Individual Markdown files exceeding 1 MiB or containing invalid UTF-8/NUL bytes are not searchable. Excerpts and context lines may be clipped; always use `read_memory` for complete content.

`INVALID_ARGUMENT`, `INVALID_PATTERN`, `SCAN_LIMIT`, `SEARCH_TIMEOUT`, `SEARCH_UNAVAILABLE`, `SEARCH_IO`, `SEARCH_PROTOCOL`, and `SEARCH_FAILED` are tool failures, **not** proof of an empty search result. Narrow folder/depth/globs, simplify syntax, or retry only when appropriate. Never imply an incomplete scan or a capped result is exhaustive. Read-only search cannot change any Markdown file or create a binding.
