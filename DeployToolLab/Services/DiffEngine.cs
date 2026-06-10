using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace DeployTool.Services;

public class DiffEngine
{
    private static readonly Differ _differ = new();
    private static readonly InlineDiffBuilder _inline = new(_differ);
    private static readonly SideBySideDiffBuilder _sideBySide = new(_differ);

    public SideBySideDiffModel ComputeSideBySideDiff(string oldText, string newText)
        => _sideBySide.BuildDiffModel(oldText, newText, ignoreWhitespace: false);

    public async Task<SideBySideDiffModel> ComputeFileDiffAsync(string? oldFilePath, string? newFilePath)
    {
        var oldText = oldFilePath is not null && File.Exists(oldFilePath)
            ? await ReadTextAsync(oldFilePath)
            : string.Empty;
        var newText = newFilePath is not null && File.Exists(newFilePath)
            ? await ReadTextAsync(newFilePath)
            : string.Empty;

        return ComputeSideBySideDiff(oldText, newText);
    }

    public async Task<(SideBySideDiffModel Diff, string OldText, string NewText)> ComputeFileDiffWithTextAsync(
        string? oldFilePath, string? newFilePath)
    {
        var oldText = oldFilePath is not null && File.Exists(oldFilePath)
            ? await ReadTextAsync(oldFilePath)
            : string.Empty;
        var newText = newFilePath is not null && File.Exists(newFilePath)
            ? await ReadTextAsync(newFilePath)
            : string.Empty;

        return (ComputeSideBySideDiff(oldText, newText), oldText, newText);
    }

    private static async Task<string> ReadTextAsync(string path)
    {
        try { return await File.ReadAllTextAsync(path); }
        catch { return string.Empty; }
    }
}
