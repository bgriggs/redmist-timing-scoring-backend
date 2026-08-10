using k8s;
using k8s.Autorest;
using k8s.Models;
using Moq;
using System.Net;

namespace RedMist.TimingAndScoringService.Tests.EventOrchestration.Utilities;

/// <summary>
/// A Moq-backed stand-in for the Kubernetes client. It records the jobs, services and custom
/// object patches the orchestrator issues, and can be told to fail any individual operation.
/// </summary>
/// <remarks>
/// The generated <c>Kubernetes</c> class implements the operation groups explicitly and non-virtually,
/// so it cannot be mocked. <see cref="IKubernetes"/> exposes each group as a property, and the
/// convenience extension methods the production code calls forward to the group's
/// <c>...WithHttpMessagesAsync</c> member, which is what is set up here.
/// </remarks>
internal sealed class FakeKubernetes
{
    private readonly Mock<IBatchV1Operations> batch = new();
    private readonly Mock<ICoreV1Operations> core = new();
    private readonly Mock<ICustomObjectsOperations> customObjects = new();
    private readonly Mock<IKubernetes> client = new();

    /// <summary>Jobs the fake cluster reports as already existing.</summary>
    public V1JobList Jobs { get; set; } = new() { Items = [] };

    /// <summary>Pods the fake cluster reports as already existing.</summary>
    public V1PodList Pods { get; set; } = new() { Items = [] };

    public List<V1Job> CreatedJobs { get; } = [];
    public List<string> DeletedJobs { get; } = [];
    public List<V1Service> CreatedServices { get; } = [];
    public List<string> DeletedServices { get; } = [];
    public List<CustomObjectPatch> CustomObjectPatches { get; } = [];

    /// <summary>The namespace every recorded operation was addressed to.</summary>
    public List<string> RequestedNamespaces { get; } = [];

    /// <summary>Label selectors passed to the pod listing, which is how event pods are found.</summary>
    public List<string?> PodListLabelSelectors { get; } = [];

    public Exception? CreateJobError { get; set; }
    public Exception? DeleteJobError { get; set; }
    public Exception? CreateServiceError { get; set; }
    public Exception? DeleteServiceError { get; set; }
    public Exception? PatchCustomObjectError { get; set; }

    public IKubernetes Object => client.Object;

    /// <summary>A 404 from the API server, the shape the production code special-cases.</summary>
    public static HttpOperationException NotFound() => new("Not Found")
    {
        Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.NotFound), string.Empty)
    };

    public FakeKubernetes()
    {
        client.SetupGet(x => x.BatchV1).Returns(batch.Object);
        client.SetupGet(x => x.CoreV1).Returns(core.Object);
        client.SetupGet(x => x.CustomObjects).Returns(customObjects.Object);

        batch.Setup(x => x.ListNamespacedJobWithHttpMessagesAsync(
                It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<int?>(),
                It.IsAny<bool?>(), It.IsAny<bool?>(), It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(new HttpOperationResponse<V1JobList> { Body = Jobs }));

        batch.Setup(x => x.CreateNamespacedJobWithHttpMessagesAsync(
                It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<bool?>(), It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), It.IsAny<CancellationToken>()))
            .Returns((V1Job body, string ns, string _, string _, string _, bool? _,
                IReadOnlyDictionary<string, IReadOnlyList<string>> _, CancellationToken _) =>
            {
                if (CreateJobError is not null) throw CreateJobError;
                RequestedNamespaces.Add(ns);
                CreatedJobs.Add(body);
                return Task.FromResult(new HttpOperationResponse<V1Job> { Body = body });
            });

        batch.Setup(x => x.DeleteNamespacedJobWithHttpMessagesAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<V1DeleteOptions>(), It.IsAny<string>(), It.IsAny<int?>(),
                It.IsAny<bool?>(), It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<bool?>(),
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), It.IsAny<CancellationToken>()))
            .Returns((string name, string ns, V1DeleteOptions _, string _, int? _, bool? _, bool? _, string _, bool? _,
                IReadOnlyDictionary<string, IReadOnlyList<string>> _, CancellationToken _) =>
            {
                if (DeleteJobError is not null) throw DeleteJobError;
                RequestedNamespaces.Add(ns);
                DeletedJobs.Add(name);
                return Task.FromResult(new HttpOperationResponse<V1Status> { Body = new V1Status() });
            });

        core.Setup(x => x.CreateNamespacedServiceWithHttpMessagesAsync(
                It.IsAny<V1Service>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<bool?>(), It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), It.IsAny<CancellationToken>()))
            .Returns((V1Service body, string ns, string _, string _, string _, bool? _,
                IReadOnlyDictionary<string, IReadOnlyList<string>> _, CancellationToken _) =>
            {
                if (CreateServiceError is not null) throw CreateServiceError;
                RequestedNamespaces.Add(ns);
                CreatedServices.Add(body);
                return Task.FromResult(new HttpOperationResponse<V1Service> { Body = body });
            });

        core.Setup(x => x.DeleteNamespacedServiceWithHttpMessagesAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<V1DeleteOptions>(), It.IsAny<string>(), It.IsAny<int?>(),
                It.IsAny<bool?>(), It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<bool?>(),
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), It.IsAny<CancellationToken>()))
            .Returns((string name, string ns, V1DeleteOptions _, string _, int? _, bool? _, bool? _, string _, bool? _,
                IReadOnlyDictionary<string, IReadOnlyList<string>> _, CancellationToken _) =>
            {
                if (DeleteServiceError is not null) throw DeleteServiceError;
                RequestedNamespaces.Add(ns);
                DeletedServices.Add(name);
                return Task.FromResult(new HttpOperationResponse<V1Service> { Body = new V1Service() });
            });

        core.Setup(x => x.ListNamespacedPodWithHttpMessagesAsync(
                It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<int?>(),
                It.IsAny<bool?>(), It.IsAny<bool?>(), It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .Returns((string ns, bool? _, string _, string _, string labelSelector, int? _, string _, string _,
                bool? _, int? _, bool? _, bool? _, IReadOnlyDictionary<string, IReadOnlyList<string>> _, CancellationToken _) =>
            {
                RequestedNamespaces.Add(ns);
                PodListLabelSelectors.Add(labelSelector);
                return Task.FromResult(new HttpOperationResponse<V1PodList> { Body = Pods });
            });

        customObjects.Setup(x => x.PatchNamespacedCustomObjectWithHttpMessagesAsync(
                It.IsAny<object>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(),
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), It.IsAny<CancellationToken>()))
            .Returns((object body, string group, string version, string ns, string plural, string name, string _,
                string _, string _, bool? _, IReadOnlyDictionary<string, IReadOnlyList<string>> _, CancellationToken _) =>
            {
                if (PatchCustomObjectError is not null) throw PatchCustomObjectError;
                RequestedNamespaces.Add(ns);
                CustomObjectPatches.Add(new CustomObjectPatch(body, group, version, ns, plural, name));
                return Task.FromResult(new HttpOperationResponse<object> { Body = new object() });
            });
    }
}

internal record CustomObjectPatch(object Body, string Group, string Version, string Namespace, string Plural, string Name);
