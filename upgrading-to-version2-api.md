# Upgrading to API Version 2
As of January 1, 2026, the Version 1 API has been officially deprecated. To ensure uninterrupted service and access to the latest features, all users must transition to Version 2 of the API.

Version 1 SignalR websocket is no longer supported due to performance reasons. As a temporary measure, if you still require access to the Version 1 API data format, a new endpoint call has been added that will return the [Payload](https://github.com/bgriggs/redmist-timing-common/blob/main/RedMist.TimingCommon/Models/Payload.cs) in the 
Version 1 format `GET https://api.redmist.racing/status/v2/Events/GetCurrentLegacySessionPayload/{eventId}`. 
However, this is only a stopgap solution, and we strongly recommend migrating to Version 2 as soon as possible.

## Real-Time Breaking Changes
Subscriptions to real-time status have been updated to use version 2 whether calling `SubscribeToEvent` or `SubscribeToEventV2`.

The main difference between version 1 and version 2 response structure is the change to session and cars are now separate. 

- `ReceiveSessionPatch`: this will include [SessionState](https://github.com/bgriggs/redmist-timing-common/blob/main/RedMist.TimingCommon/Models/SessionState.cs) updates only. Changes will include any non-null fields in the response.

- `ReceiveCarPatches`: this includes an array of [CarPosition](https://github.com/bgriggs/redmist-timing-common/blob/main/RedMist.TimingCommon/Models/CarPosition.cs). Changes include any non-null fields in the response.

- The timing system reset command is also broken out to its own method: `ReceiveReset`.

Here is an example of how to handle the new methods in C#:
```code csharp
hub.On("ReceiveSessionPatch", (SessionStatePatch ssp) => ProcessSessionMessage(ssp));
hub.On("ReceiveCarPatches", (CarPositionPatch[] cpps) => ProcessCarPatches(cpps));
hub.On("ReceiveReset", ProcessReset);
```

`ProcessSessionMessage`, `ProcessCarPatches`, and `ProcessReset` are your custom methods to handle the incoming data.

It is recommended at the startup of your application to get the initial full status of the event by calling the `GetCurrentSessionState` endpoint to populate your local state.
Your `ProcessSessionMessage` and `ProcessCarPatches` methods should then apply the changes to your local state by replacing the values on the initial status with any non-null object fields received by the `ReceiveSessionPatch` and `ReceiveCarPatches` methods.

## REST API Changes
The REST API endpoints have been updated to version 2. 

Full status updates are no longer included in the SignalR websocket, only changes. To get full status of an event, call `GET https://api.redmist.racing/status/v2/Events/GetCurrentSessionState/{eventId}`
This returns a MessagePack response application/x-msgpack for best performance.

If you you must have JSON, call `GET https://api.redmist.racing/status/v2/Events/GetCurrentSessionStateJson/{eventId}` with accept header application/json.

For temporary access to the Version 1 [Payload](https://github.com/bgriggs/redmist-timing-common/blob/main/RedMist.TimingCommon/Models/Payload.cs) format, 
call `GET https://api.redmist.racing/status/v2/Events/GetCurrentLegacySessionPayload/{eventId}`.

# OpenAPI / Swagger
For reference, the full OpenAPI specification for Version 2 of the Status API can be found at:
- https://api.redmist.racing/status/swagger/index.html
- https://api.redmist.racing/status/swagger/v2/swagger.json