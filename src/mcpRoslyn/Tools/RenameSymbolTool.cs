using System.ComponentModel;
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
    [McpServerTool(Name = "rename_symbol")]
    [Description("Renames a symbol across the solution. By default returns a PREVIEW of edits without writing — set applyEdits=true to apply.")]
    public Task<Contracts.ToolResult<RenameSymbolResult>> InvokeAsync(
        string filePath, int line, int column,
        string newName,
        bool applyEdits = false,
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
                    var newText = await newDoc.GetTextAsync(ct2);
                    var changes = newText.GetTextChanges(oldText);
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
                // Group edits by file and write the full new document text for each changed file
                var changedFilePaths = edits.Select(e => e.FilePath).Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var changedPath in changedFilePaths)
                {
                    var newDoc = newSolution.Projects
                        .SelectMany(p => p.Documents)
                        .First(d => string.Equals(d.FilePath, changedPath, StringComparison.OrdinalIgnoreCase));
                    var text = await newDoc.GetTextAsync(ct2);
                    await File.WriteAllTextAsync(changedPath, text.ToString(), ct2);
                }
            }

            return Contracts.ToolResult<RenameSymbolResult>.Ok(new RenameSymbolResult(edits, Conflicts: null));
        }, ct);
}
