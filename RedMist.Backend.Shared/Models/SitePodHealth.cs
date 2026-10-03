using System.Text.Json;

namespace RedMist.Backend.Shared.Models;

/// <summary>
/// Every pod in the orchestrator's namespace as the orchestrator last saw it, for the site operations
/// page. Stored as JSON under <see cref="Consts.SITE_POD_HEALTH"/>.
/// </summary>
/// <remarks>
/// <para>
/// Written by the orchestrator, because it is the only service with permission to list pods, and
/// read by event management, which has none. Redis is the hand-off, so the page needs no new
/// Kubernetes permissions anywhere.
/// </para>
/// <para>
/// Plain JSON rather than MessagePack, and kept out of RedMist.TimingCommon, because nothing outside
/// this repository reads it. Writer and reader both go through <see cref="JsonOptions"/>, so the two
/// cannot drift onto different property casing.
/// </para>
/// </remarks>
public class SitePodHealth
{
    /// <summary>The options the orchestrator writes this with and event management reads it with.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>When the pods were listed.</summary>
    public DateTime AsOfUtc { get; set; }

    /// <summary>Every pod in the namespace, event pods and shared services alike.</summary>
    public List<PodHealth> Pods { get; set; } = [];

    /// <summary>
    /// Event jobs that have no pod at all - created but never scheduled, or whose pod has been
    /// deleted out from under them. A job in this state shows up nowhere in <see cref="Pods"/>, which
    /// is exactly why it is listed separately.
    /// </summary>
    public List<MissingJob> MissingJobs { get; set; } = [];
}

/// <summary>One pod's health, reduced to what an operator needs to tell healthy from not.</summary>
public class PodHealth
{
    public string PodName { get; set; } = string.Empty;

    public string? Namespace { get; set; }

    /// <summary>The event the pod serves, from its <c>event_id</c> label, or null for a shared service.</summary>
    public int? EventId { get; set; }

    /// <summary>The organization the pod serves, from its <c>organization_id</c> label.</summary>
    public int? OrganizationId { get; set; }

    /// <summary>
    /// What the pod is: its <c>app</c> label, else its <c>app.kubernetes.io/name</c> label, else the
    /// name of whatever owns it. For an event pod this is the job name, which names the role.
    /// </summary>
    public string? AppName { get; set; }

    /// <summary>The first container's image name without registry or tag, e.g. redmist-event-processor.</summary>
    public string? ServiceName { get; set; }

    /// <summary>The pod phase: Pending, Running, Succeeded, Failed or Unknown.</summary>
    public string Phase { get; set; } = "Unknown";

    /// <summary>Whether every container is ready. False for a pod with no container statuses yet.</summary>
    public bool Ready { get; set; }

    public int ReadyContainers { get; set; }

    public int TotalContainers { get; set; }

    /// <summary>Restarts summed across the pod's containers.</summary>
    public int RestartCount { get; set; }

    /// <summary>
    /// Why the first waiting container is waiting - CrashLoopBackOff, ImagePullBackOff and the like -
    /// or null when none is. Init containers are included, because a pod stuck initializing waits
    /// there and nowhere else.
    /// </summary>
    public string? WaitingReason { get; set; }

    /// <summary>
    /// Why a container last stopped - OOMKilled, Error, Completed - or null when none has. The
    /// previous run's reason where there is one, since a restarted container's current state says
    /// only that it is running again.
    /// </summary>
    public string? LastTerminatedReason { get; set; }

    /// <summary>When that container stopped.</summary>
    public DateTime? LastTerminatedUtc { get; set; }

    /// <summary>When the kubelet started the pod, or null while it is unscheduled.</summary>
    public DateTime? StartedUtc { get; set; }

    public string? NodeName { get; set; }

    /// <summary>Whether the pod has been asked to terminate and is on its way out.</summary>
    public bool Deleting { get; set; }
}

/// <summary>An event job with no pod.</summary>
public class MissingJob
{
    public string JobName { get; set; } = string.Empty;

    public int EventId { get; set; }

    /// <summary>
    /// The job's role - event-processor, logger, control-log, external-source - taken from the end
    /// of its name, or null when the name does not follow the orchestrator's pattern.
    /// </summary>
    public string? Role { get; set; }
}
