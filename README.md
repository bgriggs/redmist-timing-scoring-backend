# Red Mist Backend Services

[![Build](https://github.com/bgriggs/redmist-timing-scoring-backend/actions/workflows/build.yml/badge.svg)](https://github.com/bgriggs/redmist-timing-scoring-backend/actions/workflows/build.yml)
[![.NET](https://img.shields.io/badge/.NET-9.0-purple)](https://dotnet.microsoft.com/download)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Red Mist provides race timing and scoring services for motorsport events. The backend services are designed to handle real-time data processing, event management, and user authentication.
These service make up the backend of the Red Mist system. They are responsible for managing the timing and scoring of events, as well as providing APIs for user management and event orchestration.

## Core Services

### `RedMist.TimingAndScoringService`
- Manages real-time timing and scoring logic.
- Processes rmonitor, transponder, and control log data.
- Handles session timing and race state.
- Sends data updates to subscribers via WebSocket.
- There is an instance created for each event.

### `RedMist.EventManagement`
- Controls event metadata and configurations.
- Manages event setup, registration, and scheduling.

### `RedMist.EventOrchestration`
- Coordinates the orchestration and lifecycle of events.
- Creates jobs for event processor, logging and optional control log processor services.
- Handles the removal of jobs when an event is stopped, i.e. when relay disconnects exceeds reconnection timeout.

### `RedMist.ControlLogs`
- Library for processing control logs.

### `RedMist.ControlLogProcessor`
- Requests control logs for an event.
- There is an instance created for each event that has a control log configured.

### `RedMist.EventLogger`
- Provides logging services for an event.
- There is an instance created for each event.

### `RedMist.RelayApi`
- API that receives data from Relay instances that run at the track.

### `RedMist.StatusApi`
- Provides status to live user interface connections and external services.

### `RedMist.UserManagement`
- Handles user profile data.
- Manages access controls and user roles.

### `RedMist.Database`
- Central database access layer.
- Manages migrations, persistence, and schema.

---

## Testing and Shared Libraries

### `RedMist.TimingAndScoringService.Tests`
- Unit and integration tests for timing/scoring logic.

### `RedMist.Backend.Shared`
- Shared utilities, constants, and data models.
- Used across multiple services for consistency.

### `unit-test-helpers`
- Helpers and mocks for writing unit tests.
- Reduces boilerplate across test projects.

## Infrastructure & Deployment

### `redmist-deploy`
- Deployment scripts and configuration for Kubernetes environment.

### `StackRedis.L1`
- Used for unit testing.

---

# Requesting a Token Using Client Credentials
This guide outlines how to obtain an access token using the **Client Credentials Grant** — ideal for API (machine-to-machine) authentication.

## Prerequisites

- A client is registered with the following:
`client_id` and `client_secret`

## Goal
Obtain an **access token** to use in API calls from backend systems without user interaction.

## Token Endpoint Format
https://auth.redmist.racing/realms/redmist/protocol/openid-connect/token

### Method: 
`POST`

### Headers: 
Content-Type: application/x-www-form-urlencoded

### Body Parameters:

| Key            | Value                    |
|----------------|--------------------------|
| `grant_type`   | `client_credentials`     |
| `client_id`    | Your client ID           |
| `client_secret`| Your client secret       |


## Example (curl)
```bash
curl -X POST "https://auth.redmist.racing/realms/redmist/protocol/openid-connect/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials" \
  -d "client_id=my-client" \
  -d "client_secret=secret"
```
## Example Response
```json
{
  "access_token": "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9...",
  "expires_in": 300,
  "token_type": "Bearer",
  "scope": "profile email"
}

```

## Python Example
```python
import requests

data = {
    "grant_type": "client_credentials",
    "client_id": "my-client",
    "client_secret": "secret"
}

response = requests.post(
    "https://auth.redmist.racing/realms/redmist/protocol/openid-connect/token",
    data=data
)

token = response.json()["access_token"]
```

## JavaScript (Node.js) Example
```javascript
const fetch = require("node-fetch");

const params = new URLSearchParams();
params.append("grant_type", "client_credentials");
params.append("client_id", "my-client");
params.append("client_secret", "secret");

const res = await fetch("https://auth.redmist.racing/realms/redmist/protocol/openid-connect/token", {
  method: "POST",
  headers: { "Content-Type": "application/x-www-form-urlencoded" },
  body: params
});

const json = await res.json();
const token = json.access_token;
```

---

# API Endpoints
This section provides an overview of the API endpoints available in the Red Mist backend services. Each endpoint is categorized by its functionality and includes details on the HTTP method, URL, and a brief description.
Base route for all endpoints is `https://api.redmist.racing`

## Events (Status API)
The `EventsController` provides endpoints to retrieve information about events and their associated sessions.

**Base Route:** `/status/events`

### 1. Get All Events

- **Endpoint:** `GET /status/events/LoadEvents`
- **Description:** Retrieves a list of all events.
- **Response:**
  - `200 OK`: Returns the EventListSummary object.
  - `500 Internal Server Error`: If an unexpected error occurs.

### 2. Get Live Events

- **Endpoint:** `GET /status/events/LoadLiveEvents`
- **Description:** Retrieves a list of all live events.
- **Response:**
  - `200 OK`: Returns the EventListSummary object.
  - `500 Internal Server Error`: If an unexpected error occurs.


### 3. Get Live and Recent Events

- **Endpoint:** `GET /status/events/LoadLiveAndRecentEvents`
- **Description:** Retrieves a list of all live and recent (top 100) events.
- **Response:**
  - `200 OK`: Returns the EventListSummary object.
  - `500 Internal Server Error`: If an unexpected error occurs.

### 4. Get an Event

- **Endpoint:** `GET /status/events/LoadEvent?eventId=x`
- **Parameters:**
  - `eventId` (int): The unique identifier of the event.
- **Description:** Loads a specific event. Null if not found
- **Response:**
  - `200 OK`: Returns the Event object.
  - `500 Internal Server Error`: If an unexpected error occurs.

### 5. Get Laps for a Car

- **Endpoint:** `GET /status/events/LoadCarLaps?eventId=x&sessionId=y&carNumber=n`
- **Parameters:**
  - `eventId` (int): The identifier of the event.
  - `sessionId` (int): The identifier of the session.
  - `carNumber` (string): The number of the car.
- **Description:** Loads completed laps for a specific car in a given session.
- **Response:**
  - `200 OK`: Returns the array of CarPosition.
  - `500 Internal Server Error`: If an unexpected error occurs.


### 6. Get Sessions for an Event

- **Endpoint:** `GET /status/events/LoadSessions?eventId=x`
- **Parameters:**
  - `eventId` (int): The identifier of the event.
- **Description:** Loads sessions for a specific event. Null if not found.
- **Response:**
  - `200 OK`: Returns the array of Session objects.
  - `500 Internal Server Error`: If an unexpected error occurs.

### 7. Get Results for a Session

- **Endpoint:** `GET /status/events/LoadSessionResults?eventId=x&sessionId=y`
- **Parameters:**
  - `eventId` (int): The identifier of the event.
  - `sessionId` (int): The identifier of the session.
- **Description:** Loads results for a specific session of an event. Null if not found.
- **Response:**
  - `200 OK`: Returns Payload object.
  - `500 Internal Server Error`: If an unexpected error occurs.

### 8. Get Competitor Metadata

- **Endpoint:** `GET /status/events/LoadCompetitorMetadata?eventId=x&car=n`
- **Parameters:**
  - `eventId` (int): The identifier of the event.
  - `car` (string): The identifier of the car for which metadata is retrieved.
- **Description:** Loads competitor metadata if available from an Orbits timing system for a specific event and car. Null if not found.
- **Response:**
  - `200 OK`: Returns CompetitorMetadata object or null.
  - `500 Internal Server Error`: If an unexpected error occurs.

### 9. Get Control Log

- **Endpoint:** `GET /status/events/LoadControlLog?eventId=x`
- **Parameters:**
  - `eventId` (int): The identifier of the event.
- **Description:** Loads control log for a specific event if available and configured by the organizer.
- **Response:**
  - `200 OK`: Returns array of ControlLogEntry object.
  - `500 Internal Server Error`: If an unexpected error occurs.

### 10. Get Car Control Logs

- **Endpoint:** `GET /status/events/LoadCarControlLogs?eventId=x&car=n`
- **Parameters:**
  - `eventId` (int): The identifier of the event.
  - `car` (string): The identifier of the car for which control logs are retrieved.
- **Description:** Loads control logs for a specific event and car if available and configured by the organizer.
- **Response:**
  - `200 OK`: Returns array of ControlLogEntry object.
  - `500 Internal Server Error`: If an unexpected error occurs.


### 🔐 Authentication

All endpoints require authentication via a valid API token.

- **Header:** `Authorization: Bearer {token}`

### Models
#### EventListSummary

| C# Property Name     | JSON Property Name | Type             | Description                                                        | 
|---------------------|-------------------|------------------|--------------------------------------------------------------------| 
| Id                  | eid               | int              | The unique identifier for the event.                               | 
| OrganizationId      | oid               | int              | The unique identifier for the organization.                        | 
| OrganizationName    | on                | string           | The name of the organizing entity.                                 | 
| EventName           | en                | string           | The name of the event.                                             | 
| EventDate           | ed                | string           | The date of the event (as a string, typically in ISO format).      | 
| IsLive              | l                 | bool             | Indicates if the event is currently live.                          | 
| TrackName           | t                 | string           | The name of the track where the event is held.                     | 
| Schedule            | s                 | EventSchedule?   | The schedule for the event, including session times (nullable).    |

#### Event

| C# Property Name        | JSON Property Name | Type                | Description                                                                                  | 
|------------------------|-------------------|---------------------|----------------------------------------------------------------------------------------------| 
| EventId                | e                 | int                 | The identifier for the event.                                                         | 
| EventName              | n                 | string              | The name of the event.                                                                       | 
| EventDate              | d                 | string              | The date of the event (as a string, typically in ISO format).                                | 
| EventUrl               | u                 | string              | The URL for the event's information or results.                                              | 
| Sessions               | s                 | Session[]           | The array of sessions associated with the event.                                             | 
| OrganizationName       | on                | string              | The name of the organizing entity.                                                           | 
| OrganizationWebsite    | ow                | string?             | The website URL of the organizing entity (nullable).                                         | 
| OrganizationLogo       | l                 | byte[]?             | The logo of the organizing entity as a byte array (nullable).                                | 
| Schedule               | sc                | EventSchedule?      | The schedule for the event, including session times (nullable).                              | 
| TrackName              | t                 | string              | The name of the track where the event is held.                                               | 
| CourseConfiguration    | cc                | string              | The configuration or layout of the track/course.                                             | 
| Distance               | di                | string              | The distance of the event (e.g., total race distance or lap length).                         | 
| Broadcast              | b                 | BroadcasterConfig?  | The broadcast configuration for the event (nullable).                                        | 
| HasControlLog          | hc                | bool                | Indicates if the event has an associated control log.                                        | 
| IsLive                 | il                | bool                | Indicates if the event is currently live.                                                    |


#### Session

| C# Property Name        | JSON Property Name | Type      | Description                                                                 | 
|------------------------|-------------------|-----------|-----------------------------------------------------------------------------| 
| Id                     | sid               | int       | The identifier for the session.                                      | 
| EventId                | eid               | int       | The identifier of the event this session belongs to.                        | 
| Name                   | n                 | string    | The name of the session (e.g., "Qualifying", "Race 1").                     | 
| StartTime              | st                | DateTime  | The scheduled or actual start time of the session.                          | 
| EndTime                | et                | DateTime? | The scheduled or actual end time of the session (nullable).                 | 
| LocalTimeZoneOffset    | tz                | double    | The local time zone offset (in hours) from UTC for the session times.        | 
| LastUpdated            | lu                | DateTime? | The last time the session data was updated (nullable).                      | 
| IsLive                 | il                | bool      | Indicates if the session is currently live.                                 | 
| IsPracticeQualifying   | pq                | bool      | Indicates if the session is a practice or qualifying session.                |

#### EventSchedule

| C# Property Name | JSON Property Name | Type                        | Description                                              | 
|------------------|-------------------|-----------------------------|----------------------------------------------------------| 
| Name             | n                 | string                      | The name or label for the event schedule.                | 
| Entries          | s                 | List<EventScheduleEntry>    | The list of schedule entries for the event.              |

#### EventScheduleEntry

| C# Property Name | JSON Property Name | Type      | Description                                              | 
|------------------|-------------------|-----------|----------------------------------------------------------| 
| DayOfEvent       | d                 | DateTime  | The date of the event day for this schedule entry.       | 
| StartTime        | s                 | DateTime  | The scheduled start time for this entry.                 | 
| EndTime          | e                 | DateTime  | The scheduled end time for this entry.                   | 
| Name             | n                 | string    | The name or description of the schedule entry, e.g. Practice Session 1.           |

#### BroadcasterConfig

| C# Property Name | JSON Property Name | Type   | Description                                   | 
|------------------|-------------------|--------|-----------------------------------------------| 
| CompanyName      | c                 | string | The name of the broadcasting company.         | 
| Url              | u                 | string | The URL for the broadcaster or broadcast feed.|

#### CompetitorMetadata

| C# Property Name   | JSON Property Name | Type      | Description                                                        | 
|--------------------|-------------------|-----------|--------------------------------------------------------------------| 
| EventId            | e                 | int       | The identifier for the event.                               | 
| CarNumber          | n                 | string    | The car number or competitor number.                               | 
| LastUpdated        | lu                | DateTime  | The last time this metadata was updated.                           | 
| Transponder        | t                 | uint      | The primary transponder number assigned to the car.                | 
| Transponder2       | t2                | uint      | The secondary transponder number assigned to the car.              | 
| Class              | cl                | string    | The class or category of the competitor.                           | 
| FirstName          | -                 | string    | The first name of the competitor.                                  | 
| LastName           | -                 | string    | The last name of the competitor.                                   | 
| NationState        | -                 | string    | The nation or state of the competitor.                             | 
| Sponsor            | -                 | string    | The sponsor of the competitor or car.                              | 
| Make               | -                 | string    | The make or manufacturer of the car.                               | 
| Hometown           | -                 | string    | The hometown of the competitor.                                    | 
| Club               | -                 | string    | The club affiliation of the competitor.                            |
| ModelEngine        | -                 | string    | The model and engine details of the car.                           | 
| Tires              | -                 | string    | The tire brand or type used by the car.                            | 
| Email              | -                 | string    | The email address of the competitor.                               |

#### ControlLogEntry

| C# Property Name        | JSON Property Name | Type      | Description                                                        | 
|------------------------|-------------------|-----------|--------------------------------------------------------------------| 
| OrderId                | o                 | int       | The order or sequence number of the log entry.                     | 
| Timestamp              | t                 | DateTime  | The timestamp when the log entry was created.                      | 
| Corner                 | cor               | string    | The name of the corner related to the log entry.     | 
| Car1                   | c1                | string    | The first car involved in the log entry.                           | 
| IsCar1Highlighted      | c1h               | bool      | Indicates if the first car should be highlighted.                  | 
| Car2                   | c2                | string    | The second car involved in the log entry.                          | 
| IsCar2Highlighted      | c2h               | bool      | Indicates if the second car should be highlighted.                 | 
| Note                   | n                 | string    | Additional notes or comments for the log entry.                    | 
| Status                 | s                 | string    | The status or outcome associated with the log entry.               | 
| PenalityAction         | a                 | string    | The penalty action taken, if any.                                  | 
| OtherNotes             | on                | string    | Any other notes or remarks related to the log entry.               |


### Error Handling

- `400 Bad Request`: The request was invalid or cannot be served.
- `401 Unauthorized`: Authentication failed or user does not have permissions.
- `404 Not Found`: The requested resource could not be found.
- `500 Internal Server Error`: An unexpected error occurred on the server.

## Organization (Status API)
The `OrganizationController` provides endpoints to retrieve information about organizations, i.e. the groups running the events.

**Base Route:** `/status/organization`

### 1. Get Organization Icon

- **Endpoint:** `GET /status/organization/GetOrganizationIcon?organizationId=x`
- **Description:** Retrieves the icon for a specific organization by its ID.
- **Response:**
  - `200 OK`: Returns the organization icon image.
  - `404 Not Found`: If the organization does not exist or has no icon.


## Status Endpoint (Status API)
The `StatusHub` is a SignalR hub that facilitates real-time communication between the server and connected clients. It enables clients to receive live updates about system status, session changes, and other pertinent events.

**Hub Route:** `/status/event-status`

### JavaScript client connection example
https://learn.microsoft.com/en-us/aspnet/core/signalr/javascript-client?view=aspnetcore-9.0&tabs=visual-studio-code

https://learn.microsoft.com/en-us/aspnet/core/signalr/authn-and-authz?view=aspnetcore-9.0

### Example Connection (JavaScript)
``` javascript
const connection = new signalR.HubConnectionBuilder()
     .withUrl("https://api.redmist.racing/status/event-status", { accessTokenFactory: () => this.loginToken })
    .withAutomaticReconnect()
    .build();

connection.start()
    .then(function() {
        console.log("Connected to StatusHub");
    })
    .catch(function(err) {
        return console.error(err.toString());
    });
```

### Example Connection (Python)
```bash
pip install signalr-client-aio aiohttp
```
```python
import asyncio
from signalr_aio import Connection
import aiohttp

async def main():
    url = "https://api.redmist.racing/status/event-status"
    
    # Your Bearer token
    token = "YOUR_BEARER_TOKEN_HERE"
    
    # Setup HTTP session with the Authorization header
    headers = {
        "Authorization": f"Bearer {token}"
    }
    
    session = aiohttp.ClientSession(headers=headers)
    
    # Create connection with custom session (so headers are sent)
    connection = Connection(url, session)
    
    # Get the hub proxy
    hub = connection.register_hub('event-status')
    
    # Define a handler for a server-invoked event called 'ReceiveMessage'
    def on_receive_message(message):
        print(f"Received message from server: {message}")
    
    hub.client.on('ReceiveMessage', on_receive_message)
    
    # Start connection
    await connection.start()
    print("Connection started")
    
    # Call a server method with some argument
    await hub.server.invoke('SubscribeToEvent', 123)
    
    # Keep the connection alive for 30 seconds (or your use case)
    await asyncio.sleep(30)
    
    # Stop connection and close session
    await connection.close()
    await session.close()

if __name__ == "__main__":
    asyncio.run(main())
```

## Server-to-Client Methods
The server can invoke the following methods on connected clients:

### 1. `ReceiveMessage`

- **Description:** Provides a message to the client with each change in a car's status. About every 5 seconds a full update is provided.
- **Parameters:**
  - `status` (string): The current status payload Json. The full payload is compressed with gzip. Incremental updates are not.
  
## Payload Structure
The Payload class represents the main data structure for transmitting event, entry, car, and flag status information in the RedMist timing system.

| C# Property Name      | JSON Property Name | Type | Description                                                                 | 
|---------------------- |--------------------|------|-----------------------------------------------------------------------------|
| EventId               | e                  | int  | The identifier for the event.                                        | 
| EventName             | n                  | string | The name of the event.                                                      | 
| EventStatus           | es                 | EventStatus | The current status of the event (flag, laps to go, etc.).                   | 
| EventEntries          | ee                 | array EventEntry | The list of all event entries (competitors/cars). This is only populated on a full update.  |  
| EventEntryUpdates     | eeu                | array EventEntry | The list of updated event entries since the last payload. This is only populated on an incremental update. | 
| CarPositions          | cps                | array CarPosition | The list of all car positions for the event/session. This is only populated on a full update.  | 
| CarPositionUpdates    | cpu                | array CarPosition | The list of updated car positions since the last payload. This is only populated on an incremental update. | 
| FlagDurations         | fd                 | array FlagDuration | The list of flag durations (start/end times for each flag state).           | 
| IsReset               | r                  | boolean | Indicates if this payload represents a reset state (true = reset) typically initiated by the timing system ($I command). |

### EventStatus Structure
| C# Property Name      | JSON Property Name | Type      | Description                                                        | 
|---------------------- |-------------------|-----------|--------------------------------------------------------------------| 
| EventId               | eid               | string?   | The event identifier (as a string, may be null).                   | 
| Flag                  | f                 | Flags     | The current flag status for the event.                             | 
| LapsToGo              | ltg               | int       | The number of laps remaining in the event.                         | 
| TimeToGo              | ttg               | string    | The time remaining in the event (formatted as a string, e.g. "00:12:45").           | 
| LocalTimeOfDay        | tt                | string    | The local time of day (formatted as a string, e.g. "13:34:23").                     | 
| RunningRaceTime       | rt                | string    | The total elapsed race time (formatted as a string, e.g. "00:09:47").               | 
| IsPracticeQualifying  | pq                | bool      | Indicates if the session is practice or qualifying (true/false). This is only a best guess.|

### Flags Enumeration
Represents the various flag states used in timing and scoring to indicate track conditions or race status.

| Enum (Value)   | Description                                                                 | 
|--------------|-----------------------------------------------------------------------------| 
| Unknown (0)    | The flag state is unknown or not recognized.                                | 
| Green (1)     | Normal racing conditions.                               | 
| Yellow (2)    | Caution          | 
| Red  (3)      | The session is stopped.       | 
| White (4)      | Final lap. One lap remaining in the session.                                | 
| Checkered (5)  | The session or race is finished.                                            | 
| Black (6)     | Black flag all.     | 
| Purple35 (7)  | Code 35 or purple flag. Not yet available.           |

#### EventEntry Structure
Represents the details of a competitor or car entry in the event.

| C# Property Name | JSON Property Name | Type   | Description                                 | 
|------------------|-------------------|--------|---------------------------------------------| 
| Number           | no                | string | The car or competitor number.               | 
| Name             | nm                | string | The name of the driver or competitor.       | 
| Team             | t                 | string | The name of the team.                       | 
| Class            | c                 | string | The class or category of the entry.         |


### Car Position Structure
Represents the position of a car in the event.

| C# Property Name            | JSON Property Name | Type      | Description                                                                                  | 
|-----------------------------|-------------------|-----------|----------------------------------------------------------------------------------------------| 
| EventId                     | eid               | string?   | The event identifier.                                                                        | 
| SessionId                   | sid               | string?   | The session identifier.                                                                      | 
| Number                      | n                 | string?   | The car or competitor number.                                                                | 
| TransponderId               | tp                | uint      | The transponder ID assigned to the car.                                                      | 
| Class                       | class             | string?   | The class or category of the car.                                                            | 
| BestTime                    | bt                | string?   | The best lap time for this car (formatted as a string).                                      | 
| BestLap                     | bl                | int       | The lap number on which the best time was set.                                               | 
| IsBestTime                  | ibt               | bool      | Indicates if this is the best time overall.                                                  | 
| IsBestTimeClass             | btc               | bool      | Indicates if this is the best time in class.                                                 | 
| InClassGap                  | cg                | string?   | The time gap to the next car in class.                                                       | 
| InClassDifference           | cd                | string?   | The time difference to the leader in class.                                                  | 
| OverallGap                  | og                | string?   | The time gap to the next car overall.                                                        | 
| OverallDifference           | od                | string?   | The time difference to the overall leader.                                                   |
| TotalTime                   | ttm               | string?   | The total elapsed time for this car.                                                         | 
| LastTime                    | ltm               | string?   | The last lap time for this car.                                                              | 
| LastLap                     | llp               | int       | The last completed lap number.                                                               | 
| OverallPosition             | ovp               | int       | The current overall position of the car.                                                     | 
| ClassPosition               | clp               | int       | The current position of the car within its class.                                            | 
| OverallStartingPosition     | osp               | int       | The starting position of the car overall.                                                    | 
| OverallPositionsGained      | opg               | int       | The number of overall positions gained (or lost if negative).                                | 
| InClassStartingPosition     | icsp              | int       | The starting position of the car within its class.                                           | 
| InClassPositionsGained      | cpg               | int       | The number of positions gained in class (or lost if negative).                               | 
| IsOverallMostPositionsGained| ompg              | bool      | Indicates if this car has gained the most overall positions.                                 | 
| IsClassMostPositionsGained  | cmpg              | bool      | Indicates if this car has gained the most positions in class.                                | 
| PenalityLaps                | pl                | int       | The number of penalty laps assigned to this car.                                             | 
| PenalityWarnings            | pw                | int       | The number of penalty warnings assigned to this car.                                         | 
| IsEnteredPit                | enp               | bool      | Indicates if the car has entered the pit lane.                                               | 
| IsPitStartFinish            | psf               | bool      | Indicates if the car started or finished in the pit lane.                                    | 
| IsExitedPit                 | exp               | bool      | Indicates if the car has exited the pit lane.                                                | 
| IsInPit                     | ip                | bool      | Indicates if the car is currently in the pit lane.                                           | 
| LapIncludedPit              | lip               | bool      | Indicates if the last lap included a pit stop.                                               | 
| LastLoopName                | ln                | string    | The name of the last loop (track segment) crossed.                                           | 
| IsStale                     | st                | bool      | Indicates if the car's position data is stale (not recently updated).                        | 
| Flag                        | flg               | Flags     | The current flag state for this car (see Flags enum for possible values).                    |

### FlagDuration Structure
Represents the duration of a flag state in a session.

| C# Property Name | JSON Property Name | Type         | Description                                                      | 
|------------------|-------------------|--------------|------------------------------------------------------------------| 
| Flag             | f                 | Flags        | The flag state for this duration (see Flags enum for values).    | 
| StartTime        | s                 | DateTime     | The start time of the flag state.                                | 
| EndTime          | e                 | DateTime?    | The end time of the flag state (null if ongoing).                |


