# Upgrading to API Version 2
As of January 1, 2026, the Version 1 API has been officially deprecated. To ensure uninterrupted service and access to the latest features, all users must transition to Version 2 of the API.

The main breaking change is the Version 1 SignalR websocket SubscribeToEvent call is no longer supported due to performance reasons. As a temporary measure, if you still require access to the Version 1 API data format, a new direct request endpoint call has been added that will return the [Payload](https://github.com/bgriggs/redmist-timing-common/blob/main/RedMist.TimingCommon/Models/Payload.cs) in the 
Version 1 format `GET https://api.redmist.racing/status/v2/Events/GetCurrentLegacySessionPayload?eventId=123`. 
However, this is only a stopgap solution, and we strongly recommend migrating to Version 2 as soon as possible.

## Real-Time Breaking Changes
Subscriptions to real-time status have been updated to use version 2 whether calling `SubscribeToEvent` or `SubscribeToEventV2`. Both methods now return Version 2 data format.

The main differences between version 1 and version 2 response structure is the change to session and cars are now separate, there is no full status that is sent on connection or periodically, and the session status has been changed to use SessionState rather than Payload. 

- `ReceiveSessionPatch`: this will include [SessionState](https://github.com/bgriggs/redmist-timing-common/blob/main/RedMist.TimingCommon/Models/SessionState.cs) updates only. Changes will include any non-null fields in the response. A non-null value indicates a change; fields that have not changed will be null. When a field is cleared or set to a default value, a non-null default value will be provided rather than null.

- `ReceiveCarPatches`: this includes an array of [CarPosition](https://github.com/bgriggs/redmist-timing-common/blob/main/RedMist.TimingCommon/Models/CarPosition.cs). Changes include any non-null fields in the response. The car ID field is always populated to identify which car the patch applies to. A non-null value indicates a change; fields that have not changed will be null.

- The timing system reset command is also broken out to its own method: `ReceiveReset`. When this event is received, it is recommended to clear your local session state and re-request the full session state by calling the `GetCurrentSessionState` endpoint.

Here is an example of how to handle the new methods in C#:
```code csharp
hub.On("ReceiveSessionPatch", (SessionStatePatch ssp) => ProcessSessionMessage(ssp));
hub.On("ReceiveCarPatches", (CarPositionPatch[] cpps) => ProcessCarPatches(cpps));
hub.On("ReceiveReset", ProcessReset);
```

`ProcessSessionMessage`, `ProcessCarPatches`, and `ProcessReset` are your custom methods to handle the incoming data.

It is recommended at the startup of your application to get the initial full status of the event by calling the `GetCurrentSessionState` endpoint to populate your local state.
Your `ProcessSessionMessage` and `ProcessCarPatches` methods should then apply the changes to your local state by replacing the values on the initial status with any non-null object fields received by the `ReceiveSessionPatch` and `ReceiveCarPatches` methods.

`SessionStatePatch` and `CarPositionPatch` are similar to `SessionState` and `CarPosition` but all fields are nullable to indicate if a field has changed.

If you want full status updates periodically, you will need to implement that logic in your application by calling the `GetCurrentSessionState` endpoint on a timer.

## REST API Changes
The REST API endpoints have been updated to version 2. 

Full status updates are no longer included in the SignalR websocket, only changes. To get full status of an event, call `GET https://api.redmist.racing/status/v2/Events/GetCurrentSessionState?eventId=123`
This returns a MessagePack response application/x-msgpack for best performance. MessagePack provides significantly better performance than JSON due to its binary serialization format, which results in smaller payload sizes and faster serialization/deserialization.

If you must have JSON, call `GET https://api.redmist.racing/status/v2/Events/GetCurrentSessionStateJson?eventId=123` with accept header application/json.

For temporary access to the Version 1 [Payload](https://github.com/bgriggs/redmist-timing-common/blob/main/RedMist.TimingCommon/Models/Payload.cs) format, 
call `GET https://api.redmist.racing/status/v2/Events/GetCurrentLegacySessionPayload?eventId=123`.

# OpenAPI / Swagger
For reference, the full OpenAPI specification for Version 2 of the Status API can be found at:
- https://api.redmist.racing/status/swagger/index.html
- https://api.redmist.racing/status/swagger/v2/swagger.json