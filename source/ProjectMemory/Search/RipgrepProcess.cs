namespace ProjectMemory.Search;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ProjectMemory.MemoryFiles;

/// <summary>Runs the installed read-only ripgrep with bounded, concurrent pipe readers.</summary>
/// <param name="executable">Trusted process path, fixed at composition time.</param>
internal sealed class RipgrepProcess(string executable = "/usr/bin/rg")
{
    #region Static Fields

    private const int MaxEventBytes = 256 * 1024;
    private const int MaxStdout = 4 * 1024 * 1024;
    private const int MaxStderr = 32 * 1024;

    #endregion

    #region Private Fields

    private int _bytes;
    private int _events;

    #endregion

    #region Constructors

    #endregion

    #region Properties

    #endregion

    #region Public Methods

    /// <summary>Executes one invocation within this search's cumulative output budget.</summary>
    /// <param name="directory">Validated working directory.</param>
    /// <param name="args">Server-constructed argument vector.</param>
    /// <param name="delimiter">Record separator for the selected output protocol.</param>
    /// <param name="record">Consumes each bounded record before its buffer is reused.</param>
    /// <param name="token">Request or deadline cancellation.</param>
    /// <returns>Successful match or no-match exit status.</returns>
    internal async Task<int> RunAsync(string directory, IEnumerable<string> args, byte delimiter, Action<ReadOnlyMemory<byte>> record, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = true
        };
        start.Environment.Clear();
        start.Environment["LC_ALL"] = "C";
        foreach (var argument in args)
        {
            start.ArgumentList.Add(argument);
        }

        using var child = new Process { StartInfo = start };
        try
        {
            child.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new MemoryFileException("SEARCH_UNAVAILABLE");
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var onStop = stop.Token.Register(() =>
        {
            try
            {
                child.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { /* Already exited. */ }
        });
        using var errors = new MemoryStream();
        async Task DrainAsync(Stream pipe, bool stdout)
        {
            var buffer = new byte[8192];
            using var line = new MemoryStream();
            try
            {
                int count;
                while ((count = await pipe.ReadAsync(buffer, stop.Token).ConfigureAwait(false)) != 0)
                {
                    if (!stdout)
                    {
                        if (errors.Length + count > MaxStderr)
                            throw new MemoryFileException("SCAN_LIMIT");
                        errors.Write(buffer, 0, count);
                        continue;
                    }

                    _bytes += count;
                    if (_bytes > MaxStdout)
                        throw new MemoryFileException("SCAN_LIMIT");
                    for (var offset = 0; offset < count;)
                    {
                        var end = Array.IndexOf(buffer, delimiter, offset, count - offset);
                        var size = (end < 0 ? count : end) - offset;
                        if (line.Length + size > MaxEventBytes)
                            throw new MemoryFileException("SCAN_LIMIT");
                        line.Write(buffer, offset, size);
                        offset += size;
                        if (end >= 0)
                        {
                            if (++_events > 20000)
                                throw new MemoryFileException("SCAN_LIMIT");
                            record(line.GetBuffer().AsMemory(0, (int)line.Length));
                            line.SetLength(0);
                            offset++;
                        }
                    }
                }

                if (line.Length != 0)
                    throw new MemoryFileException("SEARCH_PROTOCOL");
            }
            catch
            {
                await stop.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }

        try
        {
            child.StandardInput.Close();
            var drains = Task.WhenAll(DrainAsync(child.StandardOutput.BaseStream, true), DrainAsync(child.StandardError.BaseStream, false));
            try
            {
                await drains.ConfigureAwait(false);
                await child.WaitForExitAsync(token).ConfigureAwait(false);
            }
            catch
            {
                token.ThrowIfCancellationRequested();
                if (drains.Exception?.Flatten().InnerExceptions.OfType<MemoryFileException>().FirstOrDefault() is { } fault)
                    throw fault;
                throw;
            }

            if (child.ExitCode is 0 or 1 && errors.Length == 0)
                return child.ExitCode;
            var diagnostic = Encoding.UTF8.GetString(errors.ToArray());
            if (diagnostic.Contains("regex parse error", StringComparison.OrdinalIgnoreCase)
                || diagnostic.Contains("PCRE2: error compiling pattern", StringComparison.OrdinalIgnoreCase)
                || diagnostic.Contains("error parsing glob", StringComparison.OrdinalIgnoreCase))
                throw new MemoryFileException("INVALID_PATTERN");
            throw new MemoryFileException("SEARCH_FAILED");
        }
        catch (IOException ex) when (ex is not MemoryFileException)
        {
            throw new MemoryFileException("SEARCH_IO");
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await child.WaitForExitAsync().ConfigureAwait(false);
        }
    }

    #endregion

    #region Private Methods

    #endregion
}
