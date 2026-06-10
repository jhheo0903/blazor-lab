using DiffPlex.DiffBuilder.Model;

namespace DeployTool.Services;

public class XmlDiffService(DiffEngine diffEngine)
{
    public async Task<SideBySideDiffModel> ComputeXmlDiffAsync(string? productionPath, string? deployPath)
        => await diffEngine.ComputeFileDiffAsync(productionPath, deployPath);
}
