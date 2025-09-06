using RedMist.TimingAndScoringService.EventStatus.RMonitor;
using RedMist.TimingAndScoringService.EventStatus.RMonitor.StateChanges;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.RMonitor.StateChanges;

[TestClass]
public class CompetitorStateUpdateTests
{
    #region Constructor Tests

    [TestMethod]
    public void Constructor_ValidParameters_CreatesInstance()
    {
        // Arrange
        var competitors = new List<Competitor>
        {
            CreateCompetitor("42", "John", "Doe", "USA", 1),
            CreateCompetitor("99", "Jane", "Smith", "CAN", 2)
        };
        var classes = new Dictionary<int, string>
        {
            { 1, "GT3" },
            { 2, "GTE" }
        };

        // Act
        var stateUpdate = new CompetitorStateUpdate(competitors, classes);

        // Assert
        Assert.IsNotNull(stateUpdate);
        Assert.AreSame(competitors, stateUpdate.Competitors);
        Assert.AreSame(classes, stateUpdate.Classes);
        Assert.AreEqual(2, stateUpdate.Competitors.Count);
        Assert.AreEqual(2, stateUpdate.Classes.Count);
    }

    [TestMethod]
    public void Constructor_EmptyCompetitors_CreatesInstance()
    {
        // Arrange
        var competitors = new List<Competitor>();
        var classes = new Dictionary<int, string>();

        // Act
        var stateUpdate = new CompetitorStateUpdate(competitors, classes);

        // Assert
        Assert.IsNotNull(stateUpdate);
        Assert.AreSame(competitors, stateUpdate.Competitors);
        Assert.AreSame(classes, stateUpdate.Classes);
        Assert.AreEqual(0, stateUpdate.Competitors.Count);
        Assert.AreEqual(0, stateUpdate.Classes.Count);
    }

    #endregion

    #region GetChanges Tests - Basic Functionality

    [TestMethod]
    public void GetChanges_WithCompetitors_ReturnsSessionStatePatch()
    {
        // Arrange
        var competitors = new List<Competitor>
        {
            CreateCompetitor("42", "John", "Doe", "USA", 1),
            CreateCompetitor("99", "Jane", "Smith", "CAN", 2)
        };
        var classes = new Dictionary<int, string>
        {
            { 1, "GT3" },
            { 2, "GTE" }
        };

        var currentState = new SessionState
        {
            SessionId = 123,
            EventEntries = []
        };

        var stateUpdate = new CompetitorStateUpdate(competitors, classes);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNotNull(result.EventEntries);
        Assert.AreEqual(2, result.EventEntries.Count);
        
        var entry42 = result.EventEntries.FirstOrDefault(e => e.Number == "42");
        var entry99 = result.EventEntries.FirstOrDefault(e => e.Number == "99");
        
        Assert.IsNotNull(entry42);
        Assert.AreEqual("John Doe", entry42.Name);
        Assert.AreEqual("GT3", entry42.Class);
        
        Assert.IsNotNull(entry99);
        Assert.AreEqual("Jane Smith", entry99.Name);
        Assert.AreEqual("GTE", entry99.Class);
    }

    [TestMethod]
    public void GetChanges_EmptyCompetitors_ReturnsEmptyEventEntries()
    {
        // Arrange
        var competitors = new List<Competitor>();
        var classes = new Dictionary<int, string>();

        var currentState = new SessionState
        {
            SessionId = 123,
            EventEntries = []
        };

        var stateUpdate = new CompetitorStateUpdate(competitors, classes);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNotNull(result.EventEntries);
        Assert.AreEqual(0, result.EventEntries.Count);
    }

    #endregion

    #region GetChanges Tests - Class Name Resolution

    [TestMethod]
    public void GetChanges_WithKnownClasses_ResolvesClassNames()
    {
        // Arrange
        var competitors = new List<Competitor>
        {
            CreateCompetitor("42", "John", "Doe", "USA", 1),
            CreateCompetitor("99", "Jane", "Smith", "CAN", 2),
            CreateCompetitor("7", "Bob", "Johnson", "GBR", 3)
        };
        var classes = new Dictionary<int, string>
        {
            { 1, "GT3" },
            { 2, "GTE Pro" },
            { 3, "GTE Am" }
        };

        var currentState = new SessionState { SessionId = 123 };
        var stateUpdate = new CompetitorStateUpdate(competitors, classes);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(3, result.EventEntries!.Count);
        
        var entry42 = result.EventEntries.First(e => e.Number == "42");
        var entry99 = result.EventEntries.First(e => e.Number == "99");
        var entry7 = result.EventEntries.First(e => e.Number == "7");
        
        Assert.AreEqual("GT3", entry42.Class);
        Assert.AreEqual("GTE Pro", entry99.Class);
        Assert.AreEqual("GTE Am", entry7.Class);
    }

    [TestMethod]
    public void GetChanges_WithUnknownClasses_UsesClassNumberAsString()
    {
        // Arrange
        var competitors = new List<Competitor>
        {
            CreateCompetitor("42", "John", "Doe", "USA", 5), // Unknown class
            CreateCompetitor("99", "Jane", "Smith", "CAN", 1) // Known class
        };
        var classes = new Dictionary<int, string>
        {
            { 1, "GT3" }
            // Class 5 is not defined
        };

        var currentState = new SessionState { SessionId = 123 };
        var stateUpdate = new CompetitorStateUpdate(competitors, classes);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(2, result.EventEntries!.Count);
        
        var entry42 = result.EventEntries.First(e => e.Number == "42");
        var entry99 = result.EventEntries.First(e => e.Number == "99");
        
        Assert.AreEqual("5", entry42.Class); // Should fallback to class number
        Assert.AreEqual("GT3", entry99.Class);
    }

    [TestMethod]
    public void GetChanges_WithEmptyClasses_UsesClassNumberAsString()
    {
        // Arrange
        var competitors = new List<Competitor>
        {
            CreateCompetitor("42", "John", "Doe", "USA", 1),
            CreateCompetitor("99", "Jane", "Smith", "CAN", 2)
        };
        var classes = new Dictionary<int, string>(); // Empty classes

        var currentState = new SessionState { SessionId = 123 };
        var stateUpdate = new CompetitorStateUpdate(competitors, classes);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(2, result.EventEntries!.Count);
        
        var entry42 = result.EventEntries.First(e => e.Number == "42");
        var entry99 = result.EventEntries.First(e => e.Number == "99");
        
        Assert.AreEqual("1", entry42.Class);
        Assert.AreEqual("2", entry99.Class);
    }

    #endregion

    #region GetChanges Tests - Competitor Data Mapping

    [TestMethod]
    public void GetChanges_MapsCompetitorDataCorrectly()
    {
        // Arrange
        var competitors = new List<Competitor>
        {
            CreateCompetitorWithTeam("42", "John", "Doe", "USA", 1, "Team Racing"),
            CreateCompetitorWithTeam("99", "Jane", "Smith", "CAN", 2, "Speed Demons")
        };
        var classes = new Dictionary<int, string>
        {
            { 1, "GT3" },
            { 2, "GTE" }
        };

        var currentState = new SessionState { SessionId = 123 };
        var stateUpdate = new CompetitorStateUpdate(competitors, classes);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(2, result.EventEntries!.Count);
        
        var entry42 = result.EventEntries.First(e => e.Number == "42");
        var entry99 = result.EventEntries.First(e => e.Number == "99");
        
        // Verify all mapped data
        Assert.AreEqual("42", entry42.Number);
        Assert.AreEqual("John Doe", entry42.Name);
        Assert.AreEqual("Team Racing", entry42.Team);
        Assert.AreEqual("GT3", entry42.Class);
        
        Assert.AreEqual("99", entry99.Number);
        Assert.AreEqual("Jane Smith", entry99.Name);
        Assert.AreEqual("Speed Demons", entry99.Team);
        Assert.AreEqual("GTE", entry99.Class);
    }

    [TestMethod]
    public void GetChanges_EmptyNames_HandlesGracefully()
    {
        // Arrange
        var competitors = new List<Competitor>
        {
            CreateCompetitor("42", "", "", "USA", 1),
            CreateCompetitor("99", "Jane", "", "CAN", 2),
            CreateCompetitor("7", "", "Smith", "GBR", 3)
        };
        var classes = new Dictionary<int, string> { { 1, "GT3" }, { 2, "GTE" }, { 3, "GT4" } };

        var currentState = new SessionState { SessionId = 123 };
        var stateUpdate = new CompetitorStateUpdate(competitors, classes);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(3, result.EventEntries!.Count);
        
        var entry42 = result.EventEntries.First(e => e.Number == "42");
        var entry99 = result.EventEntries.First(e => e.Number == "99");
        var entry7 = result.EventEntries.First(e => e.Number == "7");
        
        Assert.AreEqual("", entry42.Name); // Empty first and last name
        Assert.AreEqual("Jane", entry99.Name); // Only first name
        Assert.AreEqual("Smith", entry7.Name); // Only last name
    }

    [TestMethod]
    public void GetChanges_DifferentCarNumbers_HandlesVariety()
    {
        // Test various car number formats
        var testNumbers = new[] { "1", "42", "99X", "123", "007", "A1", "P1" };
        var competitors = testNumbers.Select((num, i) => 
            CreateCompetitor(num, $"Driver{i}", $"Last{i}", "USA", i + 1)).ToList();
        
        var classes = testNumbers.Select((_, i) => new { Key = i + 1, Value = $"Class{i + 1}" })
                                .ToDictionary(x => x.Key, x => x.Value);

        var currentState = new SessionState { SessionId = 123 };
        var stateUpdate = new CompetitorStateUpdate(competitors, classes);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(testNumbers.Length, result.EventEntries!.Count);
        
        foreach (var testNumber in testNumbers)
        {
            var entry = result.EventEntries.First(e => e.Number == testNumber);
            Assert.IsNotNull(entry, $"Entry should exist for car number: {testNumber}");
            Assert.AreEqual(testNumber, entry.Number);
        }
    }

    #endregion

    #region GetChanges Tests - Large Data Sets

    [TestMethod]
    public void GetChanges_LargeCompetitorSet_ProcessesEfficiently()
    {
        // Arrange
        var competitors = new List<Competitor>();
        var classes = new Dictionary<int, string>();
        
        // Create 100 competitors across 10 classes
        for (int i = 1; i <= 100; i++)
        {
            var classId = (i % 10) + 1;
            competitors.Add(CreateCompetitor($"{i}", $"Driver{i}", $"Last{i}", "USA", classId));
            
            if (!classes.ContainsKey(classId))
            {
                classes[classId] = $"Class{classId}";
            }
        }

        var currentState = new SessionState { SessionId = 123 };
        var stateUpdate = new CompetitorStateUpdate(competitors, classes);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(100, result.EventEntries!.Count);
        Assert.AreEqual(10, classes.Count);
        
        // Verify all entries have proper class assignments
        Assert.IsTrue(result.EventEntries.All(e => !string.IsNullOrEmpty(e.Class)));
    }

    #endregion

    #region Integration Tests

    [TestMethod]
    public void GetChanges_RealWorldRaceScenario_WorksCorrectly()
    {
        // Arrange - Simulate a real race entry list
        var competitors = new List<Competitor>
        {
            CreateCompetitorWithTeam("42", "Lewis", "Hamilton", "GBR", 1, "Mercedes AMG"),
            CreateCompetitorWithTeam("44", "George", "Russell", "GBR", 1, "Mercedes AMG"),
            CreateCompetitorWithTeam("1", "Max", "Verstappen", "NLD", 1, "Red Bull Racing"),
            CreateCompetitorWithTeam("11", "Sergio", "Pérez", "MEX", 1, "Red Bull Racing"),
            CreateCompetitorWithTeam("16", "Charles", "Leclerc", "MON", 2, "Ferrari"),
            CreateCompetitorWithTeam("55", "Carlos", "Sainz", "ESP", 2, "Ferrari")
        };
        
        var classes = new Dictionary<int, string>
        {
            { 1, "Formula 1" },
            { 2, "Formula 1" } // Same class, different teams
        };

        var currentState = new SessionState
        {
            SessionId = 20241215,
            SessionName = "Monaco Grand Prix",
            EventEntries = [] // Starting with empty entries
        };

        var stateUpdate = new CompetitorStateUpdate(competitors, classes);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(6, result.EventEntries!.Count);
        
        // Verify specific drivers
        var hamilton = result.EventEntries.First(e => e.Number == "42");
        var verstappen = result.EventEntries.First(e => e.Number == "1");
        var leclerc = result.EventEntries.First(e => e.Number == "16");
        
        Assert.AreEqual("Lewis Hamilton", hamilton.Name);
        Assert.AreEqual("Mercedes AMG", hamilton.Team);
        Assert.AreEqual("Formula 1", hamilton.Class);
        
        Assert.AreEqual("Max Verstappen", verstappen.Name);
        Assert.AreEqual("Red Bull Racing", verstappen.Team);
        Assert.AreEqual("Formula 1", verstappen.Class);
        
        Assert.AreEqual("Charles Leclerc", leclerc.Name);
        Assert.AreEqual("Ferrari", leclerc.Team);
        Assert.AreEqual("Formula 1", leclerc.Class);
    }

    [TestMethod]
    public void GetChanges_SportsCarRaceScenario_HandlesMultipleClasses()
    {
        // Arrange - Simulate a sports car race with multiple classes
        var competitors = new List<Competitor>
        {
            CreateCompetitorWithTeam("1", "Earl", "Bamber", "NZL", 1, "Porsche Penske"),
            CreateCompetitorWithTeam("2", "André", "Lotterer", "DEU", 1, "Porsche Penske"),
            CreateCompetitorWithTeam("50", "Antonio", "Fuoco", "ITA", 1, "Ferrari AF Corse"),
            CreateCompetitorWithTeam("51", "Alessandro", "Pier Guidi", "ITA", 1, "Ferrari AF Corse"),
            CreateCompetitorWithTeam("91", "Gianmaria", "Bruni", "ITA", 2, "Porsche GT Team"),
            CreateCompetitorWithTeam("92", "Michael", "Christensen", "DNK", 2, "Porsche GT Team"),
            CreateCompetitorWithTeam("64", "Corvette", "Racing", "USA", 2, "Corvette Racing"),
            CreateCompetitorWithTeam("63", "Corvette", "Racing", "USA", 2, "Corvette Racing")
        };
        
        var classes = new Dictionary<int, string>
        {
            { 1, "LMH" },    // Le Mans Hypercar
            { 2, "LMGT3" }   // Le Mans GT3
        };

        var currentState = new SessionState { SessionId = 456 };
        var stateUpdate = new CompetitorStateUpdate(competitors, classes);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(8, result.EventEntries!.Count);
        
        // Verify class distribution
        var lmhEntries = result.EventEntries.Where(e => e.Class == "LMH").ToList();
        var lmgt3Entries = result.EventEntries.Where(e => e.Class == "LMGT3").ToList();
        
        Assert.AreEqual(4, lmhEntries.Count);
        Assert.AreEqual(4, lmgt3Entries.Count);
    }

    [TestMethod]
    public void GetChanges_UpdateExistingEntries_ReplacesCompletely()
    {
        // Arrange - Simulate updating existing competitor list
        var existingEntries = new List<EventEntry>
        {
            new() { Number = "42", Name = "Old Driver", Team = "Old Team", Class = "Old Class" },
            new() { Number = "99", Name = "Another Old", Team = "Old Team 2", Class = "Old Class" }
        };

        var newCompetitors = new List<Competitor>
        {
            CreateCompetitorWithTeam("42", "New", "Driver", "USA", 1, "New Team"),
            CreateCompetitorWithTeam("7", "Fresh", "Face", "CAN", 2, "Fresh Team")
        };
        
        var classes = new Dictionary<int, string>
        {
            { 1, "New Class A" },
            { 2, "New Class B" }
        };

        var currentState = new SessionState
        {
            SessionId = 789,
            EventEntries = existingEntries
        };

        var stateUpdate = new CompetitorStateUpdate(newCompetitors, classes);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(2, result.EventEntries!.Count);
        
        // Verify old entries are replaced
        var updatedEntry42 = result.EventEntries.FirstOrDefault(e => e.Number == "42");
        var newEntry7 = result.EventEntries.FirstOrDefault(e => e.Number == "7");
        var oldEntry99 = result.EventEntries.FirstOrDefault(e => e.Number == "99");
        
        Assert.IsNotNull(updatedEntry42);
        Assert.AreEqual("New Driver", updatedEntry42.Name);
        Assert.AreEqual("New Team", updatedEntry42.Team);
        Assert.AreEqual("New Class A", updatedEntry42.Class);
        
        Assert.IsNotNull(newEntry7);
        Assert.AreEqual("Fresh Face", newEntry7.Name);
        
        Assert.IsNull(oldEntry99); // Should no longer exist
    }

    #endregion

    #region Property Validation Tests

    [TestMethod]
    public void Competitors_Property_ReturnsCorrectValue()
    {
        // Arrange
        var competitors = new List<Competitor>
        {
            CreateCompetitor("42", "John", "Doe", "USA", 1)
        };
        var classes = new Dictionary<int, string> { { 1, "GT3" } };

        // Act
        var stateUpdate = new CompetitorStateUpdate(competitors, classes);

        // Assert
        Assert.AreSame(competitors, stateUpdate.Competitors);
        Assert.AreSame(classes, stateUpdate.Classes);
    }

    #endregion

    #region Record Equality Tests

    [TestMethod]
    public void Equals_SameParameters_ReturnsTrue()
    {
        // Arrange
        var competitors = new List<Competitor>
        {
            CreateCompetitor("42", "John", "Doe", "USA", 1)
        };
        var classes = new Dictionary<int, string> { { 1, "GT3" } };

        var stateUpdate1 = new CompetitorStateUpdate(competitors, classes);
        var stateUpdate2 = new CompetitorStateUpdate(competitors, classes);

        // Act & Assert
        Assert.AreEqual(stateUpdate1, stateUpdate2);
        Assert.IsTrue(stateUpdate1.Equals(stateUpdate2));
        Assert.AreEqual(stateUpdate1.GetHashCode(), stateUpdate2.GetHashCode());
    }

    [TestMethod]
    public void Equals_DifferentParameters_ReturnsFalse()
    {
        // Arrange
        var competitors1 = new List<Competitor>
        {
            CreateCompetitor("42", "John", "Doe", "USA", 1)
        };
        var competitors2 = new List<Competitor>
        {
            CreateCompetitor("99", "Jane", "Smith", "CAN", 2)
        };
        var classes1 = new Dictionary<int, string> { { 1, "GT3" } };
        var classes2 = new Dictionary<int, string> { { 2, "GTE" } };

        var stateUpdate1 = new CompetitorStateUpdate(competitors1, classes1);
        var stateUpdate2 = new CompetitorStateUpdate(competitors2, classes2);

        // Act & Assert
        Assert.AreNotEqual(stateUpdate1, stateUpdate2);
        Assert.IsFalse(stateUpdate1.Equals(stateUpdate2));
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates a Competitor instance with the specified values for testing.
    /// </summary>
    private static Competitor CreateCompetitor(
        string number,
        string firstName,
        string lastName,
        string country,
        int classNumber)
    {
        var competitor = new Competitor();

        // Use reflection to set private properties for testing
        var numberProp = typeof(Competitor).GetProperty("Number");
        var firstNameProp = typeof(Competitor).GetProperty("FirstName");
        var lastNameProp = typeof(Competitor).GetProperty("LastName");
        var countryProp = typeof(Competitor).GetProperty("Country");
        var classNumberProp = typeof(Competitor).GetProperty("ClassNumber");

        numberProp?.SetValue(competitor, number);
        firstNameProp?.SetValue(competitor, firstName);
        lastNameProp?.SetValue(competitor, lastName);
        countryProp?.SetValue(competitor, country);
        classNumberProp?.SetValue(competitor, classNumber);

        return competitor;
    }

    /// <summary>
    /// Creates a Competitor instance with team data for testing.
    /// </summary>
    private static Competitor CreateCompetitorWithTeam(
        string number,
        string firstName,
        string lastName,
        string country,
        int classNumber,
        string team)
    {
        var competitor = CreateCompetitor(number, firstName, lastName, country, classNumber);

        // Set additional data (team) using reflection
        var additionalDataProp = typeof(Competitor).GetProperty("AdditionalData");
        additionalDataProp?.SetValue(competitor, team);

        return competitor;
    }

    #endregion
}
