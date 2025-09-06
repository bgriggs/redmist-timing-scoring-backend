using RedMist.TimingAndScoringService.EventStatus.Multiloop.StateChanges;
using RedMist.TimingCommon.Models;
using MultiloopCompletedSection = RedMist.TimingAndScoringService.EventStatus.Multiloop.CompletedSection;
using TimingCommonCompletedSection = RedMist.TimingCommon.Models.CompletedSection;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.Multiloop.StateChanges;

[TestClass]
public class SectionStateUpdateTests
{
    #region Constructor Tests

    [TestMethod]
    public void Constructor_ValidParameters_CreatesInstance()
    {
        // Arrange
        var carNumber = "42";
        var multiloopSections = new List<MultiloopCompletedSection>
        {
            CreateMultiloopCompletedSection("42", "S1", 120000, 60000, 5),
            CreateMultiloopCompletedSection("42", "S2", 180000, 60000, 5)
        };

        // Act
        var stateUpdate = new SectionStateUpdate(carNumber, multiloopSections);

        // Assert
        Assert.IsNotNull(stateUpdate);
        Assert.AreEqual(carNumber, stateUpdate.CarNumber);
        Assert.AreSame(multiloopSections, stateUpdate.MultiloopCompletedSections);
        Assert.AreEqual(2, stateUpdate.MultiloopCompletedSections.Count);
    }

    [TestMethod]
    public void Constructor_EmptyCarNumber_CreatesInstance()
    {
        // Arrange
        var carNumber = "";
        var multiloopSections = new List<MultiloopCompletedSection>();

        // Act
        var stateUpdate = new SectionStateUpdate(carNumber, multiloopSections);

        // Assert
        Assert.IsNotNull(stateUpdate);
        Assert.AreEqual("", stateUpdate.CarNumber);
        Assert.AreSame(multiloopSections, stateUpdate.MultiloopCompletedSections);
    }

    #endregion

    #region GetChanges Tests - Different Sections

    [TestMethod]
    public void GetChanges_NewSections_ReturnsCarPositionPatch()
    {
        // Arrange
        var multiloopSections = new List<MultiloopCompletedSection>
        {
            CreateMultiloopCompletedSection("42", "S1", 120000, 60000, 5),
            CreateMultiloopCompletedSection("42", "S2", 180000, 60000, 5)
        };

        var currentState = new CarPosition
        {
            Number = "42",
            CompletedSections = [] // Empty sections list
        };

        var stateUpdate = new SectionStateUpdate("42", multiloopSections);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.IsNotNull(result.CompletedSections);
        Assert.AreEqual(2, result.CompletedSections.Count);
        
        // Verify the sections were mapped correctly
        var s1 = result.CompletedSections.FirstOrDefault(s => s.SectionId == "S1");
        var s2 = result.CompletedSections.FirstOrDefault(s => s.SectionId == "S2");
        Assert.IsNotNull(s1);
        Assert.IsNotNull(s2);
        Assert.AreEqual("42", s1.Number);
        Assert.AreEqual("42", s2.Number);
    }

    [TestMethod]
    public void GetChanges_SameSections_ReturnsEmptyPatch()
    {
        // Arrange
        var existingSection = new TimingCommonCompletedSection
        {
            Number = "42",
            SectionId = "S1",
            ElapsedTimeMs = 120000,
            LastSectionTimeMs = 60000,
            LastLap = 5
        };

        var multiloopSection = CreateMultiloopCompletedSection("42", "S1", 120000, 60000, 5);
        var multiloopSections = new List<MultiloopCompletedSection> { multiloopSection };

        var currentState = new CarPosition
        {
            Number = "42",
            CompletedSections = [existingSection]
        };

        var stateUpdate = new SectionStateUpdate("42", multiloopSections);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.IsNull(result.CompletedSections); // No change needed
    }

    [TestMethod]
    public void GetChanges_DifferentSectionContent_ReturnsCarPositionPatch()
    {
        // Arrange
        var existingSection = new TimingCommonCompletedSection
        {
            Number = "42",
            SectionId = "S1",
            ElapsedTimeMs = 100000, // Different
            LastSectionTimeMs = 50000, // Different
            LastLap = 4 // Different
        };

        var multiloopSection = CreateMultiloopCompletedSection("42", "S1", 120000, 60000, 5);
        var multiloopSections = new List<MultiloopCompletedSection> { multiloopSection };

        var currentState = new CarPosition
        {
            Number = "42",
            CompletedSections = [existingSection]
        };

        var stateUpdate = new SectionStateUpdate("42", multiloopSections);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.IsNotNull(result.CompletedSections);
        Assert.AreEqual(1, result.CompletedSections.Count);
        
        var updatedSection = result.CompletedSections.First();
        Assert.AreEqual("42", updatedSection.Number);
        Assert.AreEqual("S1", updatedSection.SectionId);
        Assert.AreEqual(120000, updatedSection.ElapsedTimeMs);
        Assert.AreEqual(60000, updatedSection.LastSectionTimeMs);
        Assert.AreEqual(5, updatedSection.LastLap);
    }

    [TestMethod]
    public void GetChanges_DifferentSectionCount_ReturnsCarPositionPatch()
    {
        // Arrange
        var existingSection = new TimingCommonCompletedSection
        {
            Number = "42",
            SectionId = "S1",
            ElapsedTimeMs = 120000,
            LastSectionTimeMs = 60000,
            LastLap = 5
        };

        var multiloopSections = new List<MultiloopCompletedSection>
        {
            CreateMultiloopCompletedSection("42", "S1", 120000, 60000, 5),
            CreateMultiloopCompletedSection("42", "S2", 180000, 60000, 5),
            CreateMultiloopCompletedSection("42", "S3", 240000, 60000, 5)
        };

        var currentState = new CarPosition
        {
            Number = "42",
            CompletedSections = [existingSection] // Only one existing section
        };

        var stateUpdate = new SectionStateUpdate("42", multiloopSections);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.IsNotNull(result.CompletedSections);
        Assert.AreEqual(3, result.CompletedSections.Count);
    }

    #endregion

    #region GetChanges Tests - Edge Cases

    [TestMethod]
    public void GetChanges_EmptyMultiloopSections_WithExistingSections_ReturnsCarPositionPatch()
    {
        // Arrange
        var existingSection = new TimingCommonCompletedSection
        {
            Number = "42",
            SectionId = "S1",
            ElapsedTimeMs = 120000,
            LastSectionTimeMs = 60000,
            LastLap = 5
        };

        var emptyMultiloopSections = new List<MultiloopCompletedSection>();

        var currentState = new CarPosition
        {
            Number = "42",
            CompletedSections = [existingSection]
        };

        var stateUpdate = new SectionStateUpdate("42", emptyMultiloopSections);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.IsNotNull(result.CompletedSections);
        Assert.AreEqual(0, result.CompletedSections.Count);
    }

    [TestMethod]
    public void GetChanges_EmptyMultiloopSections_WithEmptyExistingSections_ReturnsEmptyPatch()
    {
        // Arrange
        var emptyMultiloopSections = new List<MultiloopCompletedSection>();

        var currentState = new CarPosition
        {
            Number = "42",
            CompletedSections = []
        };

        var stateUpdate = new SectionStateUpdate("42", emptyMultiloopSections);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.IsNull(result.CompletedSections); // No change needed
    }

    #endregion

    #region GetChanges Tests - Car Number Handling

    [TestMethod]
    public void GetChanges_DifferentCarNumbers_CopiesStateCarNumber()
    {
        // Test various car number formats
        var testCases = new[]
        {
            "1",
            "42",
            "99X",
            "123",
            "007",
            "A1"
        };

        foreach (var carNumber in testCases)
        {
            // Arrange
            var multiloopSections = new List<MultiloopCompletedSection>
            {
                CreateMultiloopCompletedSection("42", "S1", 120000, 60000, 5) // Note: Different from car number
            };

            var currentState = new CarPosition
            {
                Number = carNumber, // This should be used in the patch
                CompletedSections = []
            };

            var stateUpdate = new SectionStateUpdate("42", multiloopSections);

            // Act
            var result = stateUpdate.GetChanges(currentState);

            // Assert
            Assert.IsNotNull(result, $"Result should not be null for car number: {carNumber}");
            Assert.AreEqual(carNumber, result.Number, $"Car number should match state for: {carNumber}");
        }
    }

    [TestMethod]
    public void GetChanges_EmptyCarNumber_CopiesEmptyNumber()
    {
        // Arrange
        var multiloopSections = new List<MultiloopCompletedSection>
        {
            CreateMultiloopCompletedSection("", "S1", 120000, 60000, 5)
        };

        var currentState = new CarPosition
        {
            Number = "",
            CompletedSections = []
        };

        var stateUpdate = new SectionStateUpdate("", multiloopSections);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("", result.Number);
    }

    [TestMethod]
    public void GetChanges_NullCarNumber_CopiesNullNumber()
    {
        // Arrange
        var multiloopSections = new List<MultiloopCompletedSection>
        {
            CreateMultiloopCompletedSection("42", "S1", 120000, 60000, 5)
        };

        var currentState = new CarPosition
        {
            Number = null,
            CompletedSections = []
        };

        var stateUpdate = new SectionStateUpdate("42", multiloopSections);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNull(result.Number);
    }

    #endregion

    #region GetChanges Tests - Large Data Sets

    [TestMethod]
    public void GetChanges_LargeSectionSet_ProcessesCorrectly()
    {
        // Arrange
        var multiloopSections = new List<MultiloopCompletedSection>();
        for (int i = 1; i <= 50; i++)
        {
            multiloopSections.Add(CreateMultiloopCompletedSection("42", $"S{i}", (uint)(60000 * i), 60000, 5));
        }

        var currentState = new CarPosition
        {
            Number = "42",
            CompletedSections = []
        };

        var stateUpdate = new SectionStateUpdate("42", multiloopSections);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.IsNotNull(result.CompletedSections);
        Assert.AreEqual(50, result.CompletedSections.Count);
    }

    #endregion

    #region GetChanges Tests - Multiple Sequential Calls

    [TestMethod]
    public void GetChanges_MultipleCallsWithSameState_ConsistentResults()
    {
        // Arrange
        var multiloopSections = new List<MultiloopCompletedSection>
        {
            CreateMultiloopCompletedSection("42", "S1", 120000, 60000, 5)
        };

        var currentState = new CarPosition
        {
            Number = "42",
            CompletedSections = []
        };

        var stateUpdate = new SectionStateUpdate("42", multiloopSections);

        // Act
        var result1 = stateUpdate.GetChanges(currentState);
        var result2 = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result1);
        Assert.IsNotNull(result2);
        Assert.AreEqual(result1.Number, result2.Number);
        Assert.AreEqual(result1.CompletedSections?.Count, result2.CompletedSections?.Count);
    }

    #endregion

    #region GetChanges Tests - Section Order

    [TestMethod]
    public void GetChanges_SectionsInDifferentOrder_PreservesOrder()
    {
        // Arrange
        var multiloopSections = new List<MultiloopCompletedSection>
        {
            CreateMultiloopCompletedSection("42", "S3", 240000, 60000, 5),
            CreateMultiloopCompletedSection("42", "S1", 120000, 60000, 5),
            CreateMultiloopCompletedSection("42", "S2", 180000, 60000, 5)
        };

        var currentState = new CarPosition
        {
            Number = "42",
            CompletedSections = []
        };

        var stateUpdate = new SectionStateUpdate("42", multiloopSections);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(3, result.CompletedSections!.Count);
        
        // The order should be preserved from the multiloop sections
        var sectionIds = result.CompletedSections.Select(s => s.SectionId).ToArray();
        Assert.AreEqual("S3", sectionIds[0]);
        Assert.AreEqual("S1", sectionIds[1]);
        Assert.AreEqual("S2", sectionIds[2]);
    }

    #endregion

    #region Integration Tests

    [TestMethod]
    public void GetChanges_RealWorldScenario_WorksCorrectly()
    {
        // Arrange - Simulate a real timing scenario with multiple track sections
        var multiloopSections = new List<MultiloopCompletedSection>
        {
            CreateMultiloopCompletedSection("42", "S1", 45000, 45000, 5), // First sector: 45 seconds
            CreateMultiloopCompletedSection("42", "S2", 95000, 50000, 5), // Second sector: 50 seconds (cumulative 95s)
            CreateMultiloopCompletedSection("42", "S3", 150000, 55000, 5), // Third sector: 55 seconds (cumulative 150s)
            CreateMultiloopCompletedSection("42", "SF", 180000, 30000, 5)  // Start/Finish: 30 seconds (cumulative 180s = 3:00 lap)
        };

        var currentState = new CarPosition
        {
            Number = "42",
            LastLapCompleted = 5,
            Class = "GT3",
            CompletedSections = [], // No previous sections
            LastLapTime = "03:00.000"
        };

        var stateUpdate = new SectionStateUpdate("42", multiloopSections);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.AreEqual(4, result.CompletedSections!.Count);
        
        // Verify the sections have expected timing data
        var s1 = result.CompletedSections.First(s => s.SectionId == "S1");
        var s2 = result.CompletedSections.First(s => s.SectionId == "S2");
        var s3 = result.CompletedSections.First(s => s.SectionId == "S3");
        var sf = result.CompletedSections.First(s => s.SectionId == "SF");
        
        Assert.AreEqual(45000, s1.ElapsedTimeMs);
        Assert.AreEqual(95000, s2.ElapsedTimeMs);
        Assert.AreEqual(150000, s3.ElapsedTimeMs);
        Assert.AreEqual(180000, sf.ElapsedTimeMs);
        
        // Verify that other properties are not touched
        Assert.IsNull(result.LastLapCompleted);
        Assert.IsNull(result.Class);
        Assert.IsNull(result.LastLapTime);
    }

    [TestMethod]
    public void GetChanges_SectionUpdate_ReplacesExisting()
    {
        // Arrange - Simulate updating existing sections with new timing data
        var existingSections = new List<TimingCommonCompletedSection>
        {
            new() { Number = "42", SectionId = "S1", ElapsedTimeMs = 50000, LastSectionTimeMs = 50000, LastLap = 4 },
            new() { Number = "42", SectionId = "S2", ElapsedTimeMs = 110000, LastSectionTimeMs = 60000, LastLap = 4 }
        };

        var updatedMultiloopSections = new List<MultiloopCompletedSection>
        {
            CreateMultiloopCompletedSection("42", "S1", 48000, 48000, 5), // Improved time
            CreateMultiloopCompletedSection("42", "S2", 105000, 57000, 5), // Improved time
            CreateMultiloopCompletedSection("42", "S3", 160000, 55000, 5)  // New section
        };

        var currentState = new CarPosition
        {
            Number = "42",
            CompletedSections = existingSections
        };

        var stateUpdate = new SectionStateUpdate("42", updatedMultiloopSections);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.AreEqual(3, result.CompletedSections!.Count);
        
        // Verify the sections were replaced/updated
        var sectionIds = result.CompletedSections.Select(s => s.SectionId).ToArray();
        Assert.IsTrue(sectionIds.Contains("S1"));
        Assert.IsTrue(sectionIds.Contains("S2"));
        Assert.IsTrue(sectionIds.Contains("S3"));
        
        // Verify improved times
        var s1 = result.CompletedSections.First(s => s.SectionId == "S1");
        var s2 = result.CompletedSections.First(s => s.SectionId == "S2");
        Assert.AreEqual(48000, s1.ElapsedTimeMs); // Improved from 50000
        Assert.AreEqual(105000, s2.ElapsedTimeMs); // Improved from 110000
    }

    [TestMethod]
    public void GetChanges_MultipleCarScenario_HandlesEachIndependently()
    {
        // Arrange - Test multiple cars with different section scenarios
        var car42Sections = new List<MultiloopCompletedSection>
        {
            CreateMultiloopCompletedSection("42", "S1", 45000, 45000, 5),
            CreateMultiloopCompletedSection("42", "S2", 95000, 50000, 5)
        };

        var car99Sections = new List<MultiloopCompletedSection>
        {
            CreateMultiloopCompletedSection("99", "S1", 47000, 47000, 3)
        };

        var car7Sections = new List<MultiloopCompletedSection>
        {
            CreateMultiloopCompletedSection("7", "S1", 44000, 44000, 8),
            CreateMultiloopCompletedSection("7", "S2", 92000, 48000, 8),
            CreateMultiloopCompletedSection("7", "S3", 145000, 53000, 8)
        };

        var car42State = new CarPosition { Number = "42", CompletedSections = [] };
        var car99State = new CarPosition { Number = "99", CompletedSections = [] };
        var car7State = new CarPosition { Number = "7", CompletedSections = [] };

        var car42StateUpdate = new SectionStateUpdate("42", car42Sections);
        var car99StateUpdate = new SectionStateUpdate("99", car99Sections);
        var car7StateUpdate = new SectionStateUpdate("7", car7Sections);

        // Act
        var result42 = car42StateUpdate.GetChanges(car42State);
        var result99 = car99StateUpdate.GetChanges(car99State);
        var result7 = car7StateUpdate.GetChanges(car7State);

        // Assert
        Assert.IsNotNull(result42);
        Assert.AreEqual("42", result42.Number);
        Assert.AreEqual(2, result42.CompletedSections!.Count);

        Assert.IsNotNull(result99);
        Assert.AreEqual("99", result99.Number);
        Assert.AreEqual(1, result99.CompletedSections!.Count);

        Assert.IsNotNull(result7);
        Assert.AreEqual("7", result7.Number);
        Assert.AreEqual(3, result7.CompletedSections!.Count);
    }

    #endregion

    #region Property Validation Tests

    [TestMethod]
    public void CarNumber_Property_ReturnsCorrectValue()
    {
        // Arrange
        var carNumber = "42";
        var multiloopSections = new List<MultiloopCompletedSection>
        {
            CreateMultiloopCompletedSection("42", "S1", 120000, 60000, 5)
        };

        // Act
        var stateUpdate = new SectionStateUpdate(carNumber, multiloopSections);

        // Assert
        Assert.AreEqual(carNumber, stateUpdate.CarNumber);
        Assert.AreSame(multiloopSections, stateUpdate.MultiloopCompletedSections);
    }

    #endregion

    #region Record Equality Tests

    [TestMethod]
    public void Equals_SameParameters_ReturnsTrue()
    {
        // Arrange
        var carNumber = "42";
        var multiloopSections = new List<MultiloopCompletedSection>
        {
            CreateMultiloopCompletedSection("42", "S1", 120000, 60000, 5)
        };

        var stateUpdate1 = new SectionStateUpdate(carNumber, multiloopSections);
        var stateUpdate2 = new SectionStateUpdate(carNumber, multiloopSections);

        // Act & Assert
        Assert.AreEqual(stateUpdate1, stateUpdate2);
        Assert.IsTrue(stateUpdate1.Equals(stateUpdate2));
        Assert.AreEqual(stateUpdate1.GetHashCode(), stateUpdate2.GetHashCode());
    }

    [TestMethod]
    public void Equals_DifferentParameters_ReturnsFalse()
    {
        // Arrange
        var multiloopSections1 = new List<MultiloopCompletedSection>
        {
            CreateMultiloopCompletedSection("42", "S1", 120000, 60000, 5)
        };

        var multiloopSections2 = new List<MultiloopCompletedSection>
        {
            CreateMultiloopCompletedSection("99", "S2", 130000, 65000, 6)
        };

        var stateUpdate1 = new SectionStateUpdate("42", multiloopSections1);
        var stateUpdate2 = new SectionStateUpdate("99", multiloopSections2);

        // Act & Assert
        Assert.AreNotEqual(stateUpdate1, stateUpdate2);
        Assert.IsFalse(stateUpdate1.Equals(stateUpdate2));
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates a multiloop CompletedSection instance with the specified values for testing.
    /// </summary>
    private static MultiloopCompletedSection CreateMultiloopCompletedSection(
        string number,
        string sectionIdentifier,
        uint elapsedTimeMs,
        uint lastSectionTimeMs,
        ushort lastLap)
    {
        var completedSection = new MultiloopCompletedSection();

        // Use reflection to set private properties for testing
        var numberProp = typeof(MultiloopCompletedSection).GetProperty("Number");
        var sectionIdentifierProp = typeof(MultiloopCompletedSection).GetProperty("SectionIdentifier");
        var elapsedTimeMsProp = typeof(MultiloopCompletedSection).GetProperty("ElapsedTimeMs");
        var lastSectionTimeMsProp = typeof(MultiloopCompletedSection).GetProperty("LastSectionTimeMs");
        var lastLapProp = typeof(MultiloopCompletedSection).GetProperty("LastLap");

        numberProp?.SetValue(completedSection, number);
        sectionIdentifierProp?.SetValue(completedSection, sectionIdentifier);
        elapsedTimeMsProp?.SetValue(completedSection, elapsedTimeMs);
        lastSectionTimeMsProp?.SetValue(completedSection, lastSectionTimeMs);
        lastLapProp?.SetValue(completedSection, lastLap);

        return completedSection;
    }

    #endregion
}
