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
}
