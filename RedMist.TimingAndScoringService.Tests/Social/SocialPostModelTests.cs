using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using RedMist.Database;
using RedMist.Database.Models;

namespace RedMist.TimingAndScoringService.Tests.Social;

/// <summary>
/// Guards the storage-level invariants the publishing pipeline depends on. These are model
/// assertions rather than round trips because the properties that matter -- a unique key, a filtered
/// index -- are declarations, and losing one would be silent until it produced a duplicate post.
/// </summary>
[TestClass]
public class SocialPostModelTests
{
    private static TsContext Context()
    {
        // TsContext only sets this switch on the branch that runs when options are NOT supplied,
        // and it changes how DateTime maps. Without it the model built here would disagree with
        // the one the migration was generated from, and any timestamp assertion would be a lie.
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

        // Reading the model needs no connection; nothing here touches a database.
        var options = new DbContextOptionsBuilder<TsContext>()
            .UseNpgsql("Host=localhost;Database=redmist-timing-dev;Username=postgres;Password=")
            .Options;
        return new TsContext(options);
    }

    /// <summary>
    /// Check constraints and value converters are stripped from the read-optimized runtime model, so
    /// assertions about them have to read the design-time one -- the same model the migration was
    /// generated from.
    /// </summary>
    private static IModel DesignModel(TsContext context) =>
        context.GetService<IDesignTimeModel>().Model;

    /// <summary>
    /// The single guarantee that keeps a retried compose job from producing a second draft, and
    /// ultimately a second public post, about the same event.
    /// </summary>
    [TestMethod]
    public void IdempotencyKey_IsUniquelyIndexed()
    {
        using var context = Context();
        var entity = context.Model.FindEntityType(typeof(SocialPost))!;

        var index = entity.GetIndexes().SingleOrDefault(
            i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(SocialPost.IdempotencyKey));

        Assert.IsNotNull(index, "IdempotencyKey must be indexed");
        Assert.IsTrue(index.IsUnique, "The index must be unique or it guarantees nothing");
    }

    [TestMethod]
    public void ThePublishClaimQuery_IsIndexed()
    {
        using var context = Context();
        var entity = context.Model.FindEntityType(typeof(SocialPost))!;

        var index = entity.GetIndexes().SingleOrDefault(i =>
            i.Properties.Select(p => p.Name).SequenceEqual([nameof(SocialPost.State), nameof(SocialPost.ScheduledUtc)]));

        Assert.IsNotNull(index, "The publish job filters on State and ScheduledUtc");
    }

    /// <summary>
    /// Stored as text so a person reading the table during an incident sees "Publishing" rather than
    /// an ordinal, and so inserting an enum member cannot renumber existing rows underneath.
    /// </summary>
    [TestMethod]
    [DataRow(nameof(SocialPost.Kind))]
    [DataRow(nameof(SocialPost.Channel))]
    [DataRow(nameof(SocialPost.State))]
    public void Enums_AreStoredAsText(string propertyName)
    {
        using var context = Context();
        var property = context.Model.FindEntityType(typeof(SocialPost))!.FindProperty(propertyName)!;

        Assert.AreEqual(typeof(string), property.GetProviderClrType(),
            $"{propertyName} should persist as text");
    }

    [TestMethod]
    public void TheDigestIsStoredAsJsonb_SoDraftsStayQueryable()
    {
        using var context = Context();
        var property = context.Model.FindEntityType(typeof(SocialPost))!
            .FindProperty(nameof(SocialPost.DigestJson))!;

        Assert.AreEqual("jsonb", property.GetColumnType());
    }

    /// <summary>
    /// "Which prompt is live" has to be answerable by construction rather than by whoever last
    /// remembered to deactivate the previous one.
    /// </summary>
    [TestMethod]
    public void AtMostOnePromptPerKindAndChannel_CanBeActive()
    {
        using var context = Context();
        var entity = context.Model.FindEntityType(typeof(SocialPrompt))!;

        var filtered = entity.GetIndexes().SingleOrDefault(i =>
            i.Properties.Select(p => p.Name).SequenceEqual([nameof(SocialPrompt.Kind), nameof(SocialPrompt.Channel)])
            && i.IsUnique);

        Assert.IsNotNull(filtered, "Expected a unique index on (Kind, Channel)");
        Assert.IsFalse(string.IsNullOrWhiteSpace(filtered.GetFilter()),
            "Without a filter this would allow only one prompt version ever, not one active version");
    }

    [TestMethod]
    public void PromptVersions_AreUniquePerKindAndChannel()
    {
        using var context = Context();
        var entity = context.Model.FindEntityType(typeof(SocialPrompt))!;

        var index = entity.GetIndexes().SingleOrDefault(i =>
            i.Properties.Select(p => p.Name).SequenceEqual(
                [nameof(SocialPrompt.Kind), nameof(SocialPrompt.Channel), nameof(SocialPrompt.Version)]));

        Assert.IsNotNull(index);
        Assert.IsTrue(index.IsUnique,
            "A post stamps a version; two rows sharing one would make that stamp ambiguous");
    }

    [TestMethod]
    public void EditedText_TakesPrecedenceOverGeneratedText()
    {
        var post = new SocialPost { GeneratedText = "as written by the model" };
        Assert.AreEqual("as written by the model", post.EffectiveText);

        post.EditedText = "as corrected by a human";
        Assert.AreEqual("as corrected by a human", post.EffectiveText);
    }

    [TestMethod]
    public void APostWithNoTextAtAll_HasNoEffectiveText()
    {
        Assert.IsNull(new SocialPost().EffectiveText);
    }

    /// <summary>
    /// The unique index only holds if every writer formats the key identically, so the format lives
    /// in one place and these pin it.
    /// </summary>
    [TestMethod]
    public void IdempotencyKey_IsStableAndDistinguishesEventChannelAndKind()
    {
        var key = SocialPost.BuildIdempotencyKey(SocialPostKind.EventResults, 341, SocialChannel.Facebook);

        Assert.AreEqual("EventResults:341:Facebook", key);
        Assert.AreEqual(key, SocialPost.BuildIdempotencyKey(SocialPostKind.EventResults, 341, SocialChannel.Facebook));
        Assert.AreNotEqual(key, SocialPost.BuildIdempotencyKey(SocialPostKind.EventResults, 341, SocialChannel.Instagram));
        Assert.AreNotEqual(key, SocialPost.BuildIdempotencyKey(SocialPostKind.EventResults, 342, SocialChannel.Facebook));
        Assert.AreNotEqual(key, SocialPost.BuildIdempotencyKey(SocialPostKind.Manual, 341, SocialChannel.Facebook));
    }

    /// <summary>
    /// Without a discriminator every event-less post of a kind would share one key. Under the
    /// find-by-key-then-update pattern the second announcement would not fail loudly -- it would
    /// overwrite the first, including one already published, and be re-approved and posted again.
    /// </summary>
    [TestMethod]
    public void EventLessPosts_AreDistinguishedByTheirDiscriminator()
    {
        var first = SocialPost.BuildIdempotencyKey(
            SocialPostKind.FeatureAnnouncement, null, SocialChannel.Facebook, "v0.0.191");
        var second = SocialPost.BuildIdempotencyKey(
            SocialPostKind.FeatureAnnouncement, null, SocialChannel.Facebook, "v0.0.192");

        Assert.AreNotEqual(first, second);
        Assert.AreEqual(first, SocialPost.BuildIdempotencyKey(
            SocialPostKind.FeatureAnnouncement, null, SocialChannel.Facebook, "v0.0.191"));
    }

    [TestMethod]
    public void APostWithNeitherEventNorDiscriminator_IsRefused()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            SocialPost.BuildIdempotencyKey(SocialPostKind.FeatureAnnouncement, null, SocialChannel.Facebook));
    }

    /// <summary>
    /// The publish job claims a post with a conditional update whose predicate names the state.
    /// That only works if the enum converter is applied inside the predicate; if it ever compared
    /// against an ordinal instead, the claim would match nothing and publishing would stop dead.
    /// </summary>
    [TestMethod]
    public void TheClaimPredicate_ComparesStateAsText()
    {
        using var context = Context();

        var sql = context.SocialPosts
            .Where(p => p.State == SocialPostState.Approved && p.ScheduledUtc <= DateTime.UtcNow)
            .ToQueryString();

        StringAssert.Contains(sql, "'Approved'");
    }

    /// <summary>
    /// Pins the strings actually written to the database. Renaming an enum member would otherwise
    /// orphan every existing row silently, since the column stores names rather than ordinals.
    /// </summary>
    [TestMethod]
    [DataRow(SocialPostState.Draft, "Draft")]
    [DataRow(SocialPostState.PendingReview, "PendingReview")]
    [DataRow(SocialPostState.Approved, "Approved")]
    [DataRow(SocialPostState.Publishing, "Publishing")]
    [DataRow(SocialPostState.Published, "Published")]
    [DataRow(SocialPostState.Rejected, "Rejected")]
    [DataRow(SocialPostState.Failed, "Failed")]
    public void StateNames_AreStableOnDisk(SocialPostState state, string expected)
    {
        using var context = Context();
        var property = DesignModel(context).FindEntityType(typeof(SocialPost))!
            .FindProperty(nameof(SocialPost.State))!;
        var converter = property.GetValueConverter() ?? property.GetTypeMapping().Converter!;

        Assert.AreEqual(expected, converter.ConvertToProvider(state));
    }

    /// <summary>
    /// The Publishing state is a trap that must survive until a human clears it. Without a
    /// concurrency token an ordinary save from a stale page would put the row back to Approved,
    /// and the same result would be published a second time.
    /// </summary>
    [TestMethod]
    public void EveryWrite_IsGuardedByAConcurrencyToken()
    {
        using var context = Context();
        var entity = context.Model.FindEntityType(typeof(SocialPost))!;

        Assert.IsTrue(entity.GetProperties().Any(p => p.IsConcurrencyToken),
            "SocialPost writes must not be last-write-wins");
    }

    /// <summary>
    /// A value outside the enum fails during materialization, so one bad hand-edit would make every
    /// query against the table throw rather than just the offending row.
    /// </summary>
    [TestMethod]
    public void TheDatabaseEnforcesTheStateVocabulary()
    {
        using var context = Context();
        var check = DesignModel(context).FindEntityType(typeof(SocialPost))!
            .GetCheckConstraints().SingleOrDefault(c => c.Name == "CK_SocialPosts_State");

        Assert.IsNotNull(check);
        foreach (var name in Enum.GetNames<SocialPostState>())
            StringAssert.Contains(check.Sql, $"'{name}'");
    }

    [TestMethod]
    public void ABlankEdit_FallsBackToTheGeneratedCopy_RatherThanPublishingNothing()
    {
        var post = new SocialPost { GeneratedText = "as written by the model", EditedText = "   " };

        Assert.AreEqual("as written by the model", post.EffectiveText);
    }
}
