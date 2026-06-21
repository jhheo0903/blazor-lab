using System.Text.Json;
using DeployTool.Models;

namespace DeployTool.Services;

public class DeployConfigService
{
    private static readonly string ConfigPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "deploy-config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private DeployConfig? _cached;

    public async Task<DeployConfig> LoadAsync()
    {
        if (_cached is not null) return _cached;
        try
        {
            if (!File.Exists(ConfigPath)) return _cached = new();
            var json = await File.ReadAllTextAsync(ConfigPath);
            return _cached = JsonSerializer.Deserialize<DeployConfig>(json, JsonOpts) ?? new();
        }
        catch
        {
            return _cached = new();
        }
    }

    public string ConfigFilePath => ConfigPath;

    public void Reload() => _cached = null;
}
