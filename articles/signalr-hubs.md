# SignalR Real-Time Communication

RedMist uses SignalR for real-time, bidirectional communication between servers and clients.

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

#### SubscribeToEvent (V1)
Subscribe to real-time updates for a specific event.

```javascript
await connection.invoke("SubscribeToEvent", eventId);
```

**Features:**
- Full status updates every ~5 seconds
- Incremental updates as they occur
- Gzip compression for full updates

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
await connection.invoke("UnsubscribeFromEvent", eventId);
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

#### SubscribeToInCarDriverEvent (V1)
Subscribe to in-car driver display data.

```javascript
await connection.invoke("SubscribeToInCarDriverEvent", eventId, carNumber);
```

#### SubscribeToInCarDriverEventV2 (V2)
Enhanced in-car data with better update frequency.

```javascript
await connection.invoke("SubscribeToInCarDriverEventV2", eventId, carNumber);
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

### Message Compression

Full status updates are gzip-compressed and base64-encoded for efficiency.

**Decompression Examples:**

**JavaScript:**
```javascript
import pako from 'pako';

function decompressMessage(message) {
    if (!message.startsWith('H4sI')) {
        return JSON.parse(message);
    }
    
    const compressed = atob(message);
    const decompressed = pako.inflate(compressed, { to: 'string' });
    return JSON.parse(decompressed);
}
```

**Python:**
```python
import gzip
import base64
import json

def decompress_message(message):
    if not message.startswith('H4sI'):
        return json.loads(message)
    
    compressed = base64.b64decode(message)
    decompressed = gzip.decompress(compressed)
    return json.loads(decompressed)
```

**C#:**
```csharp
using System.IO.Compression;
using System.Text.Json;

public static T DecompressMessage<T>(string message)
{
    if (!message.StartsWith("H4sI"))
    {
        return JsonSerializer.Deserialize<T>(message);
    }
    
    var compressed = Convert.FromBase64String(message);
    using var inputStream = new MemoryStream(compressed);
    using var gzipStream = new GZipStream(inputStream, CompressionMode.Decompress);
    using var outputStream = new MemoryStream();
    gzipStream.CopyTo(outputStream);
    
    var decompressed = Encoding.UTF8.GetString(outputStream.ToArray());
    return JsonSerializer.Deserialize<T>(decompressed);
}
```

## Message Types

### Full Status Update (V1)
```json
{
  "e": 123,
  "n": "Event Name",
  "es": {
    "st": "Green",
    "rt": "12:34:56",
    "togo": 5
  },
  "ee": [...],
  "cps": [
    {
      "n": "42",
      "ovp": 3,
      "clp": 1,
      "bt": "1:23.456",
      "lt": "1:23.789",
      "og": "+1.234",
      "cg": "+0.456"
    }
  ],
  "fd": [...]
}
```

### Incremental Update
```json
{
  "e": 123,
  "t": "patch",
  "patches": [
    {
      "op": "replace",
      "path": "/cps/0/lt",
      "value": "1:23.500"
    }
  ]
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
}

// Usage
const dashboard = new RedMistDashboard(123);
dashboard.connect();
```

## Best Practices

### 1. Always Handle Reconnection
```javascript
connection.onreconnected(async () => {
    // Re-subscribe to all events
    for (const eventId of subscribedEvents) {
        await connection.invoke("SubscribeToEvent", eventId);
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
    await connection.invoke("UnsubscribeFromEvent", eventId);
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
