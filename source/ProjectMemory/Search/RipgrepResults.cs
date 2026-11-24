namespace ProjectMemory.Search;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using ProjectMemory.MemoryFiles;

/// <summary>Validates ripgrep JSONL and projects completed match records into one deterministic page.</summary>
/// <param name="request">Validated selection and result options.</param>
internal sealed class RipgrepResults(SearchRequest request)
{
    #region Static Fields

    #endregion

    #region Private Fields

    private readonly List<MemorySearchHit> _hits = [];
    private readonly Dictionary<(string Path, int Line), SearchContext> _context = [];
    private int _spans;

    #endregion

    #region Constructors

    #endregion

    #region Properties

    #endregion

    #region Public Methods

    /// <summary>
    /// Starts protocol validation for one explicit file batch.
    /// </summary>
    /// <param name="paths">Exact operands allowed in emitted events.</param>
    /// <returns>Validator for this child process.</returns>
    internal Batch BeginBatch(IEnumerable<string> paths) => new(this, paths);

    /// <summary>
    /// Sorts, caps and pages fully validated records.
    /// </summary>
    /// <param name="token">Cancellation during final projection.</param>
    /// <returns>A bounded projection for the requested view.</returns>
    internal MemorySearchResult Page(CancellationToken token)
    {
        var options = request.Output;
        var sorted = _hits.OrderBy(h => h.Path, StringComparer.Ordinal).ThenBy(h => h.Line).ThenBy(h => h.Submatches.FirstOrDefault()?.StartByte ?? 0);
        var hits = sorted.GroupBy(h => h.Path).SelectMany(g => g.Take(options.MaxHitsPerFile ?? int.MaxValue)).ToArray();
        var counts = hits.GroupBy(h => h.Path).Select(g => new SearchCount(g.Key, g.Count(), g.Sum(h => h.Submatches.Length))).ToArray();
        var page = options.View == "lines" ? hits.Skip(options.Offset).Take(options.Limit).ToArray() : [];
        var files = counts.Skip(options.Offset).Take(options.Limit).ToArray();
        var context = new HashSet<(string, int)>();
        foreach (var hit in page)
        {
            token.ThrowIfCancellationRequested();
            for (var n = Math.Max(1, hit.Line - options.Before); n < hit.Line; n++)
            {
                context.Add((hit.Path, n));
            }

            for (var n = 1; n <= options.After; n++)
            {
                context.Add((hit.Path, checked(hit.EndLine + n)));
            }
        }

        var surroundings = context.Where(_context.ContainsKey).Select(key => _context[key])
            .OrderBy(c => c.Path, StringComparer.Ordinal).ThenBy(c => c.Line).ToArray();
        return new(options.View, page, options.View == "files" ? files.Select(c => c.Path).ToArray() : [],
            options.View == "counts" ? files : [], surroundings, options.Offset, options.Limit,
            (options.View == "lines" ? hits.Length : counts.Length) - options.Offset > options.Limit, hits.Length < _hits.Count);
    }

    #endregion

    #region Private Methods

    private static MemoryFileException Invalid() => new("SEARCH_PROTOCOL");

    private static string Text(JsonElement value)
    {
        var hasText = value.TryGetProperty("text", out var text);
        var hasBytes = value.TryGetProperty("bytes", out var bytes);
        if (hasText == hasBytes)
            throw Invalid();
        var result = hasText ? text.GetString() : RipgrepSearchService.StrictUtf8.GetString(bytes.GetBytesFromBase64());
        if (result is null || result.Contains('\0'))
            throw Invalid();
        return result;
    }

    private void Add(string relative, JsonElement data, bool match)
    {
        var path = request.Folder.Length == 0 ? relative : request.Folder + "/" + relative;
        var line = data.GetProperty("line_number").GetInt32();
        if (!MemoryFileStore.ValidPath(path) || line is < 1 or > MemoryFileStore.MaximumBytes + 1 || data.GetProperty("absolute_offset").GetInt64() < 0)
            throw Invalid();
        var raw = Text(data.GetProperty("lines"));
        var utf8 = Encoding.UTF8.GetBytes(raw);
        var spans = new List<SearchSpan>();
        foreach (var sub in data.GetProperty("submatches").EnumerateArray())
        {
            if (++_spans > 16384)
                throw new MemoryFileException("SCAN_LIMIT");
            var start = sub.GetProperty("start").GetInt32();
            var end = sub.GetProperty("end").GetInt32();
            if (start < 0 || start > end || end > utf8.Length || (spans.Count > 0 && start < spans[^1].EndByte)
                || !utf8.AsSpan(start, end - start).SequenceEqual(Encoding.UTF8.GetBytes(Text(sub.GetProperty("match")))))
                throw Invalid();
            spans.Add(new(start, end));
        }

        var body = raw.EndsWith('\n') ? raw[..^1] : raw;
        var last = checked(line + body.AsSpan().Count('\n'));
        if (request.Output.Before + request.Output.After > 0)
        {
            if (last - line + _context.Count > 20000)
                throw new MemoryFileException("SCAN_LIMIT");
            var number = line;
            foreach (var part in body.Split('\n'))
            {
                var (excerpt, truncated) = Clip(part.TrimEnd('\r'), 0);
                _context[(path, number)] = new(path, number, excerpt, truncated);
                number++;
            }
        }

        if (match)
        {
            var index = spans.Count == 0 ? 0 : Encoding.UTF8.GetCharCount(utf8.AsSpan(0, spans[0].StartByte));
            var (excerpt, truncated) = Clip(body.TrimEnd('\r'), index);
            _hits.Add(new(path, line, last, excerpt, truncated, spans.ToArray()));
        }
    }

    private static (string Text, bool Truncated) Clip(string text, int index)
    {
        const int limit = 384;
        if (text.Length <= limit)
            return (text, false);
        var start = Math.Clamp(index - 48, 0, text.Length - limit);
        if (char.IsLowSurrogate(text[start]))
        {
            start++;
        }

        var end = Math.Min(text.Length, start + limit);
        if (end < text.Length && char.IsLowSurrogate(text[end]))
        {
            end--;
        }

        return (text[start..end], true);
    }

    #endregion

    /// <summary>
    /// Tracks the begin, match, end and summary sequence for one process.
    /// </summary>
    /// <param name="owner">Collector shared by this request.</param>
    /// <param name="paths">Authorized relative paths.</param>
    internal sealed class Batch(RipgrepResults owner, IEnumerable<string> paths)
    {
        #region Static Fields

        #endregion

        #region Private Fields

        private readonly HashSet<string> _allowed = new(paths, StringComparer.Ordinal);
        private string? _active;
        private bool _summary;
        private int _matches;
        private int _occurrences;
        private int _fileOccurrences;

        #endregion

        #region Constructors

        #endregion

        #region Properties

        #endregion

        #region Public Methods

        /// <summary>
        /// Validates one JSON record.
        /// </summary>
        /// <param name="json">One complete UTF-8 JSONL event.</param>
        internal void Accept(ReadOnlyMemory<byte> json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var type = root.GetProperty("type").GetString();
                var data = root.GetProperty("data");
                if (_summary)
                    throw Invalid();
                if (type == "summary")
                {
                    if (_active is not null || data.GetProperty("stats").GetProperty("searches").GetInt32() < 0
                        || data.GetProperty("stats").GetProperty("matches").GetInt32() != _occurrences)
                        throw Invalid();
                    _summary = true;
                    return;
                }

                var path = Text(data.GetProperty("path"));
                // Ripgrep reports native separators on Windows; the allowlist uses '/' selectors.
                if (OperatingSystem.IsWindows())
                {
                    path = path.Replace('\\', '/');
                }
                if (type == "begin")
                {
                    if (_active is not null || !_allowed.Remove(path) || !MemoryFileStore.ValidPath(path))
                        throw Invalid();
                    _active = path;
                    _fileOccurrences = 0;
                    return;
                }

                if (_active != path)
                    throw Invalid();
                if (type == "end")
                {
                    if (data.GetProperty("binary_offset").ValueKind != JsonValueKind.Null
                        || data.GetProperty("stats").GetProperty("matches").GetInt32() != _fileOccurrences)
                        throw Invalid();
                    _active = null;
                    return;
                }

                if (type is not ("context" or "match"))
                    throw Invalid();
                owner.Add(path, data, type == "match");
                if (type == "match")
                {
                    _matches++;
                    var occurrences = data.GetProperty("submatches").GetArrayLength();
                    _fileOccurrences += occurrences;
                    _occurrences += occurrences;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException)
            {
                throw Invalid();
            }
        }

        /// <summary>
        /// Checks protocol completion against process exit.
        /// </summary>
        /// <param name="exit">Ripgrep exit status.</param>
        internal void Complete(int exit)
        {
            if (!_summary || (exit == 0) != (_matches > 0))
                throw Invalid();
        }

        #endregion

        #region Private Methods

        #endregion
    }
}
