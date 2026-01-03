# Starting Position Processor Test Suite

## Overview
This document describes the comprehensive test suite created for the `StartingPositionProcessor` class, which is responsible for determining and maintaining starting positions for race cars based on historical lap data.

## Changes Made to Production Code

### StartingPositionProcessor.cs
Changed method accessibility from `private` to `internal` to enable unit testing:
- `CheckHistoricLapStartingPositionsAsync()` - Main method to check if historical lap processing is needed
- `UpdateInClassStartingPositionLookup()` - Updates in-class starting positions based on overall positions
- `UpdateStartingPositionsFromHistoricLapsAsync()` - Loads historical laps and determines starting positions
- `LoadStartingLapsAsync()` - Loads lap data from the database
- `GetLapNumberPriorToGreen()` - Static method to determine which lap was just before the green flag

These changes allow comprehensive testing without exposing the methods publicly.

## Test Coverage

### 1. GetLapNumberPriorToGreen Tests (6 tests)
Tests the core logic for determining which lap was immediately before the green flag:

- **NoGreenFlag_ReturnsNegative**: Verifies proper handling when no green flag exists in the lap data
- **GreenFlagOnLapZero_ReturnsNegative**: Ensures invalid scenario (green on lap 0) is detected
- **GreenFlagOnLapOne_ReturnsLapZero**: Tests standard scenario where green comes on lap 1
- **GreenFlagOnLapTwo_ReturnsLapOne**: Tests delayed green flag scenarios
- **MultipleCarsMixedFlags_ReturnsCorrectLap**: Complex scenario with multiple cars and flag states
- **EmptyList_ReturnsNegative**: Edge case handling for empty input

### 2. LoadStartingLapsAsync Tests (3 tests)
Tests the database loading functionality:

- **WithLapsInDatabase_ReturnsFilteredLaps**: Verifies correct filtering of laps 0-4
- **NoLapsInDatabase_ReturnsEmptyList**: Tests graceful handling of empty database
- **OnlyLoadsLapsUpToLapFour**: Ensures only relevant laps (0-4) are loaded even when more exist

### 3. UpdateStartingPositionsFromHistoricLapsAsync Tests (3 tests)
Tests the complete historical lap processing workflow:

- **ValidData_UpdatesStartingPositions**: Verifies successful position determination from historical data
- **NoGreenFlag_ReturnsFalse**: Tests handling when green flag cannot be determined
- **NoLaps_ReturnsFalse**: Tests handling when no lap data exists

### 4. UpdateStartingPosition Tests (3 tests)
Tests real-time position updates during race start:

- **YellowFlag_UpdatesPosition**: Verifies position capture during yellow flag
- **GreenFlag_UpdatesPosition**: Verifies position capture on green flag
- **RedFlag_DoesNotUpdatePosition**: Ensures positions not captured during inappropriate flags

### 5. CheckHistoricLapStartingPositionsAsync Tests (12 tests)
Comprehensive tests for the main entry point method that determines if historical lap processing should occur:

- **AlreadyCheckedSession_ReturnsFalse**: Verifies prevention of duplicate checks for the same session
- **HasStartingPositions_ReturnsFalse**: Tests early exit when positions already exist
- **LapTooEarly_ReturnsTrue**: Validates that method returns true but doesn't process when lap ? 3
- **WrongFlag_ReturnsTrue**: Tests that non-racing flags (e.g., Checkered) don't trigger processing
- **GreenFlag_ValidLap_NoHistoricalData_ReturnsTrue**: Tests behavior when conditions met but no data available
- **YellowFlag_ValidLap_WithHistoricalData_ReturnsTrue**: Verifies successful processing under Yellow flag
- **RedFlag_ValidLap_WithHistoricalData_ReturnsTrue**: Verifies successful processing under Red flag
- **Purple35Flag_ValidLap_WithHistoricalData_ReturnsTrue**: Verifies successful processing under Purple35 flag
- **SessionChange_ResetsCheck**: Tests that changing sessions allows new historical checks
- **ExactlyLapFour_DoesNotTriggerCheck**: Boundary condition test (lap = 3, not > 3)
- **LapFourPointOne_TriggersCheck**: Boundary condition test (lap = 4, which is > 3)
- **LogsInformationWhenChecking**: Validates proper logging during the historical check process

### 6. Real Data Integration Tests (4 tests)
Tests using actual CSV data file (`data-1767472981761.csv`) with 257 rows of real race data:

- **LoadRealCsvData_ProcessesCorrectly**: Validates CSV parsing and database loading
- **RealData_GetLapNumberPriorToGreen_ReturnsCorrectLap**: Verifies algorithm works with real data (determined lap 0 is correct)
- **RealData_UpdateStartingPositions_SetsCorrectPositions**: Comprehensive test ensuring positions are set correctly for all cars
- **RealData_VerifyStartingOrderMatchesLapZeroPositions**: Cross-validates that calculated starting positions match lap 0 positions

## Test Data

### Real CSV Data
The test suite includes integration tests using `data-1767472981761.csv`, which contains:
- Session ID: 67
- Event ID: 47
- Multiple car classes (GTU, GTO, GP1, GP2)
- Laps 0-4 for multiple cars
- Flag transitions from Yellow (0) to Green (1)
- JSON-serialized `CarPosition` data in each lap record

### Sample Cars in CSV
- Car #1, #10 (GTU class)
- Car #109 (GTU class, position 18 at start)
- Car #11, #111, #144, #119 (GTO class)
- Car #112, #118 (GP1 class)
- Car #106, #149 (GP2 class)

## Key Test Helpers

### CreateCarPosition
Creates test `CarPosition` objects with specified parameters.

### SeedDatabaseWithLaps
Seeds the test database with synthetic lap data for specific scenarios.

### LoadCsvIntoDatabase
Parses and loads the real CSV file into the test database, handling:
- CSV quote escaping
- JSON string unescaping
- Proper field parsing

### ParseCsvLine
Robust CSV parser that handles:
- Quoted fields with embedded commas
- Escaped quotes within fields
- Complex JSON data in fields

## Expected Behavior

### CheckHistoricLapStartingPositionsAsync Method Flow
1. **Session Check**: Returns `false` if session was already checked
2. **Starting Position Check**: Returns `false` if starting positions already exist (and marks session as checked)
3. **Condition Check**: If lap > 3 AND flag is racing condition (Green/Yellow/Red/Purple35):
   - Logs information about performing historical check
   - Calls `UpdateStartingPositionsFromHistoricLapsAsync`
   - Logs result (success or warning)
   - Marks session as checked
4. **Return Value**: Returns `true` if it completes the flow (even if conditions weren't met), `false` only for early exits

### Starting Position Determination
1. System loads laps 0-4 from database
2. Identifies the leader's first green flag lap
3. Uses the lap immediately prior to green as the "starting lineup"
4. Orders cars by their position on that lap
5. Sets both overall and in-class starting positions

### ExecuteAsync Service Behavior
The background service checks every 15 seconds for sessions that:
- Don't have starting positions determined yet
- Are currently active (lap > 3)
- Are in racing condition (Green, Yellow, Red, or Purple35 flags)

When conditions are met, it automatically recovers starting positions from historical lap data.

## Test Execution

All 31 tests pass successfully:
```
Test run summary: Passed!
  total: 31
  failed: 0
  succeeded: 31
  skipped: 0
```

## CSV Data Format

The CSV file contains these columns:
1. Id - Unique lap log identifier
2. EventId - Event identifier (47)
3. SessionId - Session identifier (67)
4. CarNumber - Car number as string
5. Timestamp - When lap was recorded
6. LapNumber - Lap number (0-4 in test data)
7. Flag - Flag state (0=Yellow, 1=Green, etc.)
8. LapData - JSON-serialized CarPosition object

The JSON in LapData includes fields like:
- `n`: Car number
- `ovp`: Overall position
- `clp`: Class position
- `llp`: Last lap completed
- `flg`: Flag state
- `class`: Car class
- And many more telemetry fields

## Test Statistics

- **Total Tests**: 31
- **GetLapNumberPriorToGreen**: 6 tests
- **LoadStartingLapsAsync**: 3 tests
- **UpdateStartingPositionsFromHistoricLapsAsync**: 3 tests
- **UpdateStartingPosition**: 3 tests
- **CheckHistoricLapStartingPositionsAsync**: 12 tests (NEW)
- **Real Data Integration**: 4 tests

## Future Enhancements

Potential areas for additional testing:
1. Performance tests with larger datasets
2. Concurrent access scenarios for the background service
3. Edge cases with lap data corruption
4. Testing with different flag transition scenarios (e.g., green-yellow-green)
5. Multi-session scenarios with rapid session changes
6. Testing ExecuteAsync with cancellation token scenarios
