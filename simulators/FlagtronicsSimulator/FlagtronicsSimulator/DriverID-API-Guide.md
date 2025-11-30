<!-- markdownlint-disable MD060 -->

# DriverID API Integration Guide

## Overview

The DriverID API provides real-time access to driver identification events from
Flagtronics Track Director. When a driver's RFID tag or BLE device is scanned
by FT200 hardware, the event is immediately available through this API.

## Quick Start

### Access URLs

The API is available via three access methods:

| Access Type | URL                                              | Use Case           |
|-------------|--------------------------------------------------|--------------------|
| Local       | `http://localhost:52733/api/driverid/{apiKey}`   | Same machine       |
| LAN         | `http://{ip}:52733/api/driverid/{apiKey}`        | Same network       |

The API key and LAN IP are shown in Track Director's app log at startup:

```text
DriverID API started on port 52733
Access URLs:
  Local:    http://localhost:52733/api/driverid/{apiKey}
  LAN:      http://192.168.1.100:52733/api/driverid/{apiKey}
Active API key: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
```

### Test Connection

```bash
# Local
curl http://localhost:52733/api/driverid/{apiKey}/health

# LAN (replace IP with actual)
curl http://192.168.1.100:52733/api/driverid/{apiKey}/health

```

Response:

```json
{
  "status": "healthy",
  "timestamp": "2025-11-28T06:40:32Z",
  "bufferedEvents": 5
}
```

## API Endpoints

### Get Latest Event

Returns the most recent driver identification event.

```text
GET /api/driverid/{apiKey}
```

**Response:**

```json
{
  "eventId": 42,
  "timestamp": "2025-11-28T06:40:32Z",
  "driverId": 70012345,
  "driverName": "John Smith",
  "carNumber": "42",
  "ft200DeviceId": 1001,
  "rfid": "E2003412AB01",
  "bleMac": "AA:BB:CC:DD:EE:FF",
  "deviceLookupFound": true
}
```

Returns empty response (204 No Content) if no events exist.

### Get Event History

Returns recent events, newest first.

```text
GET /api/driverid/{apiKey}/history?count=10
```

**Parameters:**

| Name  | Type | Default | Description            |
|-------|------|---------|------------------------|
| count | int  | 10      | Number of events (max 100) |

**Response:**

```json
{
  "events": [
    {
      "eventId": 42,
      "timestamp": "2025-11-28T06:40:32Z",
      "driverId": 70012345,
      "driverName": "John Smith",
      "carNumber": "42",
      "ft200DeviceId": 1001,
      "rfid": "E2003412AB01",
      "bleMac": "AA:BB:CC:DD:EE:FF",
      "deviceLookupFound": true
    }
  ],
  "count": 1
}
```

### Long Polling (Real-Time)

Waits for new events. The request blocks until new events arrive or timeout.

```text
GET /api/driverid/{apiKey}/poll?lastEventId=42&timeout=30
```

**Parameters:**

| Name        | Type | Default | Description                    |
|-------------|------|---------|--------------------------------|
| lastEventId | long | 0       | Return events after this ID    |
| timeout     | int  | 30      | Timeout in seconds (max 30)    |

**Response:**

```json
{
  "events": [...],
  "count": 2,
  "lastEventId": 44
}
```

Returns empty array if timeout expires with no new events.

### Health Check

```text
GET /api/driverid/{apiKey}/health
```

**Response:**

```json
{
  "status": "healthy",
  "timestamp": "2025-11-28T06:40:32Z",
  "bufferedEvents": 5
}
```

### Lookup Driver by ID

```text
GET /api/driverid/{apiKey}/lookup/{driverId}
```

**Response:**

```json
{
  "driverId": 70012345,
  "rfidTag": "E2003412AB01",
  "bleMacAddress": "AA:BB:CC:DD:EE:FF",
  "notes": "Team Alpha driver"
}
```

### Get All Mappings

```text
GET /api/driverid/{apiKey}/mappings
```

**Response:**

```json
{
  "count": 2,
  "mappings": [
    {
      "driverId": 70012345,
      "rfidTag": "E2003412AB01",
      "bleMacAddress": "AA:BB:CC:DD:EE:FF"
    }
  ]
}
```

