using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Moq;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Services;
using RedMist.StatusApi.Filters;

namespace RedMist.TimingAndScoringService.Tests.StatusApi;

/// <summary>
/// Covers the access-code filter that gates every per-event Status API endpoint. It is the only thing
/// keeping a private event's timing data from being read by anyone who guesses the event id, so both
/// the reject path and the deliberate no-op paths need to be pinned down.
/// </summary>
[TestClass]
public class RequireEventAccessCodeAttributeTests
{
    private sealed class Fixture
    {
        public Mock<IEventAccessValidator> Validator { get; } = new();
        public DefaultHttpContext HttpContext { get; } = new();
        public ActionExecutingContext Context { get; }
        public bool NextCalled { get; private set; }

        public Fixture(IDictionary<string, object?> actionArguments, bool registerValidator = true)
        {
            var services = new Mock<IServiceProvider>();
            services.Setup(x => x.GetService(typeof(IEventAccessValidator)))
                .Returns(registerValidator ? Validator.Object : null);
            HttpContext.RequestServices = services.Object;

            var actionContext = new ActionContext(HttpContext, new RouteData(), new ActionDescriptor());
            Context = new ActionExecutingContext(actionContext, [], actionArguments, controller: new object());
        }

        public ActionExecutionDelegate Next => () =>
        {
            NextCalled = true;
            return Task.FromResult(new ActionExecutedContext(Context, [], controller: new object()));
        };
    }

    private static readonly RequireEventAccessCodeAttribute Filter = new();

    /// <summary>
    /// The attribute is applied to actions that have no event id (and to any future ones); those must
    /// run untouched rather than being rejected for want of a code.
    /// </summary>
    [TestMethod]
    public async Task NoEventIdArgument_RunsTheActionWithoutValidating()
    {
        var fixture = new Fixture(new Dictionary<string, object?>());

        await Filter.OnActionExecutionAsync(fixture.Context, fixture.Next);

        Assert.IsTrue(fixture.NextCalled);
        Assert.IsNull(fixture.Context.Result);
        fixture.Validator.Verify(x => x.ValidateAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>A non-integer eventId (model binding failure, or a string route value) is not validated.</summary>
    [TestMethod]
    public async Task EventIdArgumentIsNotAnInt_RunsTheActionWithoutValidating()
    {
        var fixture = new Fixture(new Dictionary<string, object?> { ["eventId"] = "not-an-int" });

        await Filter.OnActionExecutionAsync(fixture.Context, fixture.Next);

        Assert.IsTrue(fixture.NextCalled);
        fixture.Validator.Verify(x => x.ValidateAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>A null bound eventId is also not an int and must not be validated as 0.</summary>
    [TestMethod]
    public async Task EventIdArgumentIsNull_RunsTheActionWithoutValidating()
    {
        var fixture = new Fixture(new Dictionary<string, object?> { ["eventId"] = null });

        await Filter.OnActionExecutionAsync(fixture.Context, fixture.Next);

        Assert.IsTrue(fixture.NextCalled);
        fixture.Validator.Verify(x => x.ValidateAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task NoValidatorRegistered_RunsTheAction()
    {
        var fixture = new Fixture(new Dictionary<string, object?> { ["eventId"] = 7 }, registerValidator: false);

        await Filter.OnActionExecutionAsync(fixture.Context, fixture.Next);

        Assert.IsTrue(fixture.NextCalled);
        Assert.IsNull(fixture.Context.Result);
    }

    [TestMethod]
    public async Task ValidatorAccepts_RunsTheAction()
    {
        var fixture = new Fixture(new Dictionary<string, object?> { ["eventId"] = 7 });
        fixture.Validator.Setup(x => x.ValidateAsync(7, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await Filter.OnActionExecutionAsync(fixture.Context, fixture.Next);

        Assert.IsTrue(fixture.NextCalled);
        Assert.IsNull(fixture.Context.Result);
    }

    /// <summary>
    /// A rejected code must short-circuit: the action never runs, so no timing data is produced at all.
    /// </summary>
    [TestMethod]
    public async Task ValidatorRejects_ShortCircuitsWith401AndNeverRunsTheAction()
    {
        var fixture = new Fixture(new Dictionary<string, object?> { ["eventId"] = 7 });
        fixture.Validator.Setup(x => x.ValidateAsync(7, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await Filter.OnActionExecutionAsync(fixture.Context, fixture.Next);

        Assert.IsFalse(fixture.NextCalled);
        var unauthorized = fixture.Context.Result as UnauthorizedObjectResult;
        Assert.IsNotNull(unauthorized);
        Assert.AreEqual("Access code required or invalid for this event.", unauthorized.Value);
    }

    [TestMethod]
    public async Task AccessCodeHeader_IsForwardedToTheValidator()
    {
        var fixture = new Fixture(new Dictionary<string, object?> { ["eventId"] = 7 });
        fixture.HttpContext.Request.Headers[Consts.EVENT_ACCESS_CODE_HEADER] = "1234567";
        fixture.Validator.Setup(x => x.ValidateAsync(7, "1234567", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await Filter.OnActionExecutionAsync(fixture.Context, fixture.Next);

        Assert.IsTrue(fixture.NextCalled);
        fixture.Validator.Verify(x => x.ValidateAsync(7, "1234567", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// With no header the validator is still consulted with a null code, so a private event rejects
    /// rather than the filter waving the request through.
    /// </summary>
    [TestMethod]
    public async Task MissingAccessCodeHeader_StillCallsValidatorWithNullCode()
    {
        var fixture = new Fixture(new Dictionary<string, object?> { ["eventId"] = 7 });
        fixture.Validator.Setup(x => x.ValidateAsync(7, null, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await Filter.OnActionExecutionAsync(fixture.Context, fixture.Next);

        Assert.IsFalse(fixture.NextCalled);
        fixture.Validator.Verify(x => x.ValidateAsync(7, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The filter only protects the endpoints that carry it, and it is applied per action, so a new
    /// per-event endpoint is one forgotten attribute away from leaking a private event's data. This
    /// sweeps the controller so the omission fails here instead of in production — which is exactly
    /// how <c>LoadEvent</c> came to be reachable without a code.
    /// </summary>
    /// <remarks>
    /// The allowlist is for actions that take an <c>eventId</c> but are deliberately public. Adding to
    /// it should be a deliberate act, not a side effect of writing a new endpoint.
    /// </remarks>
    [TestMethod]
    // The routable controllers, not the base: version-specific endpoints are declared on these, and
    // reflecting over the base alone would not see them.
    [DataRow(typeof(RedMist.StatusApi.Controllers.V1.EventsController))]
    [DataRow(typeof(RedMist.StatusApi.Controllers.V2.EventsController))]
    public void EveryPerEventEndpoint_CarriesTheAccessCodeFilter(Type controller)
    {
        var deliberatelyPublic = new HashSet<string>();

        var unprotected = controller
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object) && m.DeclaringType != typeof(ControllerBase))
            // The filter reads the action argument literally named "eventId"; anything else is invisible
            // to it, so this mirrors that exactly rather than matching on the parameter type.
            .Where(m => m.GetParameters().Any(p => p.Name == "eventId"))
            .Where(m => !deliberatelyPublic.Contains(m.Name))
            .Where(m => m.GetCustomAttributes(typeof(RequireEventAccessCodeAttribute), inherit: true).Length == 0)
            .Select(m => m.Name)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        Assert.IsEmpty(unprotected,
            $"{controller.Name} has per-event endpoints with no [RequireEventAccessCode]: {string.Join(", ", unprotected)}");
    }

    /// <summary>
    /// Guards the sweep above: if the per-event endpoints ever stop being discoverable this way, the
    /// test would pass vacuously while protecting nothing.
    /// </summary>
    [TestMethod]
    [DataRow(typeof(RedMist.StatusApi.Controllers.V1.EventsController))]
    [DataRow(typeof(RedMist.StatusApi.Controllers.V2.EventsController))]
    public void ThePerEventEndpointSweep_ActuallyFindsEndpoints(Type controller)
    {
        var perEvent = controller
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Count(m => !m.IsSpecialName && m.GetParameters().Any(p => p.Name == "eventId"));

        Assert.IsGreaterThan(10, perEvent, $"{controller.Name} exposes {perEvent} per-event endpoints");
    }
}
