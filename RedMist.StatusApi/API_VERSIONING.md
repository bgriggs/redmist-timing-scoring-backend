# RedMist API Versioning Strategy

## Overview

The RedMist APIs use URL path versioning with a base controller pattern to minimize code duplication while maintaining clear separation between API versions.

## Architecture

### Base Controller Pattern
- **Base Controllers**: Contain all shared logic across API versions (marked as `abstract`)
- **Version-Specific Controllers**: Inherit from base and only override methods that change between versions
- **No Code Duplication**: Common functionality lives in one place

### Example Structure
```
Controllers/
??? EventsControllerBase.cs          # Shared logic
??? V1/
?   ??? EventsController.cs          # V1-specific (inherits base)
??? V2/
    ??? EventsController.cs          # V2-specific (inherits base, overrides differences)
```

## RedMist.StatusApi

### URL Structure
- **V1**: `/v1/Events/{action}` or `/Events/{action}` (legacy)
- **V2**: `/v2/Events/{action}`

### API Versions

#### Version 1.0 (Current/Stable)
- **Endpoint**: `/v1/Events` or `/Events` (legacy)
- **Response Types**: 
  - `LoadSessionResults` returns `Payload` object
- **Status**: Supported
- **Deprecation**: None planned

#### Version 2.0 (Latest)
- **Endpoint**: `/v2/Events`
- **Response Types**: 
  - `LoadSessionResults` returns `SessionState` object (breaking change from V1)
- **Status**: Active
- **New Features**: Modern SessionState model with improved structure

### Implementation Details

**EventsControllerBase.cs** (Shared)
- All common endpoints (LoadEvents, LoadLiveEvents, LoadSessions, etc.)
- Protected helper methods
- Virtual methods allow version-specific overrides

**V1/EventsController.cs**
```csharp
[Route("v{version:apiVersion}/[controller]/[action]")]
[Route("[controller]/[action]")] // Legacy support
[ApiVersion("1.0")]
public class EventsController : EventsControllerBase
{
    // Only override what's different in V1
    public async Task<Payload?> LoadSessionResults(...)
    {
        // V1-specific implementation
    }
}
```

**V2/EventsController.cs**
```csharp
[Route("v{version:apiVersion}/[controller]/[action]")]
[ApiVersion("2.0")]
public class EventsController : EventsControllerBase
{
    // Only override what's different in V2
    public async Task<SessionState?> LoadSessionResults(...)
    {
        // V2-specific implementation
    }
}
```

## RedMist.EventManagement

### URL Structure
- **V1**: `/v1/{controller}/{action}` or `/{controller}/{action}` (legacy)

### Controllers

#### EventController (V1)
- `LoadEventSummaries` - Load event summaries
- `LoadEvent` - Load specific event
- `SaveNewEvent` - Create new event
- `UpdateEvent` - Update event
- `UpdateEventStatusActive` - Set active event
- `DeleteEvent` - Soft delete event

#### OrganizationController (V1)
- `LoadOrganization` - Load organization details
- `UpdateOrganization` - Update organization
- `GetControlLogStatistics` - Get control log stats

## Client Migration Guide

### Existing Clients
All existing clients continue to work without changes:
```http
GET /Events/LoadSessionResults?eventId=123&sessionId=456
GET /Event/LoadEvent?eventId=123
```

### New Clients (Recommended)
Use versioned URLs:
```http
GET /v1/Events/LoadSessionResults?eventId=123&sessionId=456
GET /v2/Events/LoadSessionResults?eventId=123&sessionId=456
```

## Configuration

API versioning is configured in `Program.cs`:

```csharp
builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1, 0);
    options.AssumeDefaultVersionWhenUnspecified = true;
    options.ReportApiVersions = true;
    options.ApiVersionReader = new UrlSegmentApiVersionReader();
})
.AddMvc()
.AddApiExplorer(options =>
{
    options.GroupNameFormat = "'v'VVV";
    options.SubstituteApiVersionInUrl = true;
});
```

## Response Headers

API version information is included in response headers:
- `api-supported-versions`: Lists all supported API versions
- `api-deprecated-versions`: Lists deprecated versions (if any)

## Best Practices

1. **Always specify version**: Use versioned URLs (`/v1/...` or `/v2/...`)
2. **Check response headers**: Monitor `api-supported-versions`
3. **Plan migrations**: Migrate within deprecation period
4. **Test thoroughly**: Validate with target API version

## Adding New Versions

### Step 1: Identify Changes
Determine which methods need version-specific behavior.

### Step 2: Update Base Controller
If adding new shared functionality, add to the base controller as a `virtual` method.

### Step 3: Create Versioned Controller
```csharp
[Route("v{version:apiVersion}/[controller]/[action]")]
[ApiVersion("3.0")]
public class EventsController : EventsControllerBase
{
    // Only override methods that differ in V3
    public override async Task<NewModel> SomeMethod(...)
    {
        // V3-specific implementation
    }
}
```

### Step 4: Document Changes
Update this file and communicate breaking changes to API consumers.

## Version History

| API | Version | Release | Major Changes |
|-----|---------|---------|---------------|
| StatusApi | 2.0 | 2025 | SessionState model for LoadSessionResults |
| StatusApi | 1.0 | 2024 | Initial versioned release with Payload model |
| EventManagement | 1.0 | 2025 | Initial versioned release |

## Support

For questions about API versioning or migration assistance, please contact the RedMist development team.
