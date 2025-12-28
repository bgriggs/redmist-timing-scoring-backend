# SignalR Real-Time Communication

RedMist uses SignalR for real-time, bidirectional communication between servers and clients.

SignalR is an open-source library that simplifies adding real-time web functionality to applications. It enables server-side code to push content to connected clients instantly as events happen, rather than having clients poll the server for updates.

### Key Benefits

**Real-Time Updates**
- Instant data delivery with sub-second latency
- Live timing data, positions, and lap times update in real-time
- No polling overhead or delays

**Automatic Transport Selection**
- WebSockets (preferred for best performance)
- Server-Sent Events (SSE)
- Long Polling (fallback for older browsers)
- Automatically negotiates the best available transport

**Built-In Reconnection**
- Automatic reconnection with exponential backoff
- Seamless recovery from network interruptions
- State preservation across reconnections

**Scalability**
- Redis backplane for multi-server deployments
- Horizontal scaling support
- Connection state management

**Cross-Platform Support**
- JavaScript/TypeScript clients
- .NET clients (C#, F#)
- Python clients
- Java clients
- Native mobile apps (iOS, Android)

**Bi-Directional Communication**
- Server-to-client push notifications
- Client-to-server method invocation
- Strongly-typed hubs for type safety

## Hub Overview

### StatusHub
**URL:** `wss://api.redmist.racing/status/event-status`  
**Authentication:** Required (Bearer token)

The StatusHub provides real-time event updates, timing data, and race information.

## Connection Setup

### JavaScript/TypeScript

```javascript
import * as signalR from '@microsoft/signalr';

// Create connection
const connection = new signalR.HubConnectionBuilder()
    .withUrl("https://api.redmist.racing/status/event-status", {
        accessTokenFactory: () => getAccessToken() // Your token function
    })
    .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: retryContext => {
            // Exponential backoff: 0, 2, 10, 30 seconds, then 30 seconds
            if (retryContext.previousRetryCount === 0) return 0;
            if (retryContext.previousRetryCount === 1) return 2000;
            if (retryContext.previousRetryCount === 2) return 10000;
            return 30000;
        }
    })
    .configureLogging(signalR.LogLevel.Information)
    .build();

// Handle reconnection
connection.onreconnecting(error => {
    console.log('Connection lost. Reconnecting...', error);
});

connection.onreconnected(connectionId => {
    console.log('Reconnected with connection ID:', connectionId);
    // Re-subscribe to events
    resubscribe();
});

connection.onclose(error => {
    console.log('Connection closed.', error);
});

// Start connection
try {
    await connection.start();
    console.log('Connected to StatusHub');
} catch (err) {
    console.error('Connection failed:', err);
}
```

### Python

```python
from signalrcore.hub_connection_builder import HubConnectionBuilder
import logging

# Enable logging
logging.basicConfig(level=logging.INFO)

# Create connection
hub = HubConnectionBuilder()\
    .with_url(
        "https://api.redmist.racing/status/event-status",
        options={
            "access_token_factory": lambda: get_access_token(),
            "headers": {
                "User-Agent": "RedMist-Python-Client/1.0"
            }
        }
    )\
    .configure_logging(logging.INFO)\
    .with_automatic_reconnect({
        "type": "interval",
        "keep_alive_interval": 10,
        "intervals": [0, 2, 10, 30]
    })\
    .build()

# Start connection
hub.start()
```

### C#

```csharp
using Microsoft.AspNetCore.SignalR.Client;

var connection = new HubConnectionBuilder()
    .WithUrl("https://api.redmist.racing/status/event-status", options =>
    {
        options.AccessTokenProvider = async () => await GetAccessTokenAsync();
    })
    .WithAutomaticReconnect(new[] {
        TimeSpan.Zero,
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    })
    .Build();

connection.Reconnecting += error =>
{
    Console.WriteLine($"Connection lost. Reconnecting... {error}");
    return Task.CompletedTask;
};

connection.Reconnected += connectionId =>
{
    Console.WriteLine($"Reconnected: {connectionId}");
    return ResubscribeAsync();
};

await connection.StartAsync();
```

## Hub Methods

### Event Subscriptions

#### SubscribeToEventV2 (V2) 
Enhanced subscription with improved data structures.

```javascript
await connection.invoke("SubscribeToEventV2", eventId);
```

**Features:**
- Optimized data format
- Better compression
- Improved update frequency

#### UnsubscribeFromEvent / UnsubscribeFromEventV2
Stop receiving updates for an event.

```javascript
await connection.invoke("UnsubscribeFromEventV2", eventId);
```

### Control Log Subscriptions

#### SubscribeToControlLogs
Receive control log updates for an event.

```javascript
await connection.invoke("SubscribeToControlLogs", eventId);
```

#### SubscribeToCarControlLogs
Receive control log updates for a specific car.

```javascript
await connection.invoke("SubscribeToCarControlLogs", eventId, carNumber);
```

**Use Case:** Drivers/teams who only want their car's penalties.

```javascript
// Example: Subscribe to car #42's control logs
await connection.invoke("SubscribeToCarControlLogs", 123, "42");
```

### In-Car Driver Mode

#### SubscribeToInCarDriverEvent
Subscribe to in-car driver display data.

```javascript
await connection.invoke("SubscribeToInCarDriverEvent", eventId, carNumber);
```

**Data Included:**
- Current position
- Gap to car ahead
- Gap to car behind
- Best lap comparison
- Current lap time
- Flag status

## Receiving Messages

### ReceiveMessage Event

All updates are sent via the `ReceiveMessage` event.

```javascript
connection.on("ReceiveMessage", (message) => {
    // Check if message is gzipped
    if (message.startsWith('H4sI')) {
        // Decompress gzip data
        const decompressed = pako.inflate(
            atob(message), 
            { to: 'string' }
        );
        const status = JSON.parse(decompressed);
        handleStatus(status);
    } else {
        // Parse JSON directly
        const status = JSON.parse(message);
        handleStatus(status);
    }
});

function handleStatus(status) {
    console.log('Event Status:', status);
    
    // Update UI with car positions
    updateCarPositions(status.cps);
    
    // Update flags
    updateFlags(status.fd);
    
    // Update event info
    updateEventInfo(status.es);
}
```

## Complete Example

### Real-Time Dashboard

```javascript
import * as signalR from '@microsoft/signalr';
import pako from 'pako';

class RedMistDashboard {
    constructor(eventId) {
        this.eventId = eventId;
        this.connection = null;
        this.currentStatus = null;
    }

    async connect() {
        this.connection = new signalR.HubConnectionBuilder()
            .withUrl("https://api.redmist.racing/status/event-status", {
                accessTokenFactory: () => this.getToken()
            })
            .withAutomaticReconnect()
            .build();

        // Handle messages
        this.connection.on("ReceiveMessage", (message) => {
            const status = this.decompressMessage(message);
            this.updateStatus(status);
        });

        // Handle reconnection
        this.connection.onreconnected(async () => {
            await this.subscribe();
        });

        // Start connection
        await this.connection.start();
        await this.subscribe();
    }

    async subscribe() {
        await this.connection.invoke("SubscribeToEventV2", this.eventId);
        console.log(`Subscribed to event ${this.eventId}`);
    }

    async unsubscribe() {
        await this.connection.invoke("UnsubscribeFromEventV2", this.eventId);
        console.log(`Unsubscribed from event ${this.eventId}`);
    }

    decompressMessage(message) {
        if (message.startsWith('H4sI')) {
            const compressed = atob(message);
            const decompressed = pako.inflate(compressed, { to: 'string' });
            return JSON.parse(decompressed);
        }
        return JSON.parse(message);
    }

    updateStatus(status) {
        if (status.t === 'patch') {
            // Apply JSON patch
            this.applyPatches(status.patches);
        } else {
            // Full update
            this.currentStatus = status;
        }

        this.render();
    }

    applyPatches(patches) {
        // Apply JSON patches to current status
        patches.forEach(patch => {
            const path = patch.path.split('/').slice(1);
            let obj = this.currentStatus;
            
            for (let i = 0; i < path.length - 1; i++) {
                obj = obj[path[i]];
            }
            
            if (patch.op === 'replace') {
                obj[path[path.length - 1]] = patch.value;
            }
        });
    }

    render() {
        // Update UI with current status
        document.getElementById('event-name').textContent = 
            this.currentStatus.n;
        
        // Update car positions
        const tbody = document.getElementById('positions-tbody');
        tbody.innerHTML = '';
        
        this.currentStatus.cps?.forEach(car => {
            const row = tbody.insertRow();
            row.innerHTML = `
                <td>${car.ovp}</td>
                <td>${car.n}</td>
                <td>${car.bt}</td>
                <td>${car.og}</td>
            `;
        });
    }

    async getToken() {
        // Your token retrieval logic
        return localStorage.getItem('access_token');
    }

    async disconnect() {
        await this.unsubscribe();
        await this.connection.stop();
    }
}

// Usage
const dashboard = new RedMistDashboard(123);
dashboard.connect();

// Clean up on page unload
window.addEventListener('beforeunload', async () => {
    await dashboard.disconnect();
});
```

## Best Practices

### 1. Always Handle Reconnection
```javascript
connection.onreconnected(async () => {
    // Re-subscribe to all events using V2
    for (const eventId of subscribedEvents) {
        await connection.invoke("SubscribeToEventV2", eventId);
    }
});
```

### 2. Implement Exponential Backoff
```javascript
.withAutomaticReconnect({
    nextRetryDelayInMilliseconds: retryContext => {
        return Math.min(
            1000 * Math.pow(2, retryContext.previousRetryCount),
            30000
        );
    }
})
```

### 3. Handle Token Refresh
```javascript
let tokenExpiry = Date.now() + 300000; // 5 minutes

connection.onclose(async () => {
    if (Date.now() > tokenExpiry) {
        // Token expired, get new one
        await refreshToken();
    }
    await connection.start();
});
```

### 4. Unsubscribe When Done
```javascript
window.addEventListener('beforeunload', async () => {
    await connection.invoke("UnsubscribeFromEventV2", eventId);
    await connection.stop();
});
```

### 5. Handle Errors Gracefully
```javascript
connection.on("ReceiveMessage", (message) => {
    try {
        const status = decompressMessage(message);
        updateUI(status);
    } catch (error) {
        console.error('Failed to process message:', error);
        // Don't crash, log and continue
    }
});
```

## Troubleshooting

### Connection Fails
- Check token is valid and not expired
- Verify URL is correct
- Ensure HTTPS is used
- Check CORS settings

### No Messages Received
- Verify subscription was successful
- Check token has required permissions
- Ensure event is live
- Check browser console for errors

### High Latency
- Check network connection
- Verify server load
- Consider using V2 endpoints
- Check compression is working

## Related Documentation

- [Getting Started](getting-started.md)
- [Authentication](authentication.md)
- [Data Models](data-models.md)
- [Code Examples](code-examples.md)
