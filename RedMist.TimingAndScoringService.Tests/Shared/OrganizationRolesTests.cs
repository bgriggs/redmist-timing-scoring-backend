using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventProcessor.Tests.Utilities;
using System.Globalization;

namespace RedMist.TimingAndScoringService.Tests.Shared;

/// <summary>
/// The one rule for whether a stored role confers administration.
/// </summary>
/// <remarks>
/// It exists in two forms because Entity Framework forces it to: a query predicate that translates
/// to SQL, and a method for values already in memory. Two forms is one more than the number of
/// rules, so they are pinned to each other here. The failure this guards against is one service
/// granting what another refuses.
/// </remarks>
[TestClass]
public class OrganizationRolesTests
{
    private CultureInfo previousCulture = null!;

    /// <summary>
    /// The query predicate is compiled and run by the CLR here, so <c>ToLower()</c> uses the
    /// thread's culture - where in production it is Postgres <c>lower()</c>. Two reasons to pin it:
    /// a Turkish locale folds "ADMIN" to "adm\u0131n" and would fail a row that is healthy in
    /// production, and this assembly runs method-level parallel while another test class assigns
    /// CurrentCulture on a worker thread, so leaving it ambient makes the result depend on which
    /// tests happen to be running alongside.
    /// </summary>
    [TestInitialize]
    public void Setup()
    {
        previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    }

    [TestCleanup]
    public void Cleanup() => CultureInfo.CurrentCulture = previousCulture;

    /// <summary>
    /// Both spellings are in the production data: organization creation writes "Admin", the
    /// administrator screen writes "admin".
    /// </summary>
    [TestMethod]
    [DataRow("admin", true)]
    [DataRow("Admin", true)]
    [DataRow("ADMIN", true)]
    [DataRow("viewer", false)]
    [DataRow("", false)]
    [DataRow("administrator", false)]
    [DataRow("adm", false)]
    // Not trimmed on purpose: Role is never free text, so a padded value is a row that should not
    // exist rather than one to quietly accept.
    [DataRow("admin ", false)]
    [DataRow(" admin", false)]
    public void TheTwoFormsOfTheRuleAgree(string role, bool expected)
    {
        Assert.AreEqual(expected, OrganizationRoles.IsAdmin(role),
            $"IsAdmin disagreed for '{role}'.");
        Assert.AreEqual(expected, MatchesQueryPredicate(role),
            $"The query predicate disagreed with IsAdmin for '{role}'.");
    }

    [TestMethod]
    public void ANullRole_IsNotAdministration()
    {
        Assert.IsFalse(OrganizationRoles.IsAdmin(null));
    }

    /// <summary>
    /// Runs the real predicate the authorization queries compose, over a single seeded row, so this
    /// measures the expression rather than a restatement of it.
    /// </summary>
    private static bool MatchesQueryPredicate(string role)
    {
        using var db = new TestDbContextFactory(new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options).CreateDbContext();

        db.UserOrganizationMappings.Add(new UserOrganizationMapping
        {
            Username = "someone@example.com",
            OrganizationId = 1,
            Role = role,
        });
        db.SaveChanges();

        return db.UserOrganizationMappings.Where(OrganizationRoles.AdministratorMappings).Any();
    }
}
