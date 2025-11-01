namespace RedMist.EventOrchestration.Models;

public record ContainerDetails(
    string ContainerName, 
    string Version, 
    string JobFormat, 
    bool IsService = false,
    string CpuRequest = "50m",
    string MemoryRequest = "128Mi",
    string CpuLimit = "150m",
    string MemoryLimit = "250Mi")
{
    public string ImageName => $"{ContainerName.ToLowerInvariant()}:{Version}";
}
