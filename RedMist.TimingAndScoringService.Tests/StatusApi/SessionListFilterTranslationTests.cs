using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.StatusApi.Controllers;

namespace RedMist.TimingAndScoringService.Tests.StatusApi;

/// <summary>
/// The session-list filter is an expression built around a captured DbContext, and it is used inside
/// the subquery that attaches sessions to an event as well as on its own. The rest of the controller
/// tests run on EF's InMemory provider, which evaluates everything in memory and so would pass just
/// as happily on a predicate the real provider cannot translate - the failure would only show up as
/// a 500 in production. These tests put the same expression through the Npgsql provider and read the
/// SQL back. No database is contacted: ToQueryString only translates.
/// </summary>
[TestClass]
public class SessionListFilterTranslationTests
{
    private static TsContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TsContext>()
            .UseNpgsql("Host=localhost;Database=translation-only;Username=none;Password=none")
            .Options;
        return new TsContext(options);
    }

    [TestMethod]
    public void HasSomethingToShow_OnItsOwn_TranslatesToExistsSubqueries()
    {
        using var db = CreateContext();

        var sql = db.Sessions
            .Where(s => s.EventId == 1)
            .Where(EventsControllerBase.HasSomethingToShow(db))
            .ToQueryString();

        StringAssert.Contains(sql, "\"SessionResults\"", "The results row has to be checked in SQL, not in memory.");
        StringAssert.Contains(sql, "\"CarLapLogs\"");
        StringAssert.Contains(sql, "EXISTS", "Both checks should stay as EXISTS subqueries rather than joins that multiply rows.");
    }

    /// <summary>
    /// The form LoadEvents and LoadEvent use. An expression used inside a projection is the one that
    /// most easily falls out of the translator.
    /// </summary>
    [TestMethod]
    public void HasSomethingToShow_InsideAnEventProjection_Translates()
    {
        using var db = CreateContext();

        var query = from e in db.Events
                    where e.Id == 1
                    select new
                    {
                        e.Id,
                        Sessions = db.Sessions.Where(s => s.EventId == e.Id).Where(EventsControllerBase.HasSomethingToShow(db)).ToArray()
                    };

        var sql = query.ToQueryString();

        StringAssert.Contains(sql, "\"SessionResults\"");
        StringAssert.Contains(sql, "\"CarLapLogs\"");
    }
}
