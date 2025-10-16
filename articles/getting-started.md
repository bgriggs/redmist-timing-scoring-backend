# Getting Started with RedMist APIs

This guide will help you get started with the RedMist Timing & Scoring APIs.

## Prerequisites

- A RedMist account and organization
- API credentials (client ID and secret)
- Basic understanding of REST APIs and WebSockets

## Step 1: Obtain API Credentials

### For Organization Administrators
1. Log in to the RedMist platform
2. Navigate to your organization settings
3. Go to "Relay Connection" section
4. Note your `client_id` and `client_secret`

## Step 2: Get an Access Token

Use the OAuth2 client credentials flow to obtain a token:

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

## Step 3: Make Your First API Call

### Get Live Events

```bash
curl "https://api.redmist.racing/Events/LoadLiveEvents" \
  -H "Authorization: Bearer YOUR_ACCESS_TOKEN"
```

**Response:**
```json
[
  {
    "eid": 123,
    "on": "Organization Name",
    "en": "Event Name",
    "ed": "2024-03-15",
    "l": true,
    "t": "Track Name"
  }
]
```

### Get Event Details

```bash
curl "https://api.redmist.racing/Events/LoadEvent?eventId=123" \
  -H "Authorization: Bearer YOUR_ACCESS_TOKEN"
```

## Step 4: Connect to Real-Time Updates

### JavaScript/TypeScript

```javascript
import * as signalR from '@microsoft/signalr';

const connection = new signalR.HubConnectionBuilder()
    .withUrl("https://api.redmist.racing/status/event-status", {
        accessTokenFactory: () => accessToken
    })
    .withAutomaticReconnect()
    .build();

// Handle incoming messages
connection.on("ReceiveSessionPatch", (session) => {
    console.log("Session update");
});

connection.on("ReceiveCarPatches", (car) => {
    console.log("Car update");
});

// Connect
await connection.start();

// Subscribe to an event
await connection.invoke("SubscribeToEventV2", 123);
```

### Python

```python
from signalrcore.hub_connection_builder import HubConnectionBuilder
import requests

# Get token
token_response = requests.post(
    "https://auth.redmist.racing/realms/redmist/protocol/openid-connect/token",
    data={
        "grant_type": "client_credentials",
        "client_id": "YOUR_CLIENT_ID",
        "client_secret": "YOUR_SECRET"
    }
)
token = token_response.json()["access_token"]

# Connect to hub
hub = HubConnectionBuilder()\
    .with_url(
        "https://api.redmist.racing/status/event-status",
        options={
            "access_token_factory": lambda: token
        }
    )\
    .with_automatic_reconnect()\
    .build()

# Handle messages
def on_message(message):
    print(f"Status update: {message}")

hub.on("ReceiveSessionPatch", on_session_message)
hub.on("ReceiveCarPatches", on_car_message)

# Start connection
hub.start()

# Subscribe to event
hub.send("SubscribeToEventV2", [123])
```

## Step 5: Explore the API

### Interactive Documentation

Visit the Swagger UI for interactive API exploration:
- **Status API**: https://api.redmist.racing/status/swagger
- **Event Management**: https://api.redmist.racing/event-management/swagger

### Key Endpoints

**Status API:**
- `GET /Events/LoadLiveEvents` - List all live events
- `GET /Events/LoadEvent?eventId={id}` - Get event details
- `GET /Events/LoadSessions?eventId={id}` - Get sessions
- `GET /Events/LoadCarLaps?eventId={id}&sessionId={sid}&carNumber={num}` - Get lap data

**SignalR Hub:**
- `SubscribeToEventV2(eventId)` - Subscribe to receive event updates
- `SubscribeToControlLogs(eventId)` - Get control log updates
- `SubscribeToInCarDriverEvent(eventId, car)` - In-car driver mode

## Next Steps

- [Authentication Guide](authentication.md) - Deep dive into authentication
- [REST API Guide](rest-api-guide.md) - Complete REST API documentation
- [SignalR Hubs](signalr-hubs.md) - Real-time communication details
- [Code Examples](code-examples.md) - More code samples
- [Data Models](data-models.md) - Understanding the data structures

## Common Issues

### Token Expired
Tokens expire after 5 minutes. Implement token refresh logic:

```javascript
async function getToken() {
    const response = await fetch(tokenUrl, {
        method: 'POST',
        body: new URLSearchParams({
            grant_type: 'client_credentials',
            client_id: clientId,
            client_secret: clientSecret
        })
    });
    const data = await response.json();
    return data.access_token;
}

// Refresh token every 4 minutes
setInterval(async () => {
    accessToken = await getToken();
}, 4 * 60 * 1000);
```

### CORS Issues
If developing locally, ensure your origin is allowed or use a proxy.

### Rate Limiting
Implement exponential backoff for retry logic.

## Support

- [GitHub Issues](https://github.com/bgriggs/redmist-timing-scoring-backend/issues)
- [API Reference](../api/index.md)
- [Documentation Home](../index.md)
