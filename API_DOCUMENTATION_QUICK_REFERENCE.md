# RedMist API Documentation - Quick Access Guide

## ?? Live Swagger Documentation

Access interactive API documentation for each service:

### Status API (Public Endpoints)
**URL:** `https://localhost:5001/swagger` (or your configured port)

**Available Endpoints:**
- GET `/Events/LoadLiveEvents` - Get all live events (public, no auth)
- GET `/Events/LoadEvent` - Get specific event details
- GET `/Events/LoadSessions` - Get sessions for an event
- GET `/Events/LoadCarLaps` - Get lap data for a car
- GET `/Events/LoadCompetitorMetadata` - Get driver/car metadata
- GET `/Events/LoadControlLog` - Get control log entries
- GET `/Events/LoadFlags` - Get flag history
- GET `/Organization/GetOrganizationIcon` - Get org logo (public, no auth)

**Versions:** V1 and V2 available

### Event Management API (Protected)
**URL:** `https://localhost:5002/swagger` (or your configured port)

**Available Endpoints:**
- GET `/Event/LoadEventSummaries` - List all events for your org
- GET `/Event/LoadEvent` - Get specific event
- POST `/Event/SaveNewEvent` - Create new event
- POST `/Event/UpdateEvent` - Update event configuration
- PUT `/Event/UpdateEventStatusActive` - Set active event
- PUT `/Event/DeleteEvent` - Soft delete event
- GET `/Organization/LoadOrganization` - Get your org details
- POST `/Organization/UpdateOrganization` - Update org settings

**Authentication Required:** Bearer token (your organization)

### User Management API (Protected)
**URL:** `https://localhost:5003/swagger` (or your configured port)

**Available Endpoints:**
- GET `/Organization/LoadUserOrganization` - Get your organization
- GET `/Organization/LoadUserOrganizationRoles` - Get your roles
- POST `/Organization/SaveNewOrganization` - Create organization
- POST `/Organization/UpdateOrganization` - Update org details
- GET `/Organization/LoadRelayConnection` - Get relay credentials

**Authentication Required:** Bearer token (user identity)

### Timing & Scoring Service (Internal)
**URL:** `https://localhost:5004/swagger` (or your configured port)

**Available Endpoints:**
- GET `/Status/GetStatus` - Get current session state (MessagePack)

**Usage:** Internal service-to-service communication

## ?? SignalR Hub Documentation

### StatusHub - Real-time Event Updates
**Hub URL:** `wss://api.redmist.racing/status/event-status`

**Connection Example (JavaScript):**
```javascript
import * as signalR from '@microsoft/signalr';

const connection = new signalR.HubConnectionBuilder()
    .withUrl("https://api.redmist.racing/status/event-status", {
        accessTokenFactory: () => YOUR_TOKEN
    })
    .withAutomaticReconnect()
    .build();

await connection.start();
```

**Available Methods:**

#### Event Subscriptions (V1)
- `SubscribeToEvent(eventId)` - Subscribe to event updates
- `UnsubscribeFromEvent(eventId)` - Unsubscribe from event

#### Event Subscriptions (V2)
- `SubscribeToEventV2(eventId)` - Enhanced event updates
- `UnsubscribeFromEventV2(eventId)` - Unsubscribe V2

#### Control Logs
- `SubscribeToControlLogs(eventId)` - Get all control log updates
- `SubscribeToCarControlLogs(eventId, carNum)` - Car-specific logs
- `UnsubscribeFromControlLogs(eventId)`
- `UnsubscribeFromCarControlLogs(eventId, carNum)`

#### In-Car Driver Mode (V1)
- `SubscribeToInCarDriverEvent(eventId, car)` - In-car data feed
- `UnsubscribeFromInCarDriverEvent(eventId, car)`

#### In-Car Driver Mode (V2)
- `SubscribeToInCarDriverEventV2(eventId, car)` - Enhanced in-car feed
- `UnsubscribeFromInCarDriverEventV2(eventId, car)`

**Server-to-Client Events:**
- `ReceiveMessage(status)` - Receives JSON status updates (gzipped for full updates)

## ?? Authentication

### Getting a Token

**Endpoint:** `https://auth.redmist.racing/realms/redmist/protocol/openid-connect/token`

**Request (Client Credentials):**
```bash
curl -X POST "https://auth.redmist.racing/realms/redmist/protocol/openid-connect/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials" \
  -d "client_id=YOUR_CLIENT_ID" \
  -d "client_secret=YOUR_SECRET"
```

**Response:**
```json
{
  "access_token": "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9...",
  "expires_in": 300,
  "token_type": "Bearer"
}
```

### Using the Token

**HTTP Headers:**
```
Authorization: Bearer YOUR_ACCESS_TOKEN
```

**SignalR:**
```javascript
.withUrl(url, {
    accessTokenFactory: () => YOUR_ACCESS_TOKEN
})
```

## ?? Data Models

### Key Response Models

**EventListSummary** - Event list item
```json
{
  "eid": 123,
  "on": "Organization Name",
  "en": "Event Name",
  "ed": "2024-03-15",
  "l": true,
  "t": "Track Name"
}
```

**Payload** - Event status (V1)
```json
{
  "e": 123,
  "n": "Event Name",
  "es": { /* EventStatus */ },
  "ee": [ /* EventEntry[] */ ],
  "cps": [ /* CarPosition[] */ ],
  "fd": [ /* FlagDuration[] */ ]
}
```

**SessionState** - Event status (V2)
- Enhanced structure with better performance
- Use V2 endpoints for new integrations

**CarPosition** - Car timing data
```json
{
  "n": "42",
  "bt": "1:23.456",
  "ovp": 3,
  "clp": 1,
  "og": "+1.234",
  "cg": "+0.456"
}
```

## ?? API Versioning

### V1 APIs (Legacy)
- Route: `/Events/[action]` or `/v1/Events/[action]`
- Returns: `Payload` format
- Status: Supported for backward compatibility

### V2 APIs (Recommended)
- Route: `/v2/Events/[action]`
- Returns: `SessionState` format
- Status: Current version, recommended for new development

## ??? Development Tips

### Testing with Swagger UI
1. Navigate to the Swagger URL for your service
2. Click "Authorize" button
3. Enter: `Bearer YOUR_TOKEN`
4. Click "Authorize" then "Close"
5. All endpoints are now authenticated
6. Use "Try it out" to test endpoints

### IntelliSense in Visual Studio
- Hover over any API method to see full documentation
- Parameter descriptions appear as you type
- Return types are fully documented
- Examples are included in remarks

### Error Responses
All APIs return standard HTTP status codes:
- `200` - Success
- `400` - Bad Request
- `401` - Unauthorized
- `404` - Not Found
- `500` - Internal Server Error

## ?? Client Examples

### JavaScript/TypeScript
See full examples in Swagger documentation for each endpoint

### Python
```python
import requests

headers = {"Authorization": f"Bearer {token}"}
response = requests.get(
    "https://api.redmist.racing/Events/LoadLiveEvents",
    headers=headers
)
events = response.json()
```

### C#
```csharp
using var client = new HttpClient();
client.DefaultRequestHeaders.Authorization = 
    new AuthenticationHeaderValue("Bearer", token);

var response = await client.GetAsync(
    "https://api.redmist.racing/Events/LoadLiveEvents");
var events = await response.Content.ReadFromJsonAsync<List<EventListSummary>>();
```

## ?? Additional Resources

- **GitHub Repository:** https://github.com/bgriggs/redmist-timing-scoring-backend
- **README:** See main README.md for architecture overview
- **API Versioning Guide:** See API_VERSIONING.md for detailed versioning info
- **Setup Guide:** See DOCUMENTATION_SETUP_SUMMARY.md for setup details

## ?? Support

For API issues or questions:
1. Check Swagger documentation for endpoint details
2. Review XML documentation in your IDE
3. See GitHub repository issues
4. Refer to code examples in documentation
