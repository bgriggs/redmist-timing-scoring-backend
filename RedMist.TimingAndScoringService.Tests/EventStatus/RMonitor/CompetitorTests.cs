using RedMist.EventProcessor.EventStatus.RMonitor;

namespace RedMist.EventProcessor.Tests.EventStatus.RMonitor;

/// <summary>
/// A competitor is published to clients only when it is dirty, so the dirty flag is what decides
/// whether an entry list change is ever sent. It is set from the generated property change
/// notifications, which means a field that parses to the value it already had must not arm it.
/// </summary>
[TestClass]
public class CompetitorTests
{
    // $A,"1234BE","12X",52474,"John","Johnson","USA",5
    private static string[] ARecord(string reg = "1234BE", string number = "12X", string transponder = "52474",
        string first = "John", string last = "Johnson", string country = "USA", string classNumber = "5")
        => ["$A", $"\"{reg}\"", $"\"{number}\"", transponder, $"\"{first}\"", $"\"{last}\"", $"\"{country}\"", classNumber];

    // $COMP,"1234BE","12X",5,"John","Johnson","USA","CAMEL"
    private static string[] CompRecord(string number = "12X", string classNumber = "5", string additional = "CAMEL")
        => ["$COMP", "\"1234BE\"", $"\"{number}\"", classNumber, "\"John\"", "\"Johnson\"", "\"USA\"", $"\"{additional}\""];

    [TestMethod]
    public void ProcessA_StripsQuotesAndParsesNumericFields()
    {
        var competitor = new Competitor();

        competitor.ProcessA(ARecord());

        Assert.AreEqual("1234BE", competitor.RegistrationNumber);
        Assert.AreEqual("12X", competitor.Number);
        Assert.AreEqual(52474u, competitor.Transponder);
        Assert.AreEqual("John", competitor.FirstName);
        Assert.AreEqual("Johnson", competitor.LastName);
        Assert.AreEqual("USA", competitor.Country);
        Assert.AreEqual(5, competitor.ClassNumber);
    }

    /// <summary>
    /// A transponder or class that will not parse leaves the last known value rather than zeroing
    /// it - a car that loses its transponder id stops being matched to its passings.
    /// </summary>
    [TestMethod]
    public void ProcessA_WithUnparseableNumbers_KeepsThePreviousValues()
    {
        var competitor = new Competitor();
        competitor.ProcessA(ARecord());

        competitor.ProcessA(ARecord(transponder: "not-a-number", classNumber: "n/a"));

        Assert.AreEqual(52474u, competitor.Transponder);
        Assert.AreEqual(5, competitor.ClassNumber);
    }

    /// <summary>
    /// $COMP carries the class in a different position than $A and adds the team field.
    /// </summary>
    [TestMethod]
    public void ParseComp_ReadsClassFromItsOwnPositionAndTrimsTheTeam()
    {
        var competitor = new Competitor();

        competitor.ParseComp(CompRecord(classNumber: "7", additional: "  Camel Racing  "));

        Assert.AreEqual(7, competitor.ClassNumber);
        Assert.AreEqual("Camel Racing", competitor.AdditionalData);
    }

    /// <summary>
    /// The class name is looked up from the organization's metadata; an unmapped class falls back
    /// to the raw class number so the car is still grouped rather than dropped into a blank class.
    /// </summary>
    [TestMethod]
    public void ToEventEntry_WithoutAClassNameLookup_FallsBackToTheClassNumber()
    {
        var competitor = new Competitor();
        competitor.ParseComp(CompRecord(classNumber: "7"));

        var withoutLookup = competitor.ToEventEntry();
        var withUnmappedLookup = competitor.ToEventEntry(_ => null);
        var withLookup = competitor.ToEventEntry(n => n == 7 ? "GTU" : null);

        Assert.AreEqual("7", withoutLookup.Class);
        Assert.AreEqual("7", withUnmappedLookup.Class);
        Assert.AreEqual("GTU", withLookup.Class);
        Assert.AreEqual("John Johnson", withLookup.Name);
        Assert.AreEqual("12X", withLookup.Number);
    }

    /// <summary>
    /// The entry list is only republished for competitors that changed. Reading one clears the
    /// flag, so a competitor that has not changed since must yield nothing on the next pass.
    /// </summary>
    [TestMethod]
    public void ToEventEntryWhenDirtyWithReset_YieldsTheEntryOnceThenNothingUntilItChangesAgain()
    {
        var competitor = new Competitor();

        Assert.IsNull(competitor.ToEventEntryWhenDirtyWithReset(_ => null),
            "A competitor that has never been parsed has nothing to publish.");

        competitor.ProcessA(ARecord());
        var first = competitor.ToEventEntryWhenDirtyWithReset(_ => "GTU");
        Assert.IsNotNull(first);
        Assert.AreEqual("12X", first!.Number);

        Assert.IsNull(competitor.ToEventEntryWhenDirtyWithReset(_ => "GTU"),
            "Nothing changed, so nothing should be republished.");

        competitor.ProcessA(ARecord(last: "Smith"));
        var second = competitor.ToEventEntryWhenDirtyWithReset(_ => "GTU");
        Assert.IsNotNull(second);
        Assert.AreEqual("John Smith", second!.Name);
    }

    /// <summary>
    /// Re-parsing the same record changes nothing, so it must not make the competitor dirty. The
    /// relay replays its whole cached entry list on every reconnect, and a competitor that reads
    /// as dirty on each replay would republish the entire field for no reason.
    /// </summary>
    [TestMethod]
    public void ToEventEntryWhenDirtyWithReset_AfterReparsingTheSameRecord_YieldsNothing()
    {
        var competitor = new Competitor();
        competitor.ProcessA(ARecord());
        competitor.ToEventEntryWhenDirtyWithReset(_ => null);

        competitor.ProcessA(ARecord());

        Assert.IsNull(competitor.ToEventEntryWhenDirtyWithReset(_ => null));
    }
}
