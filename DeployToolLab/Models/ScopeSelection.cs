namespace DeployTool.Models;

public enum ScopeMode
{
    FullCompare,
    ProjectSelect
}

public class FolderNode
{
    public string Name { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;  // 루트 기준 상대 경로
    public int Depth { get; init; }
    public bool IsSelected { get; set; } = true;
    public bool IsExpanded { get; set; }
    public bool IsNewInDeploy { get; init; }
    public bool IsOnlyInProduction { get; init; }
    public int EstimatedFileCount { get; init; }
    public List<FolderNode> Children { get; init; } = [];

    public string Badge => (IsNewInDeploy, IsOnlyInProduction) switch
    {
        (true, _) => "🆕",
        (_, true) => "🗑",
        _ => string.Empty
    };

    // 이 노드와 모든 하위 노드를 평탄화
    public IEnumerable<FolderNode> Flatten()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var node in child.Flatten())
                yield return node;
    }

    // 선택된 리프 노드(하위가 없거나 하위가 모두 비선택)의 상대 경로
    public IEnumerable<string> SelectedRelativePaths()
    {
        if (Children.Count == 0)
        {
            if (IsSelected) yield return RelativePath;
            yield break;
        }
        foreach (var child in Children)
            foreach (var path in child.SelectedRelativePaths())
                yield return path;
    }
}

public class ScopeSelection
{
    public ScopeMode Mode { get; set; } = ScopeMode.FullCompare;
    public List<FolderNode> FolderTree { get; set; } = [];   // 트리 루트 노드들
    public List<string> ExcludePatterns { get; set; } = ["Backup/", "_quarantine/"];

    public int EstimatedTotalFiles =>
        Mode == ScopeMode.FullCompare
            ? FolderTree.Sum(f => f.Flatten().Sum(n => n.Children.Count == 0 ? n.EstimatedFileCount : 0))
            : FolderTree.SelectMany(f => f.Flatten())
                        .Where(n => n.IsSelected && n.Children.Count == 0)
                        .Sum(n => n.EstimatedFileCount);
}
