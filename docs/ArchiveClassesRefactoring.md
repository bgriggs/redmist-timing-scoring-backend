# Archive Classes Refactoring Summary

## Overview
Refactored the archive utility classes to reduce code duplication by creating an abstract base class `BaseArchive` that encapsulates shared functionality across all archive implementations.

## Changes Made

### 1. Created BaseArchive Abstract Class
**File**: `RedMist.EventOrchestration\Utilities\BaseArchive.cs`

This abstract base class provides:
- **Shared dependencies**: `IDbContextFactory<TsContext>`, `IArchiveStorage`, `PurgeUtilities`, and `ILogger`
- **Common compression and upload logic**: `CompressAndUploadFileAsync()` method that handles GZip compression and uploading to storage
- **Shared file cleanup**: `CleanupFile()` method for removing temporary files
- **Generic JSON file writing**: `WriteToJsonFileAsync<T>()` method for batched writing of database records to JSON files
- **Database deletion with fallback**: `ExecuteDeleteWithFallbackAsync<T>()` method that handles both ExecuteDelete and in-memory database scenarios

### 2. Refactored Archive Classes

#### FlagsArchive
- Inherits from `BaseArchive`
- Reduced from ~180 lines to ~70 lines
- Uses base class methods for:
  - File writing via `WriteToJsonFileAsync()`
  - Compression/upload via `CompressAndUploadFileAsync()`
  - Database deletion via `ExecuteDeleteWithFallbackAsync()`
  - File cleanup via `CleanupFile()`

#### CompetitorMetadataArchive
- Inherits from `BaseArchive`
- Reduced from ~190 lines to ~70 lines
- Uses same base class methods as FlagsArchive

#### X2LogArchive
- Inherits from `BaseArchive`
- Reduced from ~320 lines to ~140 lines
- Handles two data types (loops and passings)
- Uses base class methods for both data types

#### EventLogArchive
- Inherits from `BaseArchive`
- Reduced code complexity while maintaining multi-file writing capability
- Uses base class methods for:
  - Event file compression/upload
  - Session file compression/upload (iterates over dictionary)
  - File cleanup

#### LapsLogArchive
- Inherits from `BaseArchive`
- Reduced code complexity while maintaining multi-file writing capability
- Uses base class methods for:
  - Session file compression/upload
  - Car-specific file compression/upload (iterates over dictionary)
  - File cleanup

## Benefits

### Code Reduction
- **Total lines removed**: ~500+ lines of duplicated code
- **Maintenance improvement**: Changes to compression, upload, or cleanup logic only need to be made once in the base class

### Consistency
- All archive classes now follow the same patterns and conventions
- Error handling and logging are consistent across all implementations

### Testability
- Base class methods can be tested independently
- Derived classes are simpler and easier to unit test

### Extensibility
- New archive classes can easily be added by inheriting from `BaseArchive`
- Common functionality is immediately available to new implementations

## Architecture Patterns

### Template Method Pattern
The base class provides template methods that derived classes can use as building blocks while maintaining flexibility for their specific needs.

### Dependency Injection
All classes use constructor injection for their dependencies, with the base class handling the common ones.

### Single Responsibility
Each class now focuses on its specific archiving logic while delegating common functionality to the base class.

## Notes

- **EventLogArchive** and **LapsLogArchive** retain their custom multi-file writing logic as this is specific to their requirements (session-based and car-based file splitting)
- The base class `WriteToJsonFileAsync()` method supports custom JSON serialization and progress callbacks for flexibility
- All existing functionality is preserved; this is a pure refactoring with no behavioral changes
