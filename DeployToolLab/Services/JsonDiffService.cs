using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeployTool.Services;

public enum JsonKeyStatus
{
    Same,
    OnlyInProduction,
    OnlyInDeploy,
    ValueChanged
}

// Per-key merge decision
public enum JsonKeyDecision
{
    UseProduction,   // keep the production value / keep it absent
    UseDeploy        // take the deploy value / add the new key
}

public class JsonKeyDiff
{
    public string KeyPath { get; init; } = string.Empty;
    public JsonKeyStatus Status { get; init; }
    public string? ProductionValue { get; init; }
    public string? DeployValue { get; init; }
    public bool IsSensitive { get; init; }

    // Default: OnlyInDeploy → UseDeploy (accept new keys by default)
    //          ValueChanged  → UseDeploy (take deploy value by default)
    //          OnlyInProd    → UseProduction (keep existing by default)
    public JsonKeyDecision Decision { get; set; }
}

public class JsonDiffService
{
    private static readonly string[] SensitivePatterns =
        ["connectionstring", "password", "secret", "apikey", "token"];

    public async Task<List<JsonKeyDiff>> ComputeJsonDiffAsync(
        string? productionPath, string? deployPath)
    {
        var prodDict = await ParseJsonFlatAsync(productionPath);
        var deployDict = await ParseJsonFlatAsync(deployPath);

        var result = new List<JsonKeyDiff>();
        var allKeys = prodDict.Keys.Union(deployDict.Keys, StringComparer.OrdinalIgnoreCase);

        foreach (var key in allKeys)
        {
            var isSensitive = SensitivePatterns.Any(p =>
                key.Contains(p, StringComparison.OrdinalIgnoreCase));

            var inProd = prodDict.TryGetValue(key, out var prodVal);
            var inDeploy = deployDict.TryGetValue(key, out var deployVal);

            var status = (inProd, inDeploy) switch
            {
                (true, false) => JsonKeyStatus.OnlyInProduction,
                (false, true) => JsonKeyStatus.OnlyInDeploy,
                _ => prodVal == deployVal ? JsonKeyStatus.Same : JsonKeyStatus.ValueChanged
            };

            if (status != JsonKeyStatus.Same)
            {
                var defaultDecision = status == JsonKeyStatus.OnlyInProduction
                    ? JsonKeyDecision.UseProduction
                    : JsonKeyDecision.UseDeploy;

                result.Add(new JsonKeyDiff
                {
                    KeyPath = key,
                    Status = status,
                    ProductionValue = isSensitive ? "***" : prodVal,
                    DeployValue = isSensitive ? "***" : deployVal,
                    IsSensitive = isSensitive,
                    Decision = defaultDecision
                });
            }
        }

        return result.OrderBy(d => d.KeyPath).ToList();
    }

    public async Task<string> BuildMergedJsonAsync(
        string? productionPath, string? deployPath, List<JsonKeyDiff> decisions)
    {
        var prodText = productionPath != null && File.Exists(productionPath)
            ? await File.ReadAllTextAsync(productionPath) : "{}";
        var deployText = deployPath != null && File.Exists(deployPath)
            ? await File.ReadAllTextAsync(deployPath) : "{}";

        JsonNode root;
        try { root = JsonNode.Parse(prodText) ?? new JsonObject(); }
        catch { root = new JsonObject(); }

        JsonNode deployRoot;
        try { deployRoot = JsonNode.Parse(deployText) ?? new JsonObject(); }
        catch { deployRoot = new JsonObject(); }

        foreach (var diff in decisions)
        {
            switch (diff.Decision)
            {
                case JsonKeyDecision.UseDeploy:
                    var deployValue = GetNodeByPath(deployRoot, diff.KeyPath);
                    if (deployValue != null)
                        SetNodeByPath(root, diff.KeyPath, deployValue.DeepClone());
                    else
                        RemoveNodeByPath(root, diff.KeyPath);
                    break;

                case JsonKeyDecision.UseProduction:
                    if (diff.Status == JsonKeyStatus.OnlyInDeploy)
                        RemoveNodeByPath(root, diff.KeyPath);
                    // ValueChanged or OnlyInProduction: keep production value (already in root)
                    break;
            }
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonNode? GetNodeByPath(JsonNode root, string keyPath)
    {
        var parts = SplitKeyPath(keyPath);
        JsonNode? current = root;
        foreach (var part in parts)
        {
            if (current is JsonObject obj && obj.TryGetPropertyValue(part, out var child))
                current = child;
            else
                return null;
        }
        return current;
    }

    private static void SetNodeByPath(JsonNode root, string keyPath, JsonNode value)
    {
        var parts = SplitKeyPath(keyPath);
        JsonNode current = root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (current is JsonObject obj)
            {
                if (!obj.TryGetPropertyValue(parts[i], out var child) || child is not JsonObject)
                {
                    var newObj = new JsonObject();
                    obj[parts[i]] = newObj;
                    current = newObj;
                }
                else
                    current = child!;
            }
        }
        if (current is JsonObject target)
            target[parts[^1]] = value;
    }

    private static void RemoveNodeByPath(JsonNode root, string keyPath)
    {
        var parts = SplitKeyPath(keyPath);
        JsonNode? current = root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (current is JsonObject obj && obj.TryGetPropertyValue(parts[i], out var child))
                current = child;
            else
                return;
        }
        if (current is JsonObject parent)
            parent.Remove(parts[^1]);
    }

    private static string[] SplitKeyPath(string keyPath)
        => keyPath.Split(':', StringSplitOptions.RemoveEmptyEntries);

    private static async Task<Dictionary<string, string>> ParseJsonFlatAsync(string? path)
    {
        if (path is null || !File.Exists(path))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var text = await File.ReadAllTextAsync(path);
            var doc = JsonDocument.Parse(text);
            return FlattenJson(doc.RootElement, string.Empty);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, string> FlattenJson(JsonElement element, string prefix)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    var key = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}:{prop.Name}";
                    foreach (var (k, v) in FlattenJson(prop.Value, key))
                        result[k] = v;
                }
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var (k, v) in FlattenJson(item, $"{prefix}[{i}]"))
                        result[k] = v;
                    i++;
                }
                break;
            default:
                result[prefix] = element.ToString();
                break;
        }

        return result;
    }
}
