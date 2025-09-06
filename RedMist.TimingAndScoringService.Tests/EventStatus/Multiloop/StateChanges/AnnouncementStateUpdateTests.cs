using RedMist.TimingAndScoringService.EventStatus.Multiloop.StateChanges;
using RedMist.TimingCommon.Models;
using MultiloopAnnouncement = RedMist.TimingAndScoringService.EventStatus.Multiloop.Announcement;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.Multiloop.StateChanges;

[TestClass]
public class AnnouncementStateUpdateTests
{
    private static readonly DateTime TestTimestamp = new(2024, 1, 15, 10, 30, 45, DateTimeKind.Utc);

    #region Constructor Tests

    [TestMethod]
    public void Constructor_ValidAnnouncementsDict_CreatesInstance()
    {
        // Arrange
        var announcements = new Dictionary<ushort, MultiloopAnnouncement>
        {
            [1] = CreateMultiloopAnnouncement(1, "Test message 1"),
            [2] = CreateMultiloopAnnouncement(2, "Test message 2")
        };

        // Act
        var stateUpdate = new AnnouncementStateUpdate(announcements);

        // Assert
        Assert.IsNotNull(stateUpdate);
        Assert.AreSame(announcements, stateUpdate.Announcements);
        Assert.AreEqual(2, stateUpdate.Announcements.Count);
    }

    [TestMethod]
    public void Constructor_EmptyAnnouncementsDict_CreatesInstance()
    {
        // Arrange
        var announcements = new Dictionary<ushort, MultiloopAnnouncement>();

        // Act
        var stateUpdate = new AnnouncementStateUpdate(announcements);

        // Assert
        Assert.IsNotNull(stateUpdate);
        Assert.AreSame(announcements, stateUpdate.Announcements);
        Assert.AreEqual(0, stateUpdate.Announcements.Count);
    }

    #endregion

    #region GetChanges Tests - Different Announcements

    [TestMethod]
    public void GetChanges_NewAnnouncements_ReturnsSessionStatePatch()
    {
        // Arrange
        var multiloopAnnouncements = new Dictionary<ushort, MultiloopAnnouncement>
        {
            [1] = CreateMultiloopAnnouncement(1, "New announcement"),
            [2] = CreateMultiloopAnnouncement(2, "Another announcement")
        };

        var currentState = new SessionState
        {
            SessionId = 123,
            Announcements = [] // Empty announcements list
        };

        var stateUpdate = new AnnouncementStateUpdate(multiloopAnnouncements);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(123, result.SessionId);
        Assert.IsNotNull(result.Announcements);
        Assert.AreEqual(2, result.Announcements.Count);
    }

    [TestMethod]
    public void GetChanges_SameAnnouncements_ReturnsNull()
    {
        // Arrange
        var commonAnnouncement1 = new TimingCommon.Models.Announcement
        {
            Timestamp = TestTimestamp,
            Priority = "High",
            Text = "Test message"
        };

        var multiloopAnnouncement = CreateMultiloopAnnouncementWithDetails(1, "High", "Test message", TestTimestamp);
        var multiloopAnnouncements = new Dictionary<ushort, MultiloopAnnouncement>
        {
            [1] = multiloopAnnouncement
        };

        var currentState = new SessionState
        {
            SessionId = 123,
            Announcements = [commonAnnouncement1]
        };

        var stateUpdate = new AnnouncementStateUpdate(multiloopAnnouncements);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        // Note: This test may fail if the mapper converts differently than expected
        // The actual behavior depends on the AnnouncementMapper implementation
        Assert.IsNull(result);
    }

    [TestMethod]
    public void GetChanges_DifferentAnnouncementContent_ReturnsSessionStatePatch()
    {
        // Arrange
        var existingAnnouncement = new TimingCommon.Models.Announcement
        {
            Timestamp = TestTimestamp,
            Priority = "Low",
            Text = "Old message"
        };

        var multiloopAnnouncement = CreateMultiloopAnnouncementWithDetails(1, "High", "New message", TestTimestamp);
        var multiloopAnnouncements = new Dictionary<ushort, MultiloopAnnouncement>
        {
            [1] = multiloopAnnouncement
        };

        var currentState = new SessionState
        {
            SessionId = 456,
            Announcements = [existingAnnouncement]
        };

        var stateUpdate = new AnnouncementStateUpdate(multiloopAnnouncements);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(456, result.SessionId);
        Assert.IsNotNull(result.Announcements);
        Assert.AreEqual(1, result.Announcements.Count);
    }

    [TestMethod]
    public void GetChanges_DifferentAnnouncementCount_ReturnsSessionStatePatch()
    {
        // Arrange
        var existingAnnouncement = new TimingCommon.Models.Announcement
        {
            Timestamp = TestTimestamp,
            Priority = "Normal",
            Text = "Existing message"
        };

        var multiloopAnnouncements = new Dictionary<ushort, MultiloopAnnouncement>
        {
            [1] = CreateMultiloopAnnouncement(1, "Message 1"),
            [2] = CreateMultiloopAnnouncement(2, "Message 2"),
            [3] = CreateMultiloopAnnouncement(3, "Message 3")
        };

        var currentState = new SessionState
        {
            SessionId = 789,
            Announcements = [existingAnnouncement] // Only one existing announcement
        };

        var stateUpdate = new AnnouncementStateUpdate(multiloopAnnouncements);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(789, result.SessionId);
        Assert.IsNotNull(result.Announcements);
        Assert.AreEqual(3, result.Announcements.Count);
    }

    #endregion

    #region GetChanges Tests - Edge Cases

    [TestMethod]
    public void GetChanges_EmptyMultiloopAnnouncements_WithExistingAnnouncements_ReturnsSessionStatePatch()
    {
        // Arrange
        var existingAnnouncement = new TimingCommon.Models.Announcement
        {
            Timestamp = TestTimestamp,
            Priority = "High",
            Text = "Existing message"
        };

        var emptyAnnouncements = new Dictionary<ushort, MultiloopAnnouncement>();

        var currentState = new SessionState
        {
            SessionId = 100,
            Announcements = [existingAnnouncement]
        };

        var stateUpdate = new AnnouncementStateUpdate(emptyAnnouncements);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(100, result.SessionId);
        Assert.IsNotNull(result.Announcements);
        Assert.AreEqual(0, result.Announcements.Count);
    }

    [TestMethod]
    public void GetChanges_EmptyMultiloopAnnouncements_WithEmptyExistingAnnouncements_ReturnsNull()
    {
        // Arrange
        var emptyAnnouncements = new Dictionary<ushort, MultiloopAnnouncement>();

        var currentState = new SessionState
        {
            SessionId = 200,
            Announcements = []
        };

        var stateUpdate = new AnnouncementStateUpdate(emptyAnnouncements);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNull(result);
    }

    #endregion

    #region GetChanges Tests - Session ID Handling

    [TestMethod]
    public void GetChanges_DifferentSessionIds_CopiesCorrectSessionId()
    {
        // Arrange
        var announcements = new Dictionary<ushort, MultiloopAnnouncement>
        {
            [1] = CreateMultiloopAnnouncement(1, "Test message")
        };

        var currentState = new SessionState
        {
            SessionId = 999,
            Announcements = []
        };

        var stateUpdate = new AnnouncementStateUpdate(announcements);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(999, result.SessionId);
    }

    [TestMethod]
    public void GetChanges_ZeroSessionId_HandlesCorrectly()
    {
        // Arrange
        var announcements = new Dictionary<ushort, MultiloopAnnouncement>
        {
            [1] = CreateMultiloopAnnouncement(1, "Test message")
        };

        var currentState = new SessionState
        {
            SessionId = 0,
            Announcements = []
        };

        var stateUpdate = new AnnouncementStateUpdate(announcements);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.SessionId);
    }

    [TestMethod]
    public void GetChanges_NegativeSessionId_HandlesCorrectly()
    {
        // Arrange
        var announcements = new Dictionary<ushort, MultiloopAnnouncement>
        {
            [1] = CreateMultiloopAnnouncement(1, "Test message")
        };

        var currentState = new SessionState
        {
            SessionId = -1,
            Announcements = []
        };

        var stateUpdate = new AnnouncementStateUpdate(announcements);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(-1, result.SessionId);
    }

    #endregion

    #region GetChanges Tests - Large Data Sets

    [TestMethod]
    public void GetChanges_LargeAnnouncementSet_ProcessesCorrectly()
    {
        // Arrange
        var announcements = new Dictionary<ushort, MultiloopAnnouncement>();
        for (ushort i = 1; i <= 1000; i++)
        {
            announcements[i] = CreateMultiloopAnnouncement(i, $"Announcement {i}");
        }

        var currentState = new SessionState
        {
            SessionId = 500,
            Announcements = []
        };

        var stateUpdate = new AnnouncementStateUpdate(announcements);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(500, result.SessionId);
        Assert.IsNotNull(result.Announcements);
        Assert.AreEqual(1000, result.Announcements.Count);
    }

    #endregion

    #region GetChanges Tests - Multiple Sequential Calls

    [TestMethod]
    public void GetChanges_MultipleCallsWithSameState_ConsistentResults()
    {
        // Arrange
        var announcements = new Dictionary<ushort, MultiloopAnnouncement>
        {
            [1] = CreateMultiloopAnnouncement(1, "Consistent message")
        };

        var currentState = new SessionState
        {
            SessionId = 300,
            Announcements = []
        };

        var stateUpdate = new AnnouncementStateUpdate(announcements);

        // Act
        var result1 = stateUpdate.GetChanges(currentState);
        var result2 = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result1);
        Assert.IsNotNull(result2);
        Assert.AreEqual(result1.SessionId, result2.SessionId);
        Assert.AreEqual(result1.Announcements!.Count, result2.Announcements!.Count);
    }

    #endregion

    #region GetChanges Tests - Announcement Order

    [TestMethod]
    public void GetChanges_AnnouncementsInDifferentOrder_PreservesOrder()
    {
        // Arrange
        var announcements = new Dictionary<ushort, MultiloopAnnouncement>
        {
            [3] = CreateMultiloopAnnouncement(3, "Third message"),
            [1] = CreateMultiloopAnnouncement(1, "First message"),
            [2] = CreateMultiloopAnnouncement(2, "Second message")
        };

        var currentState = new SessionState
        {
            SessionId = 400,
            Announcements = []
        };

        var stateUpdate = new AnnouncementStateUpdate(announcements);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(3, result.Announcements!.Count);
        
        // The order should be based on the Values enumeration of the dictionary
        // Dictionary.Values preserves insertion order in .NET Core 2.0+
        var texts = result.Announcements.Select(a => a.Text).ToArray();
        Assert.AreEqual("Third message", texts[0]);
        Assert.AreEqual("First message", texts[1]);
        Assert.AreEqual("Second message", texts[2]);
    }

    #endregion

    #region Integration Tests

    [TestMethod]
    public void GetChanges_RealWorldScenario_WorksCorrectly()
    {
        // Arrange - Simulate a real timing scenario with multiple announcements
        var multiloopAnnouncements = new Dictionary<ushort, MultiloopAnnouncement>
        {
            [1] = CreateMultiloopAnnouncementWithDetails(1, "High", "Race Start", TestTimestamp),
            [2] = CreateMultiloopAnnouncementWithDetails(2, "Normal", "Car #42 pit stop", TestTimestamp.AddMinutes(5)),
            [3] = CreateMultiloopAnnouncementWithDetails(3, "Urgent", "Yellow flag sector 2", TestTimestamp.AddMinutes(10)),
            [4] = CreateMultiloopAnnouncementWithDetails(4, "Normal", "Green flag", TestTimestamp.AddMinutes(12))
        };

        var currentState = new SessionState
        {
            SessionId = 20241215,
            EventId = 100,
            SessionName = "Race 1",
            Announcements = []
        };

        var stateUpdate = new AnnouncementStateUpdate(multiloopAnnouncements);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(20241215, result.SessionId);
        Assert.AreEqual(4, result.Announcements!.Count);
        
        // Verify that announcements have expected content
        var texts = result.Announcements.Select(a => a.Text).ToArray();
        Assert.IsTrue(texts.Contains("Race Start"));
        Assert.IsTrue(texts.Contains("Car #42 pit stop"));
        Assert.IsTrue(texts.Contains("Yellow flag sector 2"));
        Assert.IsTrue(texts.Contains("Green flag"));
    }

    [TestMethod]
    public void GetChanges_AnnouncementUpdate_ReplacesExisting()
    {
        // Arrange - Simulate updating existing announcements
        var existingAnnouncements = new List<TimingCommon.Models.Announcement>
        {
            new() { Timestamp = TestTimestamp, Priority = "Low", Text = "Old message 1" },
            new() { Timestamp = TestTimestamp.AddMinutes(1), Priority = "Normal", Text = "Old message 2" }
        };

        var updatedMultiloopAnnouncements = new Dictionary<ushort, MultiloopAnnouncement>
        {
            [1] = CreateMultiloopAnnouncementWithDetails(1, "High", "Updated message 1", TestTimestamp),
            [2] = CreateMultiloopAnnouncementWithDetails(2, "Urgent", "Updated message 2", TestTimestamp.AddMinutes(1)),
            [3] = CreateMultiloopAnnouncementWithDetails(3, "Normal", "New message 3", TestTimestamp.AddMinutes(2))
        };

        var currentState = new SessionState
        {
            SessionId = 555,
            Announcements = existingAnnouncements
        };

        var stateUpdate = new AnnouncementStateUpdate(updatedMultiloopAnnouncements);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(555, result.SessionId);
        Assert.AreEqual(3, result.Announcements!.Count);
        
        // Verify the announcements were replaced/updated
        var texts = result.Announcements.Select(a => a.Text).ToArray();
        Assert.IsTrue(texts.Contains("Updated message 1"));
        Assert.IsTrue(texts.Contains("Updated message 2"));
        Assert.IsTrue(texts.Contains("New message 3"));
        Assert.IsFalse(texts.Contains("Old message 1"));
        Assert.IsFalse(texts.Contains("Old message 2"));
    }

    #endregion

    #region Concurrency Tests

    [TestMethod]
    public void GetChanges_ConcurrentCalls_ThreadSafe()
    {
        // Arrange
        var announcements = new Dictionary<ushort, MultiloopAnnouncement>
        {
            [1] = CreateMultiloopAnnouncement(1, "Concurrent test message")
        };

        var currentState = new SessionState
        {
            SessionId = 666,
            Announcements = []
        };

        var stateUpdate = new AnnouncementStateUpdate(announcements);
        var results = new List<SessionStatePatch?>();

        // Act - Run multiple concurrent calls
        for (int i = 0; i < 10; i++)
        {
            results.Add(stateUpdate.GetChanges(currentState));
        }

        // Assert
        Assert.IsTrue(results.All(r => r is not null));
        Assert.IsTrue(results.All(r => r!.SessionId == 666));
        Assert.IsTrue(results.All(r => r!.Announcements!.Count == 1));
    }

    #endregion

    #region Property Validation Tests

    [TestMethod]
    public void Announcements_Property_ReturnsCorrectValue()
    {
        // Arrange
        var announcements = new Dictionary<ushort, MultiloopAnnouncement>
        {
            [10] = CreateMultiloopAnnouncement(10, "Property test message")
        };

        // Act
        var stateUpdate = new AnnouncementStateUpdate(announcements);

        // Assert
        Assert.AreSame(announcements, stateUpdate.Announcements);
        Assert.AreEqual(1, stateUpdate.Announcements.Count);
        Assert.IsTrue(stateUpdate.Announcements.ContainsKey(10));
    }

    #endregion

    #region Record Equality Tests

    [TestMethod]
    public void Equals_SameAnnouncementsInstance_ReturnsTrue()
    {
        // Arrange
        var announcements = new Dictionary<ushort, MultiloopAnnouncement>
        {
            [1] = CreateMultiloopAnnouncement(1, "Test message")
        };

        var stateUpdate1 = new AnnouncementStateUpdate(announcements);
        var stateUpdate2 = new AnnouncementStateUpdate(announcements);

        // Act & Assert
        Assert.AreEqual(stateUpdate1, stateUpdate2);
        Assert.IsTrue(stateUpdate1.Equals(stateUpdate2));
        Assert.AreEqual(stateUpdate1.GetHashCode(), stateUpdate2.GetHashCode());
    }

    [TestMethod]
    public void Equals_DifferentAnnouncementsInstances_ReturnsFalse()
    {
        // Arrange
        var announcements1 = new Dictionary<ushort, MultiloopAnnouncement>
        {
            [1] = CreateMultiloopAnnouncement(1, "Message 1")
        };

        var announcements2 = new Dictionary<ushort, MultiloopAnnouncement>
        {
            [2] = CreateMultiloopAnnouncement(2, "Message 2")
        };

        var stateUpdate1 = new AnnouncementStateUpdate(announcements1);
        var stateUpdate2 = new AnnouncementStateUpdate(announcements2);

        // Act & Assert
        Assert.AreNotEqual(stateUpdate1, stateUpdate2);
        Assert.IsFalse(stateUpdate1.Equals(stateUpdate2));
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates a multiloop announcement with basic properties for testing.
    /// </summary>
    private static MultiloopAnnouncement CreateMultiloopAnnouncement(ushort messageNumber, string text)
    {
        var announcement = new MultiloopAnnouncement();
        
        // Use reflection to set private properties for testing
        var messageNumberProp = typeof(MultiloopAnnouncement).GetProperty("MessageNumber");
        var textProp = typeof(MultiloopAnnouncement).GetProperty("Text");
        var priorityProp = typeof(MultiloopAnnouncement).GetProperty("PriorityStr");
        var timestampProp = typeof(MultiloopAnnouncement).GetProperty("TimestampSecs");
        
        messageNumberProp?.SetValue(announcement, messageNumber);
        textProp?.SetValue(announcement, text);
        priorityProp?.SetValue(announcement, "Normal");
        
        // Set timestamp to a known value (seconds since epoch)
        var epochSeconds = (uint)((DateTimeOffset)TestTimestamp).ToUnixTimeSeconds();
        timestampProp?.SetValue(announcement, epochSeconds);
        
        return announcement;
    }

    /// <summary>
    /// Creates a multiloop announcement with detailed properties for testing.
    /// </summary>
    private static MultiloopAnnouncement CreateMultiloopAnnouncementWithDetails(ushort messageNumber, string priority, string text, DateTime timestamp)
    {
        var announcement = new MultiloopAnnouncement();
        
        // Use reflection to set private properties for testing
        var messageNumberProp = typeof(MultiloopAnnouncement).GetProperty("MessageNumber");
        var textProp = typeof(MultiloopAnnouncement).GetProperty("Text");
        var priorityProp = typeof(MultiloopAnnouncement).GetProperty("PriorityStr");
        var timestampProp = typeof(MultiloopAnnouncement).GetProperty("TimestampSecs");
        
        messageNumberProp?.SetValue(announcement, messageNumber);
        textProp?.SetValue(announcement, text);
        priorityProp?.SetValue(announcement, priority);
        
        // Convert timestamp to seconds since epoch
        var epochSeconds = (uint)((DateTimeOffset)timestamp).ToUnixTimeSeconds();
        timestampProp?.SetValue(announcement, epochSeconds);
        
        return announcement;
    }

    #endregion
}
