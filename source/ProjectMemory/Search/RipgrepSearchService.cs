namespace ProjectMemory.Search;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ProjectMemory.MemoryFiles;
using ProjectMemory.Project;
using ProjectMemory.Tools;

/// <summary>Admits bounded searches and maps validated requests to ripgrep arguments.</summary>
internal sealed class RipgrepSearchService : IDisposable
{
    #region Static Fields

    /// <summary>Strict decoder for the ripgrep JSONL protocol.</summary>
    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static readonly Version MinimumRipgrepVersion = new(15, 2, 0);

    #endregion

    #region Private Fields

    private readonly SemaphoreSlim _slots = new(4, 4);
    private readonly MemoryManager _manager;
    private readonly MemoryFileStore _store;
    private readonly string _executable;
    private readonly TimeSpan _timeout;

    #endregion

    #region Constructors

    /// <summary>
    /// Creates the production search service.
    /// </summary>
    /// <param name="manager">Revalidates project authorization under reader admission.</param>
    /// <param name="store">Managed storage boundary.</param>
    public RipgrepSearchService(MemoryManager manager, MemoryFileStore store)
        : this(manager, store, DefaultExecutable(), TimeSpan.FromSeconds(10)) { }

    /// <summary>
    /// Creates an adapter with a trusted executable and deadline.
    /// </summary>
    /// <param name="manager">Revalidates project authorization under reader admission.</param>
    /// <param name="store">Managed storage boundary.</param>
    /// <param name="executable">Trusted executable name or path for isolated process tests.</param>
    /// <param name="timeout">Maximum admission and search duration.</param>
    internal RipgrepSearchService(MemoryManager manager, MemoryFileStore store, string executable, TimeSpan timeout)
    {
        _manager = manager;
        _store = store;
        _executable = executable;
        _timeout = timeout;
    }

    #endregion

    #region Properties

    #endregion

    #region Public Methods

    /// <summary>
    /// Executes one admitted project search.
    /// </summary>
    /// <param name="context">Exact context selector.</param>
    /// <param name="request">Typed search options.</param>
    /// <param name="token">Caller cancellation.</param>
    /// <returns>Versioned search result or routing failure.</returns>
    internal async Task<AdministrationResponse<MemorySearchResult>> RunAsync(string context, SearchRequest request, CancellationToken token)
    {
        request.Validate();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_timeout);
        try
        {
            await _slots.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                return await _manager.RunOperationAsync(context, ProjectAccess.Read,
                    id => SearchAsync(_store, id, request, deadline.Token, _executable), deadline.Token).ConfigureAwait(false);
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new MemoryFileException("SEARCH_TIMEOUT");
        }
    }

    /// <summary>
    /// Uses the installed ripgrep command from the process search path on every platform.
    /// </summary>
    /// <returns>The ripgrep executable name.</returns>
    internal static string DefaultExecutable() => "rg";

    /// <summary>
    /// Checks that the installed ripgrep version meets the supported minimum.
    /// </summary>
    /// <param name="line">The first version line from ripgrep.</param>
    /// <returns>Whether the reported version is supported.</returns>
    internal static bool SupportsVersion(string line)
    {
        if (!line.StartsWith("ripgrep ", StringComparison.Ordinal))
            return false;
        var number = line.AsSpan("ripgrep ".Length).Trim();
        var end = number.IndexOfAny(' ', '\t', '\r');
        if (end >= 0)
            number = number[..end];
        return Version.TryParse(number, out var version) && version != null! && version.CompareTo(MinimumRipgrepVersion) >= 0;
    }

    /// <summary>
    /// Requires a supported ripgrep version and PCRE2 before host readiness.
    /// </summary>
    /// <param name="token">Startup cancellation.</param>
    /// <returns>The asynchronous capability check.</returns>
    internal async Task VerifyBinaryAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var directory = AppContext.BaseDirectory;
        try
        {
            var supported = false;
            var versionExit = await new RipgrepProcess(_executable).RunAsync(directory, ["--no-config", "--version"], 10,
                line => supported |= SupportsVersion(StrictUtf8.GetString(line.Span)), deadline.Token).ConfigureAwait(false);
            if (versionExit != 0 || !supported)
                throw new MemoryFileException("SEARCH_UNAVAILABLE");

            var verified = false;
            var pcreExit = await new RipgrepProcess(_executable).RunAsync(directory, ["--no-config", "--pcre2-version"], 10,
                line => verified |= StrictUtf8.GetString(line.Span).StartsWith("PCRE2 ", StringComparison.Ordinal), deadline.Token).ConfigureAwait(false);
            if (pcreExit != 0 || !verified)
                throw new MemoryFileException("SEARCH_UNAVAILABLE");
        }
        catch (MemoryFileException error) when (error.Code != "SEARCH_UNAVAILABLE")
        {
            // Startup capability failures should all produce the same actionable diagnostic.
            throw new MemoryFileException("SEARCH_UNAVAILABLE");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new MemoryFileException("SEARCH_UNAVAILABLE");
        }
    }

    /// <summary>
    /// Searches a revalidated UUID under the caller’s shared project lease.
    /// </summary>
    /// <param name="store">Managed selection and content eligibility.</param>
    /// <param name="id">Server-owned project ID.</param>
    /// <param name="request">Validated search options.</param>
    /// <param name="token">Search deadline or cancellation.</param>
    /// <param name="executable">Optional trusted executable name or path.</param>
    /// <returns>The complete projected page.</returns>
    internal static async Task<MemorySearchResult> SearchAsync(MemoryFileStore store, Guid id, SearchRequest request, CancellationToken token, string? executable = null)
    {
        var (directory, paths) = store.SelectSearchFiles(id, request, token);
        var process = new RipgrepProcess(executable ?? DefaultExecutable());
        var filters = request.Files;
        var flagsForEmpty = new List<string>();
        if (filters.Include?.Length > 0 || filters.Exclude?.Length > 0)
        {
            var globs = (filters.Include ?? []).Concat((filters.Exclude ?? []).Select(g => "!" + g));
            var arguments = globs.SelectMany(g => new[] { "--glob", g });
            if (paths.Length == 0)
            {
                // Validate glob syntax without traversing a lazy or empty root.
                flagsForEmpty.AddRange(arguments);
            }
            else
            {
                paths = await FilterAsync(arguments).ConfigureAwait(false);
            }
        }

        if (paths.Length > 0 && filters.RespectIgnoreFiles)
        {
            var ignores = new List<string>();
            foreach (var name in new[] { ".gitignore", ".ignore", ".rgignore" })
            {
                var info = new FileInfo(Path.Combine(directory, name));
                if (info.LinkTarget is not null)
                    throw new MemoryFileException("UNSAFE_PATH");
                if (info.Exists)
                {
                    if (info.Length > 65536)
                        throw new MemoryFileException("SCAN_LIMIT");
                    ignores.Add("--ignore-file");
                    ignores.Add(name);
                }
            }

            if (ignores.Count > 0)
            {
                paths = await FilterAsync(ignores).ConfigureAwait(false);
            }
        }

        var eligible = new List<string>();
        long bytes = 0;
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            if (store.EligibleForSearch(Path.Combine(directory, path), ref bytes))
            {
                eligible.Add(path);
            }
        }

        var result = new RipgrepResults(request);
        var flags = MatchArguments(request);
        flags.AddRange(flagsForEmpty);
        flags.Add("--");
        // Empty stdin still compiles the pattern for an empty selection.
        foreach (var batch in eligible.Chunk(48).DefaultIfEmpty([]))
        {
            var events = result.BeginBatch(batch);
            var exit = await process.RunAsync(directory, flags.Concat(batch.Length == 0 ? ["-"] : batch), 10, events.Accept, token).ConfigureAwait(false);
            events.Complete(exit);
        }

        var page = result.Page(token);
        token.ThrowIfCancellationRequested();
        return page;

        async Task<string[]> FilterAsync(IEnumerable<string> options)
        {
            var selected = new HashSet<string>(StringComparer.Ordinal);
            string[] common = ["--no-config", "--no-ignore", "--no-follow", "--one-file-system", "--threads=1", "--files", "--null",
                filters.IncludeHidden ? "--hidden" : "--no-hidden", "--max-depth", filters.MaxDepth.ToString(CultureInfo.InvariantCulture)];
            await process.RunAsync(directory, common.Concat(options).Concat(["--", "."]), 0, line =>
            {
                // An unrepresentable directory entry cannot be an addressable memory.
                string path;
                try { path = StrictUtf8.GetString(line.Span); }
                catch (DecoderFallbackException) { return; }
                // File-list output uses native separators on Windows; selectors always use '/'.
                if (OperatingSystem.IsWindows())
                    path = path.Replace('\\', '/');
                selected.Add(path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path);
            }, token).ConfigureAwait(false);
            return paths.Where(selected.Contains).ToArray();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _slots.Dispose();

    #endregion

    #region Private Methods

    private static List<string> MatchArguments(SearchRequest request)
    {
        var m = request.Match;
        var args = new List<string> { "--json", "--no-config", "--no-ignore", "--no-follow", "--threads=1", "--max-filesize=1M",
            "--regex-size-limit=4M", "--dfa-size-limit=4M", "--encoding=utf-8", "--crlf", "--engine", m.Engine,
            m.Case switch { "ignore" => "-i", "smart" => "-S", _ => "-s" } };
        if (m.Syntax == "literal")
        {
            args.Add("-F");
        }

        if (m.Boundary == "word")
        {
            args.Add("-w");
        }

        if (m.Boundary == "line")
        {
            args.Add("-x");
        }

        if (m.Invert)
        {
            args.Add("-v");
        }

        if (m.Multiline)
        {
            args.Add("-U");
        }

        args.AddRange(["-B", request.Output.Before.ToString(CultureInfo.InvariantCulture), "-A", request.Output.After.ToString(CultureInfo.InvariantCulture)]);
        foreach (var pattern in new[] { request.Query }.Concat(m.Alternatives ?? []))
        {
            args.Add("-e");
            args.Add(pattern);
        }

        return args;
    }

    #endregion
}
