using BigMission.TestHelpers.Testing;
using Keycloak.AuthServices.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using RedMist.Backend.Shared;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventManagement.Controllers;
using RedMist.EventManagement.Models;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.Social.Imaging;
using System.Reflection;
using System.Security.Claims;
using ConfigEvent = RedMist.TimingCommon.Models.Configuration.Event;

namespace RedMist.TimingAndScoringService.Tests.EventManagement.Controllers;

/// <summary>
/// The review API: the human step between a model writing a post and the post going out.
/// </summary>
/// <remarks>
/// Two things here fail quietly rather than loudly, and most of these tests are about those two. A
/// write that is not conditional on the version the reviewer read applies an approval to copy nobody
/// read. And a state change that is not checked against the state it came from can release the
/// Publishing trap, which is how one result gets posted twice in public.
/// </remarks>
[TestClass]
public class SocialControllerBaseTests
{
    private const int PostId = 10;
    private const int EventId = 384;
    private const uint Version = 42;
    private const string Reviewer = "brian";

    private static readonly DateTime Now = new(2026, 9, 6, 5, 0, 0, DateTimeKind.Utc);

    private IDbContextFactory<TsContext> dbFactory = null!;
    private TsContext db = null!;
    private StubStore store = null!;
    private TestSocialController controller = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        dbFactory = new TestDbContextFactory(options);
        db = dbFactory.CreateDbContext();

        var loggerFactory = new DebugLoggerFactory();
        store = new StubStore(dbFactory);
        controller = new TestSocialController(
            loggerFactory, dbFactory, new SocialImageCleanup(store, loggerFactory), new FakeTimeProvider(Now));

        SetUser(Reviewer, Consts.SITE_ADMIN_ROLE);
    }

    [TestCleanup]
    public void Cleanup() => db?.Dispose();

    #region The gate

    /// <summary>
    /// The role requirement sits on the controller, so an endpoint added later inherits it. What has
    /// to be watched is the opposite: an endpoint opted back out of it.
    /// </summary>
    [TestMethod]
    public void EveryEndpointExceptWhoAmI_RequiresTheSiteAdminRole()
    {
        var authorize = typeof(SocialControllerBase).GetCustomAttribute<AuthorizeAttribute>();
        Assert.IsNotNull(authorize, "The controller is not gated at all");
        Assert.AreEqual(Consts.SITE_ADMIN_ROLE, authorize.Roles);

        var anonymous = Actions()
            .Where(m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
            .Select(m => m.Name)
            .ToList();

        CollectionAssert.AreEqual(new[] { nameof(SocialControllerBase.WhoAmI) }, anonymous,
            "Only the identity probe may be reachable without the role; it reports nothing the caller " +
            "did not already present.");
    }

    /// <summary>Every endpoint that writes has to be a POST, so none is reachable by a link.</summary>
    [TestMethod]
    public void TheWritingEndpoints_AreNotReachableByNavigation()
    {
        foreach (var name in new[]
        {
            nameof(SocialControllerBase.SaveEdit), nameof(SocialControllerBase.ApprovePost),
            nameof(SocialControllerBase.UnapprovePost), nameof(SocialControllerBase.RejectPost),
            nameof(SocialControllerBase.DeletePost), nameof(SocialControllerBase.ResolvePost),
        })
        {
            var action = Actions().Single(m => m.Name == name);
            Assert.IsNotNull(action.GetCustomAttribute<HttpPostAttribute>(), $"{name} is not a POST");
        }
    }

    private static IEnumerable<MethodInfo> Actions() =>
        typeof(SocialControllerBase)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName);

    /// <summary>
    /// The concrete controller is what is actually routed, so an action added there -- or opted out
    /// of the role there -- would not be seen by a check that only reflects over the base.
    /// </summary>
    [TestMethod]
    public void TheRoutedController_AddsNothingOutsideTheGate()
    {
        var anonymous = typeof(RedMist.EventManagement.Controllers.V1.SocialController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
            .Select(m => m.Name)
            .ToList();

        CollectionAssert.AreEqual(new[] { nameof(SocialControllerBase.WhoAmI) }, anonymous);
    }

    /// <summary>
    /// The states each write accepts. Held as data so the whole machine is asserted in one place: the
    /// dangerous mistakes here are omissions, and an allowed-from list is only ever read by the person
    /// editing the endpoint next to it.
    /// </summary>
    private static readonly Dictionary<string, SocialPostState[]> AllowedStates = new()
    {
        [nameof(SocialControllerBase.SaveEdit)] =
            [SocialPostState.Draft, SocialPostState.PendingReview, SocialPostState.Failed],
        [nameof(SocialControllerBase.ApprovePost)] =
            [SocialPostState.PendingReview, SocialPostState.Failed],
        [nameof(SocialControllerBase.UnapprovePost)] =
            [SocialPostState.Approved],
        [nameof(SocialControllerBase.RejectPost)] =
            [SocialPostState.Draft, SocialPostState.PendingReview, SocialPostState.Approved, SocialPostState.Failed],
        [nameof(SocialControllerBase.DeletePost)] =
            [SocialPostState.Draft, SocialPostState.PendingReview, SocialPostState.Approved,
             SocialPostState.Rejected, SocialPostState.Failed],
        [nameof(SocialControllerBase.ResolvePost)] =
            [SocialPostState.Publishing],
    };

    /// <summary>Every write has to appear in the matrix, so adding one forces the question to be answered.</summary>
    [TestMethod]
    public void TheTransitionMatrix_CoversEveryWritingEndpoint()
    {
        var writes = Actions()
            .Where(m => m.GetCustomAttribute<HttpPostAttribute>() is not null)
            .Select(m => m.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(AllowedStates.Keys.Order(StringComparer.Ordinal).ToArray(), writes,
            "A write that is not in the matrix has no test saying which states it accepts");
    }

    /// <summary>
    /// The whole state machine, asserted rather than read.
    /// </summary>
    /// <remarks>
    /// Publishing is the one that matters most. A post there may or may not be live on the channel, so
    /// any endpoint that moves it somewhere ApprovePost accepts is a route to posting the same result
    /// twice in public -- and the route is not obvious, because it takes two ordinary-looking requests.
    /// </remarks>
    [TestMethod]
    [DataRow(nameof(SocialControllerBase.SaveEdit))]
    [DataRow(nameof(SocialControllerBase.ApprovePost))]
    [DataRow(nameof(SocialControllerBase.UnapprovePost))]
    [DataRow(nameof(SocialControllerBase.RejectPost))]
    [DataRow(nameof(SocialControllerBase.DeletePost))]
    [DataRow(nameof(SocialControllerBase.ResolvePost))]
    public async Task EachWrite_AcceptsExactlyTheStatesTheMatrixNames(string endpoint)
    {
        var allowed = AllowedStates[endpoint];

        foreach (var state in Enum.GetValues<SocialPostState>())
        {
            Setup(); // A fresh database per case, since several of these change or remove the row.
            await SeedAsync(NewPost(PostId, state));

            var result = await InvokeAsync(endpoint);
            var refused = result is ConflictObjectResult;

            Assert.AreEqual(!allowed.Contains(state), refused,
                $"{endpoint} from {state}: expected {(allowed.Contains(state) ? "accepted" : "refused")} " +
                $"but got {result?.GetType().Name ?? "success"}");
        }
    }

    /// <summary>Calls one write with a request that is valid apart from the post's state.</summary>
    private async Task<IActionResult?> InvokeAsync(string endpoint)
    {
        var change = new SocialPostChange { Id = PostId, RowVersion = Version };
        return endpoint switch
        {
            nameof(SocialControllerBase.SaveEdit) =>
                (await controller.SaveEdit(new SocialPostEdit { Id = PostId, RowVersion = Version, EditedText = "x" })).Result,
            nameof(SocialControllerBase.ApprovePost) =>
                (await controller.ApprovePost(new SocialPostApproval { Id = PostId, RowVersion = Version })).Result,
            nameof(SocialControllerBase.UnapprovePost) =>
                (await controller.UnapprovePost(change)).Result,
            nameof(SocialControllerBase.RejectPost) =>
                (await controller.RejectPost(new SocialPostRejection { Id = PostId, RowVersion = Version })).Result,
            nameof(SocialControllerBase.DeletePost) =>
                await controller.DeletePost(change),
            nameof(SocialControllerBase.ResolvePost) =>
                (await controller.ResolvePost(new SocialPostResolution
                {
                    Id = PostId,
                    RowVersion = Version,
                    WasPublished = true,
                    ExternalPostId = "fb-1",
                })).Result,
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "Not a known write"),
        };
    }

    [TestMethod]
    public void WhoAmI_ReportsTheRolesAndWhetherTheTokenCarriedRealmAccess()
    {
        SetUser(Reviewer, Consts.SITE_ADMIN_ROLE);
        var withRoles = Value(controller.WhoAmI());

        Assert.AreEqual(Reviewer, withRoles.Name);
        CollectionAssert.Contains(withRoles.Roles, Consts.SITE_ADMIN_ROLE);
        Assert.IsTrue(withRoles.IsSiteAdmin);
        Assert.IsTrue(withRoles.HasRealmAccessClaim);
    }

    /// <summary>
    /// The case this endpoint exists for: a valid token that simply never carried roles. It is
    /// indistinguishable from not being an administrator at the point of refusal.
    /// </summary>
    [TestMethod]
    public void WhoAmI_DistinguishesATokenWithoutRealmAccessFromAnOrdinaryUser()
    {
        SetUser(Reviewer, roles: null, includeRealmAccess: false);
        var lightweight = Value(controller.WhoAmI());

        Assert.IsFalse(lightweight.HasRealmAccessClaim,
            "A token with no realm_access is the signature of lightweight access tokens or a missing roles scope");
        Assert.IsFalse(lightweight.IsSiteAdmin);
        Assert.AreEqual(0, lightweight.Roles.Count);
    }

    #endregion

    #region Reading

    [TestMethod]
    public async Task LoadPosts_FiltersByState()
    {
        await SeedAsync(NewPost(1, SocialPostState.PendingReview));
        await SeedAsync(NewPost(2, SocialPostState.Published));

        var page = Value(await controller.LoadPosts(SocialPostState.PendingReview));

        Assert.AreEqual(1, page.TotalCount);
        Assert.AreEqual(1, page.Posts.Single().Id);
    }

    [TestMethod]
    public async Task LoadPosts_FiltersByEvent()
    {
        await SeedAsync(NewPost(1, SocialPostState.PendingReview));
        var other = NewPost(2, SocialPostState.PendingReview);
        other.EventId = EventId + 1;
        await SeedAsync(other);

        var page = Value(await controller.LoadPosts(eventId: EventId));

        Assert.AreEqual(1, page.TotalCount);
        Assert.AreEqual(1, page.Posts.Single().Id);
    }

    /// <summary>
    /// Posts written by one run share a creation time closely enough that ordering on it alone is
    /// unstable, and an unstable order drops and repeats rows across pages.
    /// </summary>
    [TestMethod]
    public async Task LoadPosts_OrdersNewestFirstAndBreaksTiesDeterministically()
    {
        var sameMoment = Now.AddHours(-1);
        await SeedAsync(NewPost(1, SocialPostState.PendingReview, created: sameMoment));
        await SeedAsync(NewPost(2, SocialPostState.PendingReview, created: sameMoment));
        await SeedAsync(NewPost(3, SocialPostState.PendingReview, created: Now.AddHours(-2)));

        var page = Value(await controller.LoadPosts());

        CollectionAssert.AreEqual(new[] { 2, 1, 3 }, page.Posts.Select(p => p.Id).ToArray());
    }

    [TestMethod]
    public async Task LoadPosts_CapsThePageSize()
    {
        for (var i = 1; i <= 5; i++)
            await SeedAsync(NewPost(i, SocialPostState.PendingReview, created: Now.AddMinutes(-i)));

        var page = Value(await controller.LoadPosts(take: 100_000));

        Assert.AreEqual(5, page.Posts.Count);
        Assert.AreEqual(5, page.TotalCount);
    }

    [TestMethod]
    [DataRow(-1, 10)]
    [DataRow(0, 0)]
    [DataRow(0, -5)]
    public async Task LoadPosts_RefusesNonsensePaging(int skip, int take)
    {
        var result = await controller.LoadPosts(skip: skip, take: take);
        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    /// <summary>
    /// The total counts every match, not the page: a UI that pages on the returned count stops early.
    /// </summary>
    [TestMethod]
    public async Task LoadPosts_ReportsTheTotalRatherThanThePageLength()
    {
        for (var i = 1; i <= 5; i++)
            await SeedAsync(NewPost(i, SocialPostState.PendingReview, created: Now.AddMinutes(-i)));

        var page = Value(await controller.LoadPosts(skip: 1, take: 2));

        Assert.AreEqual(2, page.Posts.Count);
        Assert.AreEqual(5, page.TotalCount);
    }

    /// <summary>The list has to mark what needs reading closely before anything is opened.</summary>
    [TestMethod]
    public async Task LoadPosts_CarriesTheImageCountAndTheUnverifiedClaimsFlag()
    {
        var post = NewPost(1, SocialPostState.PendingReview);
        post.HasUnverifiedClaims = true;
        post.ImageRefs = ["https://cdn.example/a.png", "https://cdn.example/b.png"];
        await SeedAsync(post);

        var summary = Value(await controller.LoadPosts()).Posts.Single();

        Assert.IsTrue(summary.HasUnverifiedClaims);
        Assert.AreEqual(2, summary.ImageCount);
    }

    /// <summary>The preview is of what would be published, not of what the model first wrote.</summary>
    [TestMethod]
    public async Task LoadPosts_PreviewsTheTextThatWouldBePublished()
    {
        var post = NewPost(1, SocialPostState.PendingReview);
        post.GeneratedText = "generated";
        post.EditedText = "edited";
        await SeedAsync(post);

        Assert.AreEqual("edited", Value(await controller.LoadPosts()).Posts.Single().Preview);
    }

    [TestMethod]
    public async Task LoadPost_ReturnsNotFoundForAnUnknownId()
    {
        Assert.IsInstanceOfType<NotFoundResult>((await controller.LoadPost(999)).Result);
    }

    /// <summary>
    /// Times leave here marked as the UTC they are.
    /// </summary>
    /// <remarks>
    /// The columns are "timestamp without time zone" and legacy timestamp behavior reads them back as
    /// Unspecified, which serializes with no trailing Z -- and a browser reads an offsetless timestamp
    /// as its own local time. A post drafted at 05:00 UTC would show as the previous evening, and
    /// which day a post was written is what tells a reviewer it is about the right race.
    /// </remarks>
    [TestMethod]
    public async Task TheTimesAPostCarries_SaySoFar_TheyAreUtc()
    {
        var post = NewPost(PostId, SocialPostState.Published);
        post.PublishedUtc = Now.AddHours(-1);
        post.ApprovedUtc = Now.AddHours(-2);
        await SeedAsync(post);

        var detail = Value(await controller.LoadPost(PostId));

        Assert.AreEqual(DateTimeKind.Utc, detail.CreatedUtc.Kind);
        Assert.AreEqual(DateTimeKind.Utc, detail.ScheduledUtc.Kind);
        Assert.AreEqual(DateTimeKind.Utc, detail.PublishedUtc!.Value.Kind);
        Assert.AreEqual(DateTimeKind.Utc, detail.ApprovedUtc!.Value.Kind);

        var summary = Value(await controller.LoadPosts()).Posts.Single();
        Assert.AreEqual(DateTimeKind.Utc, summary.CreatedUtc.Kind);
        Assert.AreEqual(DateTimeKind.Utc, summary.ScheduledUtc.Kind);
    }

    /// <summary>
    /// The images are part of the post under review: they are publicly fetchable from the moment they
    /// are stored and they go out attached to the copy, so a reviewer who cannot see them is not
    /// reviewing the post.
    /// </summary>
    [TestMethod]
    public async Task LoadPost_CarriesTheImagesAndTheDigestTheClaimsCanBeCheckedAgainst()
    {
        var post = NewPost(PostId, SocialPostState.PendingReview);
        post.ImageRefs = ["https://cdn.example/a.png"];
        post.DigestJson = """{"winner":"42"}""";
        post.ValidationWarnings = "mentions a number the digest does not support";
        post.HasUnverifiedClaims = true;
        await SeedAsync(post);
        db.Events.Add(new ConfigEvent { Id = EventId, OrganizationId = 1, Name = "Summer Sprints" });
        await db.SaveChangesAsync();

        var detail = Value(await controller.LoadPost(PostId));

        CollectionAssert.AreEqual(new[] { "https://cdn.example/a.png" }, detail.ImageRefs.ToArray());
        Assert.AreEqual("""{"winner":"42"}""", detail.DigestJson);
        Assert.AreEqual("mentions a number the digest does not support", detail.ValidationWarnings);
        Assert.IsTrue(detail.HasUnverifiedClaims);
        Assert.AreEqual("Summer Sprints", detail.EventName, "A reviewer should not be reading bare event ids");
        Assert.AreEqual(Version, detail.RowVersion, "Without the version the reviewer cannot write anything back");
    }

    #endregion

    #region The version check

    /// <summary>
    /// The reason the whole API is conditional. A reviewer takes a minute over a draft; the compose
    /// job rewrites that row in seconds when the results change. Approving without this check clears
    /// copy nobody read.
    /// </summary>
    [TestMethod]
    public async Task AWriteAgainstAVersionSomebodyElseSuperseded_IsRefused()
    {
        await SeedAsync(NewPost(PostId, SocialPostState.PendingReview));

        // The reviewer read version 42. The compose job rewrote the row while they were reading.
        await using (var other = await dbFactory.CreateDbContextAsync())
        {
            var post = await other.SocialPosts.FirstAsync(p => p.Id == PostId);
            post.GeneratedText = "a completely different post about different results";
            other.Entry(post).Property<uint>(TsContext.RowVersionProperty).CurrentValue = Version + 1;
            await other.SaveChangesAsync();
        }

        var result = await controller.ApprovePost(new SocialPostApproval { Id = PostId, RowVersion = Version });

        Assert.IsInstanceOfType<ConflictObjectResult>(result.Result);
        Assert.AreEqual(SocialPostState.PendingReview, (await Reload()).State,
            "The stale approval must not have been applied");
    }

    /// <summary>
    /// Zero is what a client that has not implemented the check sends. Matching no row would read as
    /// "somebody deleted it", which sends a reviewer looking for the wrong problem.
    /// </summary>
    [TestMethod]
    public async Task AWriteWithNoVersionAtAll_IsRefusedAsABadRequest()
    {
        await SeedAsync(NewPost(PostId, SocialPostState.PendingReview));

        var result = await controller.SaveEdit(new SocialPostEdit { Id = PostId, RowVersion = 0, EditedText = "hi" });

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
        Assert.IsNull((await Reload()).EditedText);
    }

    [TestMethod]
    public async Task AWriteAgainstAnUnknownPost_IsNotFound()
    {
        var result = await controller.SaveEdit(new SocialPostEdit { Id = 999, RowVersion = Version });
        Assert.IsInstanceOfType<NotFoundResult>(result.Result);
    }

    /// <summary>
    /// A stale write that happens to change nothing must still be refused. Saving identical text makes
    /// EF issue no command at all, so the version in the WHERE clause is never tested and the request
    /// would otherwise be answered 200 -- telling a reviewer on an out-of-date page that they are
    /// up to date.
    /// </summary>
    [TestMethod]
    public async Task AStaleWriteThatWouldChangeNothing_IsStillRefused()
    {
        var post = NewPost(PostId, SocialPostState.PendingReview);
        post.EditedText = "unchanged";
        await SeedAsync(post);

        var result = await controller.SaveEdit(
            new SocialPostEdit { Id = PostId, RowVersion = Version + 1, EditedText = "unchanged" });

        Assert.IsInstanceOfType<ConflictObjectResult>(result.Result);
    }

    /// <summary>The version has to come back out, or a reviewer has nothing to send with a write.</summary>
    [TestMethod]
    public async Task TheListCarriesTheVersion_SoAReviewerCanActFromIt()
    {
        await SeedAsync(NewPost(PostId, SocialPostState.PendingReview));

        Assert.AreEqual(Version, Value(await controller.LoadPosts()).Posts.Single().RowVersion);
    }

    #endregion

    #region Editing and approval

    /// <summary>
    /// Clearing the edit box in a review UI produces an empty string, not a null. Storing that would
    /// publish nothing, so a blank edit restores the generated copy rather than replacing it.
    /// </summary>
    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public async Task SaveEdit_ABlankEditRestoresTheGeneratedCopy(string blank)
    {
        var post = NewPost(PostId, SocialPostState.PendingReview);
        post.GeneratedText = "what the model wrote";
        post.EditedText = "an earlier rewrite";
        await SeedAsync(post);

        Value(await controller.SaveEdit(new SocialPostEdit { Id = PostId, RowVersion = Version, EditedText = blank }));

        var saved = await Reload();
        Assert.IsNull(saved.EditedText);
        Assert.AreEqual("what the model wrote", saved.EffectiveText);
    }

    /// <summary>
    /// The two endpoints treat a missing edit differently on purpose, and the difference is easy to
    /// get backwards: SaveEdit carries the whole edit, so absent means none; ApprovePost carries an
    /// optional last change, so absent means leave it alone. Getting the second wrong would publish
    /// the generated copy in place of the reviewer's rewrite.
    /// </summary>
    [TestMethod]
    public async Task AnOmittedEdit_ClearsOnSaveButIsLeftAloneOnApprove()
    {
        var post = NewPost(PostId, SocialPostState.PendingReview);
        post.GeneratedText = "what the model wrote";
        post.EditedText = "the reviewer's rewrite";
        await SeedAsync(post);

        var approved = Value(await controller.ApprovePost(new SocialPostApproval { Id = PostId, RowVersion = Version }));
        Assert.AreEqual("the reviewer's rewrite", approved.EffectiveText,
            "Approving without mentioning the text must not discard the rewrite");

        Setup();
        await SeedAsync(post);

        var edited = Value(await controller.SaveEdit(new SocialPostEdit { Id = PostId, RowVersion = Version }));
        Assert.AreEqual("what the model wrote", edited.EffectiveText,
            "Saving an edit with no text is how a reviewer takes their rewrite back");
    }

    [TestMethod]
    public async Task SaveEdit_KeepsTheGeneratedTextVerbatim()
    {
        var post = NewPost(PostId, SocialPostState.PendingReview);
        post.GeneratedText = "what the model wrote";
        await SeedAsync(post);

        Value(await controller.SaveEdit(new SocialPostEdit { Id = PostId, RowVersion = Version, EditedText = "mine" }));

        var saved = await Reload();
        Assert.AreEqual("what the model wrote", saved.GeneratedText,
            "How much a draft is rewritten is the most direct signal of whether the prompt works");
        Assert.AreEqual("mine", saved.EffectiveText);
    }

    /// <summary>
    /// An approval is given for particular words. Editing afterwards would publish text that was
    /// never the text anybody cleared.
    /// </summary>
    [TestMethod]
    public async Task SaveEdit_RefusesAnApprovedPost()
    {
        await SeedAsync(NewPost(PostId, SocialPostState.Approved));

        var result = await controller.SaveEdit(new SocialPostEdit { Id = PostId, RowVersion = Version, EditedText = "sneaky" });

        Assert.IsInstanceOfType<ConflictObjectResult>(result.Result);
        Assert.IsNull((await Reload()).EditedText);
    }

    [TestMethod]
    public async Task ApprovePost_RecordsWhoApprovedItAndWhen()
    {
        await SeedAsync(NewPost(PostId, SocialPostState.PendingReview));

        var detail = Value(await controller.ApprovePost(new SocialPostApproval { Id = PostId, RowVersion = Version }));

        Assert.AreEqual(SocialPostState.Approved, detail.State);
        Assert.AreEqual(Reviewer, detail.ApprovedBy);
        Assert.AreEqual(Now, detail.ApprovedUtc);
        Assert.AreEqual(Now, detail.ScheduledUtc, "An approval with no schedule means as soon as possible");
    }

    /// <summary>
    /// The schedule is an offset on the wire, so a time given anywhere but UTC lands as the moment it
    /// names. A bare timestamp read as UTC would move a scheduled post by the size of the offset, in
    /// a column that cannot tell the difference afterwards.
    /// </summary>
    [TestMethod]
    public async Task ApprovePost_StoresTheMomentTheScheduleNamesRatherThanItsWallClock()
    {
        await SeedAsync(NewPost(PostId, SocialPostState.PendingReview));
        var noonInParis = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.FromHours(2));

        var detail = Value(await controller.ApprovePost(
            new SocialPostApproval { Id = PostId, RowVersion = Version, ScheduledUtc = noonInParis }));

        Assert.AreEqual(new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc), detail.ScheduledUtc);
    }

    [TestMethod]
    public async Task ApprovePost_AppliesAFinalEditAsPartOfTheSameDecision()
    {
        await SeedAsync(NewPost(PostId, SocialPostState.PendingReview));

        var detail = Value(await controller.ApprovePost(
            new SocialPostApproval { Id = PostId, RowVersion = Version, EditedText = "final wording" }));

        Assert.AreEqual("final wording", detail.EffectiveText);
        Assert.AreEqual(SocialPostState.Approved, detail.State);
    }

    /// <summary>Approving something with nothing to say hands the publish job an empty post.</summary>
    [TestMethod]
    public async Task ApprovePost_RefusesAPostWithNothingToPublish()
    {
        var post = NewPost(PostId, SocialPostState.PendingReview);
        post.GeneratedText = null;
        await SeedAsync(post);

        var result = await controller.ApprovePost(new SocialPostApproval { Id = PostId, RowVersion = Version });

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
        Assert.AreEqual(SocialPostState.PendingReview, (await Reload()).State);
    }

    /// <summary>
    /// A post whose publication was interrupted and then confirmed not to have gone out is good copy
    /// that simply did not send. Refusing it here would strand it: the compose job will not redraft
    /// over a hand-edited post either, so there would be no way to publish it at all.
    /// </summary>
    [TestMethod]
    public async Task ApprovePost_AcceptsAPostWhosePublicationWasConfirmedNotToHaveHappened()
    {
        await SeedAsync(NewPost(PostId, SocialPostState.Failed));

        var detail = Value(await controller.ApprovePost(new SocialPostApproval { Id = PostId, RowVersion = Version }));

        Assert.AreEqual(SocialPostState.Approved, detail.State);
        Assert.IsNull(detail.Error, "The old failure must not sit on an approved post");
    }

    /// <summary>Text longer than a channel accepts is refused rather than cut in half.</summary>
    [TestMethod]
    public async Task AnAbsurdlyLongRewrite_IsRefused()
    {
        await SeedAsync(NewPost(PostId, SocialPostState.PendingReview));

        var result = await controller.SaveEdit(
            new SocialPostEdit { Id = PostId, RowVersion = Version, EditedText = new string('x', 10_001) });

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
        Assert.IsNull((await Reload()).EditedText);
    }

    /// <summary>
    /// The alternative to changing your mind is rejecting, which destroys the images. That should not
    /// be the only way back.
    /// </summary>
    [TestMethod]
    public async Task UnapprovePost_ReturnsItToReviewAndForgetsTheApproval()
    {
        var post = NewPost(PostId, SocialPostState.Approved);
        post.ApprovedBy = "somebody";
        post.ApprovedUtc = Now.AddHours(-1);
        post.ImageRefs = ["https://cdn.example/a.png"];
        await SeedAsync(post);

        var detail = Value(await controller.UnapprovePost(new SocialPostChange { Id = PostId, RowVersion = Version }));

        Assert.AreEqual(SocialPostState.PendingReview, detail.State);
        Assert.IsNull(detail.ApprovedBy);
        Assert.IsNull(detail.ApprovedUtc);
        Assert.AreEqual(0, store.Deleted.Count, "Changing your mind must not destroy the pictures");
    }

    #endregion

    #region Rejection and deletion

    /// <summary>
    /// The row is what says the images are nobody's. Deleting them first would destroy the pictures
    /// of a post whose rejection then failed to save, leaving something reviewable with no images.
    /// </summary>
    [TestMethod]
    public async Task RejectPost_GivesUpTheImagesOnlyAfterTheRowSaysSo()
    {
        var post = NewPost(PostId, SocialPostState.PendingReview);
        post.ImageRefs = ["https://cdn.example/a.png", "https://cdn.example/b.png"];
        await SeedAsync(post);

        Value(await controller.RejectPost(new SocialPostRejection { Id = PostId, RowVersion = Version, Reason = "wrong tone" }));

        CollectionAssert.AreEquivalent(
            new[] { "https://cdn.example/a.png", "https://cdn.example/b.png" }, store.Deleted.ToArray());
        Assert.IsTrue(store.StateWhenDeleted.All(s => s == SocialPostState.Rejected),
            $"Images were deleted while the post still read as {string.Join(", ", store.StateWhenDeleted)}");

        var saved = await Reload();
        Assert.AreEqual(0, saved.ImageRefs.Count, "A released image must not still be referenced");
        Assert.AreEqual("wrong tone", saved.RejectionReason);
    }

    /// <summary>
    /// A rejection is a judgment, not a failure. Recording it as an error would make a post somebody
    /// disliked indistinguishable from one the generator could not write.
    /// </summary>
    [TestMethod]
    public async Task RejectPost_RecordsTheReasonSeparatelyFromAnyError()
    {
        var post = NewPost(PostId, SocialPostState.Failed);
        post.Error = "the model timed out";
        await SeedAsync(post);

        Value(await controller.RejectPost(new SocialPostRejection { Id = PostId, RowVersion = Version, Reason = "not worth redrafting" }));

        var saved = await Reload();
        Assert.AreEqual("not worth redrafting", saved.RejectionReason);
        Assert.AreEqual("the model timed out", saved.Error);
    }

    /// <summary>
    /// The column is 1000 characters and the reason is free text a reviewer types. The in-memory
    /// provider enforces no length at all, so without this the first over-long reason would be a
    /// Postgres 22001 and a 500 in production, with nothing here having noticed.
    /// </summary>
    [TestMethod]
    public async Task RejectPost_TrimsAReasonTooLongForItsColumn()
    {
        await SeedAsync(NewPost(PostId, SocialPostState.PendingReview));

        Value(await controller.RejectPost(
            new SocialPostRejection { Id = PostId, RowVersion = Version, Reason = new string('r', 1500) }));

        Assert.AreEqual(1000, (await Reload()).RejectionReason!.Length);
    }

    /// <summary>ApprovedBy is 200 characters, and a Keycloak username is not guaranteed to be shorter.</summary>
    [TestMethod]
    public async Task ApprovePost_TrimsAnApproverNameTooLongForItsColumn()
    {
        await SeedAsync(NewPost(PostId, SocialPostState.PendingReview));
        SetUser(new string('u', 300), Consts.SITE_ADMIN_ROLE);

        var detail = Value(await controller.ApprovePost(new SocialPostApproval { Id = PostId, RowVersion = Version }));

        Assert.AreEqual(200, detail.ApprovedBy!.Length);
    }

    /// <summary>The key is kept, which is what stops the same event being drafted again tomorrow.</summary>
    [TestMethod]
    public async Task RejectPost_KeepsTheRowAndItsIdempotencyKey()
    {
        await SeedAsync(NewPost(PostId, SocialPostState.PendingReview));

        Value(await controller.RejectPost(new SocialPostRejection { Id = PostId, RowVersion = Version }));

        var saved = await Reload();
        Assert.AreEqual(SocialPostState.Rejected, saved.State);
        Assert.AreEqual(SocialPost.BuildIdempotencyKey(SocialPostKind.EventResults, EventId, SocialChannel.Facebook),
            saved.IdempotencyKey);
    }

    [TestMethod]
    [DataRow(SocialPostState.Published)]
    [DataRow(SocialPostState.Publishing)]
    [DataRow(SocialPostState.Rejected)]
    public async Task RejectPost_RefusesAPostThatIsPastReview(SocialPostState state)
    {
        var post = NewPost(PostId, state);
        post.ImageRefs = ["https://cdn.example/a.png"];
        await SeedAsync(post);

        var result = await controller.RejectPost(new SocialPostRejection { Id = PostId, RowVersion = Version });

        Assert.IsInstanceOfType<ConflictObjectResult>(result.Result);
        Assert.AreEqual(0, store.Deleted.Count, "A refused rejection must not have deleted anything");
    }

    [TestMethod]
    public async Task DeletePost_RemovesTheRowAndThenTheImages()
    {
        var post = NewPost(PostId, SocialPostState.PendingReview);
        post.ImageRefs = ["https://cdn.example/a.png"];
        await SeedAsync(post);

        var result = await controller.DeletePost(new SocialPostChange { Id = PostId, RowVersion = Version });

        Assert.IsInstanceOfType<OkResult>(result);
        Assert.IsNull(await Reload());
        CollectionAssert.AreEqual(new[] { "https://cdn.example/a.png" }, store.Deleted.ToArray());
        Assert.IsTrue(store.PostGoneWhenDeleted.All(gone => gone),
            "The images were deleted before the row that had a claim to them was");
    }

    /// <summary>
    /// A published post exists on the channel whether or not this row does, and the row is the only
    /// record of something the site said in public. A publishing one may or may not have gone out.
    /// </summary>
    [TestMethod]
    [DataRow(SocialPostState.Published)]
    [DataRow(SocialPostState.Publishing)]
    public async Task DeletePost_RefusesToDestroyTheOnlyRecordOfAPublicPost(SocialPostState state)
    {
        var post = NewPost(PostId, state);
        post.ImageRefs = ["https://cdn.example/a.png"];
        await SeedAsync(post);

        var result = await controller.DeletePost(new SocialPostChange { Id = PostId, RowVersion = Version });

        Assert.IsInstanceOfType<ConflictObjectResult>(result);
        Assert.IsNotNull(await Reload());
        Assert.AreEqual(0, store.Deleted.Count);
    }

    [TestMethod]
    public async Task DeletePost_AgainstAStaleVersion_LeavesEverythingAlone()
    {
        var post = NewPost(PostId, SocialPostState.PendingReview);
        post.ImageRefs = ["https://cdn.example/a.png"];
        await SeedAsync(post);

        var result = await controller.DeletePost(new SocialPostChange { Id = PostId, RowVersion = Version + 5 });

        Assert.IsInstanceOfType<ConflictObjectResult>(result);
        Assert.IsNotNull(await Reload());
        Assert.AreEqual(0, store.Deleted.Count, "Nothing may be released for a delete that did not happen");
    }

    #endregion

    #region The publishing trap

    [TestMethod]
    public async Task ResolvePost_MarksItPublishedWhenItReachedTheChannel()
    {
        await SeedAsync(NewPost(PostId, SocialPostState.Publishing));

        var detail = Value(await controller.ResolvePost(new SocialPostResolution
        {
            Id = PostId,
            RowVersion = Version,
            WasPublished = true,
            ExternalPostId = "fb-123",
            ExternalUrl = "https://facebook.com/posts/123",
        }));

        Assert.AreEqual(SocialPostState.Published, detail.State);
        Assert.AreEqual("fb-123", detail.ExternalPostId);
        Assert.AreEqual(Now, detail.PublishedUtc);
    }

    /// <summary>
    /// Without the channel's id there is nothing to link to and no way to tell a determination from a
    /// guess -- and the whole point of resolving is that somebody actually looked.
    /// </summary>
    [TestMethod]
    public async Task ResolvePost_RefusesToRecordAPublicationItCannotPointAt()
    {
        await SeedAsync(NewPost(PostId, SocialPostState.Publishing));

        var result = await controller.ResolvePost(new SocialPostResolution
        {
            Id = PostId,
            RowVersion = Version,
            WasPublished = true,
        });

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
        Assert.AreEqual(SocialPostState.Publishing, (await Reload()).State, "The trap must still be set");
    }

    [TestMethod]
    public async Task ResolvePost_MarksItFailedWhenItNeverReachedTheChannel()
    {
        await SeedAsync(NewPost(PostId, SocialPostState.Publishing));

        var detail = Value(await controller.ResolvePost(new SocialPostResolution
        {
            Id = PostId,
            RowVersion = Version,
            WasPublished = false,
        }));

        Assert.AreEqual(SocialPostState.Failed, detail.State);
        StringAssert.Contains(detail.Error, Reviewer, "The record should say who determined it");
    }

    /// <summary>Only a post actually stuck mid-publish can be resolved; anything else is a state change in disguise.</summary>
    [TestMethod]
    [DataRow(SocialPostState.PendingReview)]
    [DataRow(SocialPostState.Approved)]
    [DataRow(SocialPostState.Published)]
    public async Task ResolvePost_OnlyFromPublishing(SocialPostState state)
    {
        await SeedAsync(NewPost(PostId, state));

        var result = await controller.ResolvePost(new SocialPostResolution
        {
            Id = PostId,
            RowVersion = Version,
            WasPublished = true,
            ExternalPostId = "fb-123",
        });

        Assert.IsInstanceOfType<ConflictObjectResult>(result.Result);
        Assert.AreEqual(state, (await Reload()).State);
    }

    #endregion

    #region Helpers

    private static SocialPost NewPost(int id, SocialPostState state, DateTime? created = null) => new()
    {
        Id = id,
        Kind = SocialPostKind.EventResults,
        EventId = EventId,
        Channel = SocialChannel.Facebook,
        IdempotencyKey = SocialPost.BuildIdempotencyKey(SocialPostKind.EventResults, EventId, SocialChannel.Facebook)
            + (id == PostId ? string.Empty : $":{id}"),
        State = state,
        CreatedUtc = created ?? Now.AddHours(-3),
        ScheduledUtc = Now.AddHours(-3),
        GeneratedText = "Race results are in.",
    };

    /// <summary>
    /// Seeds a post with a row version, which the in-memory provider will not generate but does store
    /// and does enforce -- so the conflict path is exercised for real rather than assumed.
    /// </summary>
    /// <remarks>
    /// Where that provider stops: it never advances the version on update, so nothing here can catch
    /// the API handing back a stale one, and a write-then-write-again sequence cannot be tested at
    /// all. It also applies neither the value converters, nor [MaxLength], nor the check constraints,
    /// which is why the truncation guards are asserted against the constants rather than against a
    /// database that would reject them.
    /// </remarks>
    private async Task SeedAsync(SocialPost post)
    {
        await using var context = await dbFactory.CreateDbContextAsync();
        context.SocialPosts.Add(post);
        context.Entry(post).Property<uint>(TsContext.RowVersionProperty).CurrentValue = Version;
        await context.SaveChangesAsync();
    }

    private async Task<SocialPost> Reload()
    {
        await using var context = await dbFactory.CreateDbContextAsync();
        return (await context.SocialPosts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == PostId))!;
    }

    private static T Value<T>(ActionResult<T> result)
    {
        Assert.IsNull(result.Result, $"Expected a value but the request was refused with {result.Result?.GetType().Name}");
        Assert.IsNotNull(result.Value);
        return result.Value;
    }

    private void SetUser(string? name, string? roles = null, bool includeRealmAccess = true)
    {
        List<Claim> claims = [];
        if (name is not null)
            claims.Add(new Claim("preferred_username", name));
        if (roles is not null)
            claims.Add(new Claim(KeycloakConstants.RoleClaimType, roles));
        if (includeRealmAccess)
            claims.Add(new Claim(KeycloakConstants.RealmAccessClaimType, """{"roles":[]}""", "JSON"));

        var identity = new ClaimsIdentity(claims, "TestAuthType", "preferred_username", KeycloakConstants.RoleClaimType);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
    }

    /// <summary>
    /// Records deletes, and what the database said at the moment of each one, so the ordering rule --
    /// save the row, then release the images -- can be asserted rather than read.
    /// </summary>
    private sealed class StubStore(IDbContextFactory<TsContext> dbFactory) : ISocialImageStore
    {
        public List<string> Deleted { get; } = [];
        public List<SocialPostState?> StateWhenDeleted { get; } = [];
        public List<bool> PostGoneWhenDeleted { get; } = [];

        public Task<string> StoreAsync(
            SocialChannel channel, int eventId, int sessionId, CapturedImage image, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The review API never stores images.");

        public async Task<bool> DeleteAsync(string url, CancellationToken cancellationToken)
        {
            await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
            var post = await context.SocialPosts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == PostId, cancellationToken);

            Deleted.Add(url);
            StateWhenDeleted.Add(post?.State);
            PostGoneWhenDeleted.Add(post is null);
            return true;
        }
    }

    private sealed class TestSocialController(
        ILoggerFactory loggerFactory,
        IDbContextFactory<TsContext> tsContext,
        SocialImageCleanup imageCleanup,
        TimeProvider timeProvider)
        : SocialControllerBase(loggerFactory, tsContext, imageCleanup, timeProvider);

    #endregion
}
