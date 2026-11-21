namespace ProjectMemory.Search;

using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using ProjectMemory.MemoryFiles;

/// <summary>Optional pattern controls shared by the query and all alternatives.</summary>
/// <param name="Syntax">Pattern syntax.</param>
/// <param name="Engine">Regex engine.</param>
/// <param name="Case">Pattern case handling.</param>
/// <param name="Boundary">Match boundary.</param>
/// <param name="Alternatives">Additional OR patterns.</param>
/// <param name="Invert">Select nonmatching records.</param>
/// <param name="Multiline">Permit newline-spanning regex.</param>
internal sealed record SearchMatch(
    [property: Description("literal (default) or regex.")] string Syntax = "literal",
    [property: Description("default or explicit pcre2 for regex look-around and backreferences.")] string Engine = "default",
    [property: Description("ignore (default), sensitive, or ripgrep smart case.")] string Case = "ignore",
    [property: Description("any, word, or whole line.")] string Boundary = "any",
    [property: Description("Up to 8 additional OR patterns.")] string[]? Alternatives = null,
    [property: Description("Select nonmatching lines. Incompatible with multiline.")] bool Invert = false,
    [property: Description("Allow regex matches spanning lines.")] bool Multiline = false);

/// <summary>Optional filters relative to the selected search directory.</summary>
/// <param name="Include">Allowed scope-relative globs.</param>
/// <param name="Exclude">Omitted scope-relative globs.</param>
/// <param name="MaxDepth">Depth below scope.</param>
/// <param name="IncludeHidden">Permit addressable hidden entries.</param>
/// <param name="RespectIgnoreFiles">Apply selected directory ignore rules.</param>
internal sealed record SearchFiles(
    [property: Description("Up to 8 ripgrep include globs, combined with OR.")] string[]? Include = null,
    [property: Description("Up to 8 ripgrep exclude globs. Exclusion wins.")] string[]? Exclude = null,
    [property: Description("Maximum depth below folder. Direct children have depth 1. Range 1–16.")] int MaxDepth = 16,
    [property: Description("Include eligible hidden entries. Dot names are never addressable.")] bool IncludeHidden = true,
    [property: Description("Apply .gitignore, .ignore and .rgignore from the selected directory only, in that order.")] bool RespectIgnoreFiles = false);

/// <summary>Optional projection and paging controls.</summary>
/// <param name="View">Requested projection.</param>
/// <param name="Before">Leading context lines.</param>
/// <param name="After">Trailing context lines.</param>
/// <param name="Offset">Rows skipped.</param>
/// <param name="Limit">Maximum returned rows.</param>
/// <param name="MaxHitsPerFile">Requested per-file record cap.</param>
internal sealed record SearchOutput(
    [property: Description("lines, files or counts. Counts separate match records and submatch occurrences.")] string View = "lines",
    [property: Description("Preceding lines per match, 0–8, lines view only.")] int Before = 0,
    [property: Description("Following lines per match, 0–8, lines view only.")] int After = 0,
    [property: Description("Rows skipped after sorting and projection, 0–100000.")] int Offset = 0,
    [property: Description("Rows per page, 1–200.")] int Limit = 50,
    [property: Description("Keep the first 1–1000 matching records per file before projection.")] int? MaxHitsPerFile = null);

/// <summary>Validated request shared by selection, process invocation and projection.</summary>
/// <param name="Query">Primary pattern.</param>
/// <param name="Folder">Validated directory scope.</param>
/// <param name="File">Optional exact memory path.</param>
/// <param name="Match">Pattern options.</param>
/// <param name="Files">Directory filters.</param>
/// <param name="Output">Projection options.</param>
internal sealed record SearchRequest(string Query, string Folder, string? File, SearchMatch Match, SearchFiles Files, SearchOutput Output)
{

    #region Static Fields

    #endregion

    #region Private Fields

    #endregion

    #region Constructors

    #endregion

    #region Properties

    #endregion

    #region Public Methods

    /// <summary>Publishes the closed option groups, supported values and limits in tool discovery.</summary>
    /// <param name="input">The SDK-generated search input schema.</param>
    internal static void DescribeSchema(JsonObject input)
    {
        var groups = input["properties"]!;
        foreach (var name in new[] { "match", "files", "output" })
        {
            groups[name]!["additionalProperties"] = false;
        }

        var match = groups["match"]!["properties"]!;
        match["syntax"]!["enum"] = new JsonArray("literal", "regex");
        match["engine"]!["enum"] = new JsonArray("default", "pcre2");
        match["case"]!["enum"] = new JsonArray("ignore", "sensitive", "smart");
        match["boundary"]!["enum"] = new JsonArray("any", "word", "line");
        groups["output"]!["properties"]!["view"]!["enum"] = new JsonArray("lines", "files", "counts");
        var files = groups["files"]!["properties"]!;
        var output = groups["output"]!["properties"]!;
        groups["query"]!["minLength"] = 1;
        groups["query"]!["maxLength"] = 512;
        groups["folder"]!["maxLength"] = 1024;
        groups["file"]!["maxLength"] = 1024;
        Range(files["max_depth"]!, 1, 16, 16);
        Range(output["before"]!, 0, 8, 0);
        Range(output["after"]!, 0, 8, 0);
        Range(output["offset"]!, 0, 100000, 0);
        Range(output["limit"]!, 1, 200, 50);
        Range(output["max_hits_per_file"]!, 1, 1000, null);
        foreach (var list in new[] { match["alternatives"]!, files["include"]!, files["exclude"]! })
        {
            list["maxItems"] = 8;
            list["items"]!["maxLength"] = list == match["alternatives"] ? 512 : 256;
        }

        static void Range(JsonNode field, int min, int max, int? initial)
        {
            field["minimum"] = min;
            field["maximum"] = max;
            field["default"] = initial;
        }
    }

    /// <summary>Rejects incompatible options and requests exceeding search bounds.</summary>
    internal void Validate()
    {
        static bool Pattern(string s) => !string.IsNullOrWhiteSpace(s) && s.Length <= 512 && !s.Any(char.IsControl);
        static bool Glob(string s) => !string.IsNullOrEmpty(s) && s.Length <= 256 && !s.Any(char.IsControl)
            && s[0] is not ('!' or '/' or '#') && !s.Contains('\\') && !s.Split('/').Contains("..");
        if (!Pattern(Query) || Match.Alternatives is { Length: > 8 } || Match.Alternatives?.All(Pattern) == false
            || Match.Syntax is not ("literal" or "regex") || Match.Engine is not ("default" or "pcre2")
            || Match.Case is not ("ignore" or "sensitive" or "smart") || Match.Boundary is not ("any" or "word" or "line")
            || (Match.Syntax == "literal" && (Match.Engine == "pcre2" || Match.Multiline)) || (Match.Invert && Match.Multiline)
            || Files.Include is { Length: > 8 } || Files.Exclude is { Length: > 8 }
            || Files.Include?.All(Glob) == false || Files.Exclude?.All(Glob) == false || Files.MaxDepth is < 1 or > 16
            || (File is not null && (Folder.Length != 0 || Files != new SearchFiles()))
            || Output.View is not ("lines" or "counts" or "files") || Output.Before is < 0 or > 8 || Output.After is < 0 or > 8
            || (Output.View != "lines" && (Output.Before != 0 || Output.After != 0))
            || Output.Offset is < 0 or > 100000 || Output.Limit is < 1 or > 200 || Output.MaxHitsPerFile is < 1 or > 1000)
            throw new MemoryFileException("INVALID_ARGUMENT");
    }

    #endregion

    #region Private Methods

    #endregion
}

/// <summary>A page of one requested projection with shared context lines for that page.</summary>
/// <param name="View">Selected view.</param>
/// <param name="Matches">Matching records for the lines view.</param>
/// <param name="Files">Paths for the files view.</param>
/// <param name="Counts">Per-file counts for the counts view.</param>
/// <param name="Context">Deduplicated context for this page.</param>
/// <param name="Offset">Applied page offset.</param>
/// <param name="Limit">Applied page size.</param>
/// <param name="HasMore">More eligible rows exist.</param>
/// <param name="LimitedByRequest">A per-file cap omitted at least one record.</param>
internal sealed record MemorySearchResult(string View, MemorySearchHit[] Matches, string[] Files, SearchCount[] Counts,
    SearchContext[] Context, int Offset, int Limit, bool HasMore, bool LimitedByRequest);
/// <summary>
/// A single ripgrep match record.
/// </summary>
/// <param name="Path">Exact memory path.</param>
/// <param name="Line">First matched line.</param>
/// <param name="EndLine">Last line in the record.</param>
/// <param name="Excerpt">Bounded text from the record.</param>
/// <param name="ExcerptTruncated">The excerpt omits text.</param>
/// <param name="Submatches">Byte spans in the original UTF-8 record.</param>
internal sealed record MemorySearchHit(string Path, int Line, int EndLine, string Excerpt, bool ExcerptTruncated, SearchSpan[] Submatches);
/// <summary>
/// A half-open UTF-8 byte range.
/// </summary>
/// <param name="StartByte">Inclusive byte offset.</param>
/// <param name="EndByte">Exclusive byte offset.</param>
internal sealed record SearchSpan(int StartByte, int EndByte);
/// <summary>
/// Match counts for one path.
/// </summary>
/// <param name="Path">Exact memory path.</param>
/// <param name="Records">Retained match events.</param>
/// <param name="Occurrences">Individual submatches.</param>
internal sealed record SearchCount(string Path, int Records, int Occurrences);
/// <summary>
/// A context line shared by page hits.
/// </summary>
/// <param name="Path">Exact memory path.</param>
/// <param name="Line">One-based line number.</param>
/// <param name="Text">Bounded surrounding text.</param>
/// <param name="Truncated">The context line was shortened.</param>
internal sealed record SearchContext(string Path, int Line, string Text, bool Truncated);
