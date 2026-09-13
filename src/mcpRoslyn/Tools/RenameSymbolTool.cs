using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using mcpRoslyn.Workspace;

namespace mcpRoslyn.Tools;

public sealed record RenameEdit(
    string FilePath,
    string OldText,
    string NewText,
    Contracts.SymbolLocation Location);

public sealed record RenameSymbolResult(
    IReadOnlyList<RenameEdit> Edits,
    IReadOnlyList<string>? Conflicts = null);

[McpServerToolType]
internal sealed class RenameSymbolTool(IWorkspaceService ws, ILogger<RenameSymbolTool> log)
    : ToolBase(ws, log)
{
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    /// <summary>
    /// The encoding to read and write a file back in: its byte-order mark — four-byte UTF-32 marks
    /// checked before the two-byte UTF-16 one they begin with — else UTF-8 without. Every decoder throws
    /// on invalid bytes: a replacement-character decode would make a legacy code page file look unchanged
    /// and then write it back corrupted. ponytail: such files are refused, not re-encoded; detect code
    /// pages if a solution ever needs renames across them.
    /// </summary>
    private static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes) => bytes switch
    {
        [0xFF, 0xFE, 0x00, 0x00, ..] => (new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: true), 4),
        [0x00, 0x00, 0xFE, 0xFF, ..] => (new UTF32Encoding(bigEndian: true, byteOrderMark: true, throwOnInvalidCharacters: true), 4),
        [0xEF, 0xBB, 0xBF, ..] => (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true), 3),
        [0xFF, 0xFE, ..] => (new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true), 2),
        [0xFE, 0xFF, ..] => (new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true), 2),
        _ => (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), 0),
    };

    [McpServerTool(Name = "rename_symbol")]
    [Description("Renames a symbol across the solution. By default returns a PREVIEW of edits without writing — set applyEdits=true to apply.")]
    public Task<Contracts.ToolResult<RenameSymbolResult>> InvokeAsync(
        string filePath, int line, int column,
        string newName,
        bool applyEdits = false,
        string format = "structured",
        CancellationToken ct = default)
        => ExecuteAsync(async ct2 =>
        {
            var solution = await Workspace.GetFreshSolutionAsync(ct2);
            var doc = RoslynHelpers.FindDocument(solution, filePath);
            if (doc is null)
                return Contracts.ToolResult<RenameSymbolResult>.Fail(
                    "FILE_NOT_IN_WORKSPACE", $"File not in workspace: {filePath}");

            var symbol = await RoslynHelpers.ResolveSymbolAtPositionAsync(doc, line, column, ct2);
            if (symbol is null)
                return Contracts.ToolResult<RenameSymbolResult>.Fail(
                    "SYMBOL_NOT_FOUND", $"No symbol at {filePath}:{line}:{column}");

            var newSolution = await Renamer.RenameSymbolAsync(
                solution, symbol, new SymbolRenameOptions(), newName, ct2);

            // Compute edits by diffing each document in the renamed solution against the original
            var edits = new List<RenameEdit>();
            foreach (var project in newSolution.Projects)
            {
                var oldProject = solution.GetProject(project.Id);
                if (oldProject is null) continue;

                foreach (var newDoc in project.Documents)
                {
                    var oldDoc = oldProject.GetDocument(newDoc.Id);
                    if (oldDoc is null || newDoc.FilePath is null) continue;

                    var oldText = await oldDoc.GetTextAsync(ct2);
                    // Not SourceText.GetTextChanges: Renamer rebuilds the tree rather than editing the
                    // text, so that returns one whole-file change — the preview carried every changed
                    // file twice, ~35k tokens for one interface rename on BPG (PERF-002). The document
                    // diff compares syntax trees and yields one change per occurrence.
                    var changes = await newDoc.GetTextChangesAsync(oldDoc, ct2);
                    foreach (var change in changes)
                    {
                        var oldSubstring = oldText.GetSubText(change.Span).ToString();
                        var line0 = oldText.Lines.GetLinePosition(change.Span.Start);
                        var endLine0 = oldText.Lines.GetLinePosition(change.Span.End);
                        var location = new Contracts.SymbolLocation(
                            FilePath: newDoc.FilePath,
                            Line: line0.Line + 1,
                            Column: line0.Character + 1,
                            EndLine: endLine0.Line + 1,
                            EndColumn: endLine0.Character + 1);
                        edits.Add(new RenameEdit(newDoc.FilePath, oldSubstring, change.NewText ?? "", location));
                    }
                }
            }

            if (applyEdits)
            {
                // TOOL-008: every changed file is opened exclusively and checked before any is written —
                // still the text this rename was computed from, valid in its encoding, writable — so a
                // rename lands whole or not at all (bar an IO failure during the writes themselves, see
                // PARTIAL_WRITE), never over an edit made after the workspace snapshot, and no other
                // program can save one of the files between the check and the write.
                var changedPaths = edits.Select(e => e.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var writes = new List<(string Path, string OldText, string NewText)>();
                var conflicts = new List<string>();
                foreach (var linked in newSolution.Projects
                             .SelectMany(p => p.Documents)
                             .Where(d => d.FilePath is not null && changedPaths.Contains(d.FilePath))
                             .GroupBy(d => d.FilePath!, StringComparer.OrdinalIgnoreCase))
                {
                    // A file linked into several projects is one document per project; when #if makes
                    // their renamed texts differ, no single write can honour all of them.
                    var texts = new List<(string Old, string New)>();
                    foreach (var newDoc in linked)
                        texts.Add(((await solution.GetDocument(newDoc.Id)!.GetTextAsync(ct2)).ToString(),
                                   (await newDoc.GetTextAsync(ct2)).ToString()));
                    if (texts.Select(t => t.New).Distinct().Count() > 1) { conflicts.Add(linked.Key); continue; }
                    writes.Add((linked.Key, texts[0].Old, texts[0].New));
                }
                if (conflicts.Count > 0)
                    return Contracts.ToolResult<RenameSymbolResult>.Fail("LINKED_FILE_CONFLICT",
                        $"Nothing written; linked into several projects whose renamed text differs: {string.Join(", ", conflicts)}",
                        "The file's #if branches rename differently per project; edit it by hand.");

                await WriteLock.WaitAsync(ct2); // two renames in this process must not check-then-write past each other
                var handles = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    // Every file's output bytes are produced during the checks, so nothing that can fail
                    // for a reason other than IO is left for the write loop.
                    var outputs = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                    List<string> notWritable = [], locked = [], undecodable = [], stale = [];
                    foreach (var (path, oldText, newText) in writes)
                    {
                        FileStream? stream = null;
                        try
                        {
                            // Inside the try: a file deleted since the snapshot — even between these two
                            // calls — surfaces as FileNotFound/DirectoryNotFound and reads as STALE_FILE.
                            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) { notWritable.Add(path); continue; }
                            // Held until the write: an editor saving meanwhile fails instead of being overwritten.
                            // Two short retries ride out a transient reader (an indexer, antivirus) holding it.
                            for (var attempt = 1; stream is null; attempt++)
                            {
                                try { stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                                catch (IOException ex) when (attempt < 3 && ex is not (FileNotFoundException or DirectoryNotFoundException))
                                {
                                    await Task.Delay(100, ct2);
                                }
                            }
                        }
                        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { stale.Add(path); continue; }
                        catch (UnauthorizedAccessException) { notWritable.Add(path); continue; }
                        catch (IOException) { locked.Add(path); continue; }
                        handles[path] = stream;

                        using var buffer = new MemoryStream();
                        await stream.CopyToAsync(buffer, ct2);
                        var bytes = buffer.ToArray();
                        var (encoding, preamble) = DetectEncoding(bytes);
                        string current;
                        try { current = encoding.GetString(bytes, preamble, bytes.Length - preamble); }
                        catch (DecoderFallbackException) { undecodable.Add(path); continue; }
                        if (current != oldText) { stale.Add(path); continue; }
                        try { outputs[path] = [.. encoding.GetPreamble(), .. encoding.GetBytes(newText)]; }
                        catch (EncoderFallbackException) { undecodable.Add(path); }
                    }
                    if (notWritable.Count > 0)
                        return Contracts.ToolResult<RenameSymbolResult>.Fail("FILE_READ_ONLY",
                            $"Nothing written; read-only or not writable: {string.Join(", ", notWritable)}");
                    if (locked.Count > 0)
                        return Contracts.ToolResult<RenameSymbolResult>.Fail("FILE_LOCKED",
                            $"Nothing written; held open by another program: {string.Join(", ", locked)}",
                            "Close the file elsewhere, then call rename_symbol again.");
                    if (undecodable.Count > 0)
                        return Contracts.ToolResult<RenameSymbolResult>.Fail("UNSUPPORTED_ENCODING",
                            $"Nothing written; not valid in the encoding its byte-order mark declares (UTF-8 when it has none) — a legacy code page?: {string.Join(", ", undecodable)}");
                    if (stale.Count > 0)
                        return Contracts.ToolResult<RenameSymbolResult>.Fail("STALE_FILE",
                            $"Nothing written; changed on disk since the workspace snapshot: {string.Join(", ", stale)}",
                            "Call reload_workspace, then rename_symbol again.");

                    // The apply phase is not cancellable: stopping between files is the half-applied
                    // rename this exists to prevent. There is no rollback, so an IO failure here — the one
                    // way left to land part of a rename — is reported with exactly what was written.
                    var written = new List<string>();
                    foreach (var (path, _, _) in writes)
                    {
                        try
                        {
                            var stream = handles[path];
                            stream.SetLength(0);
                            await stream.WriteAsync(outputs[path], CancellationToken.None);
                            await stream.FlushAsync(CancellationToken.None);
                            written.Add(path);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            return Contracts.ToolResult<RenameSymbolResult>.Fail("PARTIAL_WRITE",
                                $"Writing {path} failed after {written.Count} of {writes.Count} files were written " +
                                $"({string.Join(", ", written)}); {path} itself may now be empty or incomplete: {ex.Message}");
                        }
                    }
                }
                finally
                {
                    try
                    {
                        foreach (var stream in handles.Values)
                        {
                            // Disposal retries a flush that already failed; nothing it throws may skip the
                            // remaining handles or strand the lock for every later rename.
                            try { await stream.DisposeAsync(); }
                            catch (Exception) { }
                        }
                    }
                    finally { WriteLock.Release(); }
                }
            }

            var result = new RenameSymbolResult(edits, Conflicts: null);
            if (string.Equals(format, "summary", StringComparison.OrdinalIgnoreCase))
            {
                var desc = applyEdits ? $"renamed {result.Edits.Count} occurrences" : $"preview: {result.Edits.Count} occurrences";
                return Contracts.ToolResult<RenameSymbolResult>.OkSummary(desc);
            }
            return Contracts.ToolResult<RenameSymbolResult>.Ok(result);
        }, ct);
}
