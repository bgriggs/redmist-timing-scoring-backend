namespace RedMist.EventOrchestration.Models;

public record ContainerDetails(string ContainerName, string Version, string JobFormat, bool IsService = false)
{
    public string ImageName => $"{ContainerName.ToLowerInvariant()}:{Version}";
}
