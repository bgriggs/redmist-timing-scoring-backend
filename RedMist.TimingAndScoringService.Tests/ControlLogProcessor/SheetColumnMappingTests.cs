using RedMist.ControlLogs;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.Tests.ControlLogProcessor;

/// <summary>
/// <see cref="SheetColumnMapping"/> is the cell-to-property step of control log parsing: it compiles a
/// setter for the named <see cref="ControlLogEntry"/> property and decides, per the mapping's flags,
/// whether a given cell value is allowed to write. These tests pin the conditions under which a cell is
/// rejected, since a rejected timestamp cell is also what stops the sheet parse early.
/// </summary>
[TestClass]
public class SheetColumnMappingTests
{
    /// <summary>The converter the WRL/ChampCar/LuckyDog sheets use for their "Time" column.</summary>
    private static readonly Func<string, object> TimestampConvert = s => { DateTime.TryParse(s, out var dt); return dt; };

    #region Property resolution

    [TestMethod]
    public void SetEntryValue_PropertyNameNotOnControlLogEntry_ReturnsFalseWithoutThrowing()
    {
        var mapping = new SheetColumnMapping { SheetColumn = "Bogus", PropertyName = "NoSuchProperty" };
        var entry = new ControlLogEntry();

        Assert.IsFalse(mapping.SetEntryValue(entry, "anything"));
        // A second call must take the cached "no property" path rather than re-resolving and throwing.
        Assert.IsFalse(mapping.SetEntryValue(entry, "anything"));
    }

    [TestMethod]
    public void SetEntryValue_SameMappingReusedAcrossRows_WritesToEachEntry()
    {
        // The parser holds one mapping instance for the whole sheet and reuses it row after row, so the
        // cached/compiled setter must not become bound to the first entry it saw.
        var mapping = new SheetColumnMapping { SheetColumn = "Car #", PropertyName = "Car1" };
        var first = new ControlLogEntry();
        var second = new ControlLogEntry();

        mapping.SetEntryValue(first, "12");
        mapping.SetEntryValue(second, "99");

        Assert.AreEqual("12", first.Car1);
        Assert.AreEqual("99", second.Car1);
    }

    #endregion

    #region Blank-cell flags

    [TestMethod]
    public void SetEntryValue_SetIfNotNullOrEmpty_WhitespaceCell_IsRejectedAndLeavesExistingValue()
    {
        var mapping = new SheetColumnMapping { SheetColumn = "Cause", PropertyName = "Note", SetIfNotNullOrEmpty = true };
        var entry = new ControlLogEntry { Note = "contact in T3" };

        Assert.IsFalse(mapping.SetEntryValue(entry, "   "));
        Assert.AreEqual("contact in T3", entry.Note);
    }

    [TestMethod]
    public void SetEntryValue_WithoutSetIfNotNullOrEmpty_BlankCellOverwritesExistingValue()
    {
        // Mappings that do not opt in to SetIfNotNullOrEmpty write blanks straight through - this is why
        // the sheets set the flag on the columns that are allowed to fall back to another column.
        var mapping = new SheetColumnMapping { SheetColumn = "Note", PropertyName = "Note" };
        var entry = new ControlLogEntry { Note = "contact in T3" };

        Assert.IsTrue(mapping.SetEntryValue(entry, "   "));
        Assert.AreEqual("   ", entry.Note);
    }

    [TestMethod]
    public void SetEntryValue_SetIfTargetNotNullOrEmpty_TargetAlreadyPopulated_IsRejected()
    {
        // ChampCar maps both "Cause" and "Flag State" onto Note; "Flag State" is only a fallback.
        var flagState = new SheetColumnMapping
        {
            SheetColumn = "Flag State",
            PropertyName = "Note",
            SetIfNotNullOrEmpty = true,
            SetIfTargetNotNullOrEmpty = true
        };
        var entry = new ControlLogEntry { Note = "off course" };

        Assert.IsFalse(flagState.SetEntryValue(entry, "Full Course Yellow"));
        Assert.AreEqual("off course", entry.Note);
    }

    [TestMethod]
    public void SetEntryValue_SetIfTargetNotNullOrEmpty_TargetBlank_Writes()
    {
        var flagState = new SheetColumnMapping
        {
            SheetColumn = "Flag State",
            PropertyName = "Note",
            SetIfNotNullOrEmpty = true,
            SetIfTargetNotNullOrEmpty = true
        };
        var entry = new ControlLogEntry { Note = "   " };

        Assert.IsTrue(flagState.SetEntryValue(entry, "Full Course Yellow"));
        Assert.AreEqual("Full Course Yellow", entry.Note);
    }

    #endregion

    #region Conversion

    [TestMethod]
    public void SetEntryValue_ConvertThrows_IsRejectedAndLeavesEntryUnchanged()
    {
        var mapping = new SheetColumnMapping
        {
            SheetColumn = "Time",
            PropertyName = "Timestamp",
            Convert = _ => throw new FormatException("bad cell")
        };
        var entry = new ControlLogEntry { Timestamp = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc) };

        Assert.IsFalse(mapping.SetEntryValue(entry, "junk"));
        Assert.AreEqual(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), entry.Timestamp);
    }

    [TestMethod]
    public void SetEntryValue_ConvertReturnsWrongType_IsRejectedRatherThanThrowing()
    {
        // Car1 is a string; a converter handing back an int must not escape as an InvalidCastException.
        var mapping = new SheetColumnMapping { SheetColumn = "Car #", PropertyName = "Car1", Convert = _ => 42 };
        var entry = new ControlLogEntry { Car1 = "7" };

        Assert.IsFalse(mapping.SetEntryValue(entry, "42"));
        Assert.AreEqual("7", entry.Car1);
    }

    [TestMethod]
    [DataRow("not a time")]
    [DataRow("")]
    [DataRow("   ")]
    public void SetEntryValue_SheetTimestampConverter_UnparsableCell_AcceptsDefaultDateTime(string cell)
    {
        // The sheets' converter swallows the parse failure via TryParse and hands back default(DateTime),
        // so the mapping reports success. Nothing downstream sees a "missed timestamp" - the bad row is
        // instead dropped later because default(DateTime) fails the minimum-timestamp filter.
        var mapping = new SheetColumnMapping { SheetColumn = "Time", PropertyName = "Timestamp", IsRequired = true, Convert = TimestampConvert };
        var entry = new ControlLogEntry();

        Assert.IsTrue(mapping.SetEntryValue(entry, cell));
        Assert.AreEqual(default, entry.Timestamp);
    }

    // Cell text is not trimmed; that is asserted where it matters, at the parse level, by
    // GoogleSheetsControlLogParsingTests.ParseRows_CarNumbersAreNotTrimmed.

    #endregion

    #region Highlighting

    [TestMethod]
    [DataRow("Car1", true, false)]
    [DataRow("Car2", false, true)]
    public void SetCellHighlighted_SetsTheMatchingIsHighlightedFlag(string propertyName, bool expectCar1, bool expectCar2)
    {
        var mapping = new SheetColumnMapping { SheetColumn = "Car #", PropertyName = propertyName };
        var entry = new ControlLogEntry();

        mapping.SetEntryValue(entry, "12");
        mapping.SetCellHighlighted(entry);

        Assert.AreEqual(expectCar1, entry.IsCar1Highlighted);
        Assert.AreEqual(expectCar2, entry.IsCar2Highlighted);
    }

    [TestMethod]
    public void SetCellHighlighted_PropertyWithNoIsHighlightedCounterpart_IsANoOp()
    {
        // ControlLogEntry has no IsNoteHighlighted, and a shaded notes cell must not throw.
        var mapping = new SheetColumnMapping { SheetColumn = "Note", PropertyName = "Note" };
        var entry = new ControlLogEntry();

        mapping.SetEntryValue(entry, "spun");
        mapping.SetCellHighlighted(entry);

        Assert.IsFalse(entry.IsCar1Highlighted);
        Assert.IsFalse(entry.IsCar2Highlighted);
    }

    [TestMethod]
    public void SetCellHighlighted_CalledBeforeAnyValueIsSet_StillResolvesTheFlag()
    {
        var mapping = new SheetColumnMapping { SheetColumn = "Car #", PropertyName = "Car2" };
        var entry = new ControlLogEntry();

        mapping.SetCellHighlighted(entry);

        Assert.IsTrue(entry.IsCar2Highlighted);
    }

    [TestMethod]
    public void SetCellHighlighted_UnknownProperty_IsANoOp()
    {
        var mapping = new SheetColumnMapping { SheetColumn = "Bogus", PropertyName = "NoSuchProperty" };
        var entry = new ControlLogEntry();

        mapping.SetCellHighlighted(entry);

        Assert.IsFalse(entry.IsCar1Highlighted);
    }

    #endregion
}
