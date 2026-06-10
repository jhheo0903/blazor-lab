using DeployTool.Models;

namespace DeployTool.Components;

public class FileTreeNodeModel
{
    public FileChangeItem? Item { get; init; }
    public string? FolderName { get; init; }
    public List<FileTreeNodeModel> Children { get; } = [];

    public bool HasChanges =>
        Item?.Status is not (null or FileChangeStatus.Identical) ||
        Children.Any(c => c.HasChanges);

    public static List<FileTreeNodeModel> BuildTree(IEnumerable<FileChangeItem> items)
    {
        // Single-pass O(N) build using persistent child dictionaries
        var root = new FolderBucket();

        foreach (var item in items)
        {
            var parts = item.RelativePath.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

            root.Insert(parts, 0, item);
        }

        return root.ToNodes();
    }

    private sealed class FolderBucket
    {
        private readonly Dictionary<string, FolderBucket> _folders =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly List<FileChangeItem> _files = [];

        public void Insert(string[] parts, int depth, FileChangeItem item)
        {
            if (depth == parts.Length - 1)
            {
                _files.Add(item);
                return;
            }

            var folder = parts[depth];
            if (!_folders.TryGetValue(folder, out var child))
            {
                child = new FolderBucket();
                _folders[folder] = child;
            }

            child.Insert(parts, depth + 1, item);
        }

        public List<FileTreeNodeModel> ToNodes()
        {
            var result = new List<FileTreeNodeModel>(
                _folders.Count + _files.Count);

            foreach (var (name, bucket) in _folders.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                var node = new FileTreeNodeModel { FolderName = name };
                node.Children.AddRange(bucket.ToNodes());
                result.Add(node);
            }

            foreach (var file in _files.OrderBy(f => Path.GetFileName(f.RelativePath), StringComparer.OrdinalIgnoreCase))
                result.Add(new FileTreeNodeModel { Item = file });

            return result;
        }
    }
}
