# ?? API Versioning - Quick Reference

## Implementation Pattern: Inheritance-Based Versioning

### Structure
```
Controllers/
??? EventsControllerBase.cs           ? All shared logic (abstract, not routable)
??? OrganizationControllerBase.cs     ? All shared logic (abstract, not routable)
??? V1/
?   ??? EventsController.cs           ? V1: Inherits base, overrides differences
?   ??? OrganizationController.cs     ? V1: Inherits base
??? V2/
    ??? EventsController.cs           ? V2: Inherits base, overrides differences
```

### Key Principle
**DRY (Don't Repeat Yourself)**: Common code lives in the base, versions only contain differences.

## URL Patterns

### StatusApi - EventsController
| Route | Version | Returns |
|-------|---------|---------|
| `/Events/LoadSessionResults` | V1 (legacy) | `Payload` |
| `/v1/Events/LoadSessionResults` | V1 | `Payload` |
| `/v2/Events/LoadSessionResults` | V2 | `SessionState` |

### StatusApi - OrganizationController
| Route | Version |
|-------|---------|
| `/Organization/GetOrganizationIcon` | V1 (legacy) |
| `/v1/Organization/GetOrganizationIcon` | V1 |

### EventManagement
| Route | Version |
|-------|---------|
| `/Event/LoadEvent` | V1 (legacy) |
| `/v1/Event/LoadEvent` | V1 |
| `/Organization/LoadOrganization` | V1 (legacy) |
| `/v1/Organization/LoadOrganization` | V1 |

## Code Examples

### Base Controller (Shared Logic)
```csharp
public abstract class EventsControllerBase : ControllerBase
{
    // All common endpoints
    [HttpGet]
    public virtual async Task<Event[]> LoadEvents(DateTime startDateUtc)
    {
        // Shared implementation
    }
}
```

### V1 Controller (Specific Implementation)
```csharp
[Route("v{version:apiVersion}/[controller]/[action]")]
[Route("[controller]/[action]")] // Legacy
[ApiVersion("1.0")]
public class EventsController : EventsControllerBase
{
    [HttpGet]
    public async Task<Payload?> LoadSessionResults(int eventId, int sessionId)
    {
        // V1-specific: returns Payload
    }
}
```

### V2 Controller (Override Only What Changes)
```csharp
[Route("v{version:apiVersion}/[controller]/[action]")]
[ApiVersion("2.0")]
public class EventsController : EventsControllerBase
{
    [HttpGet]
    public async Task<SessionState?> LoadSessionResults(int eventId, int sessionId)
    {
        // V2-specific: returns SessionState
    }
}
```

## Benefits

? **No Code Duplication** - Common logic in one place  
? **Type Safety** - Compiler enforces consistency  
? **Easy Versioning** - Add V3 by inheriting and overriding  
? **Backward Compatible** - Legacy routes work automatically  
? **Maintainable** - Fix bugs once in base controller  

## Response Headers

All responses include:
- `api-supported-versions: 1.0, 2.0`
- `api-deprecated-versions: (none)` (when applicable)

## Client Usage

### Existing Clients (No Changes Required)
```http
GET /Events/LoadSessionResults?eventId=1&sessionId=2
GET /Organization/GetOrganizationIcon?organizationId=1
```
Automatically routed to V1

### New Clients (Recommended)
```http
GET /v1/Events/LoadSessionResults?eventId=1&sessionId=2
GET /v2/Events/LoadSessionResults?eventId=1&sessionId=2
GET /v1/Organization/GetOrganizationIcon?organizationId=1
```
Explicit version selection

## Files Modified/Created

### StatusApi
- ? `Controllers/EventsControllerBase.cs` (new)
- ? `Controllers/OrganizationControllerBase.cs` (new)
- ? `Controllers/V1/EventsController.cs` (new)
- ? `Controllers/V1/OrganizationController.cs` (new)
- ? `Controllers/V2/EventsController.cs` (new)
- ? `Program.cs` (updated)
- ? `API_VERSIONING.md` (documentation)

### EventManagement
- ? `Controllers/EventControllerBase.cs` (new)
- ? `Controllers/OrganizationControllerBase.cs` (new)
- ? `Controllers/V1/EventController.cs` (new)
- ? `Controllers/V1/OrganizationController.cs` (new)
- ? `Program.cs` (updated)
- ? `API_VERSIONING.md` (documentation)

## Build Status
? All projects build successfully  
? No compilation errors  
? Versioning configured correctly  

---

For detailed documentation, see `API_VERSIONING.md`
