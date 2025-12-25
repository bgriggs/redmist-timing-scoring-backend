# Red Mist Sample Project

A C# console application demonstrating how to integrate with the Red Mist Racing timing and scoring platform. This sample project showcases three main clients for interacting with the Red Mist API: REST API calls, real-time SignalR subscriptions, and external telemetry management.

## Overview

The Red Mist Sample Project provides ready-to-use examples for:

- **Status API (REST)**: Query event data, sessions, car laps, competitor metadata, control logs, and more
- **Status Subscriptions (SignalR)**: Real-time updates for live events, control logs, and in-car driver data
- **External Telemetry**: Manage driver information and video metadata for cars

### Project Features

- Interactive console menu system with hierarchical navigation
- Three specialized clients:
  - `StatusClient`: REST API operations for retrieving event and session data
  - `StatusSubscriptionClient`: SignalR-based real-time subscriptions
  - `ExternalTelemetryClient`: Driver and video metadata management
- JSON serialization for easy data inspection

## Prerequisites

- **.NET 9 SDK** or later
- **Visual Studio 2022** (17.8 or later) or **Visual Studio Code** with C# extension
- **Red Mist API credentials** (Client ID and Client Secret)

## Getting Started

### 1. Obtain API Credentials

Contact Red Mist Racing to obtain your API credentials:
- Client ID
- Client Secret

These credentials are required to authenticate with the Red Mist API.

### 2. Configure the Application

#### Option A: Using appsettings.json (Recommended for Development)

1. Open `appsettings.json` in the sample project directory
2. Add your credentials to the `Keycloak` section:

```json
{
  "Keycloak": {
    "Realm": "redmist",
    "AuthServerUrl": "https://auth.redmist.racing",
    "SslRequired": "external",
    "Resource": "account",
    "ClientId": "your-client-id-here",
    "ClientSecret": "your-client-secret-here"
  }
}
```

**Note**: Do not commit `appsettings.json` with credentials to source control.

#### Option B: Using User Secrets (Recommended for Security)

User Secrets provide a secure way to store sensitive configuration without exposing it in source control.

**Using Visual Studio:**
1. Right-click the `RedMist.SampleProject` project in Solution Explorer
2. Select **Manage User Secrets**
3. Add your credentials to the opened `secrets.json` file:

```json
{
  "Keycloak:ClientId": "your-client-id-here",
  "Keycloak:ClientSecret": "your-client-secret-here"
}
```

**Using Command Line:**
```bash
cd samples/RedMist.SampleProject
dotnet user-secrets set "Keycloak:ClientId" "your-client-id-here"
dotnet user-secrets set "Keycloak:ClientSecret" "your-client-secret-here"
```

## Running the Application

### Visual Studio 2022

1. Open `redmist-timing-scoring-backend.sln` in Visual Studio
2. In Solution Explorer, right-click the `RedMist.SampleProject` project
3. Select **Set as Startup Project**
4. Press **F5** or click the **Start** button to run with debugging
   - Or press **Ctrl+F5** to run without debugging

### Visual Studio Code

1. Open the workspace folder in VS Code
2. Open a terminal in VS Code (Terminal → New Terminal)
3. Navigate to the sample project directory:
   ```bash
   cd samples/RedMist.SampleProject
   ```
4. Run the application:
   ```bash
   dotnet run
   ```

### Command Line

From the repository root directory:
```bash
cd samples/RedMist.SampleProject
dotnet run
```

## Using the Application

Upon starting, you'll see the main menu with three options:

```
==== Red Mist Sample Client ====

1. Status API (REST)
2. Status Subscriptions (SignalR)
3. External Telemetry

0. Exit
```

### 1. Status API (REST)

Access read-only data through REST API endpoints:

- **Load Recent Events**: Get a list of recent and live events
- **Load Event**: Retrieve detailed information about a specific event
- **Load Event Status**: Get current status of a live event
- **Load Car Laps**: Query lap data for a specific car in a session
- **Load Sessions**: List all sessions for an event
- **Load Session Results**: Get final results for a completed session
- **Load Competitor Metadata**: Retrieve metadata for a specific competitor
- **Load Control Log**: Get control/penalty log entries for an event
- **Load Car Control Logs**: Get control logs specific to a car
- **Load In-Car Driver Mode Payload**: Retrieve in-car driver display data
- **Load Flags**: Get flag history for a session

### 2. Status Subscriptions (SignalR)

Subscribe to real-time updates via SignalR WebSocket connections:

- **Subscribe to Event**: Receive live updates for event status, car positions, and session changes
- **Subscribe to Control Logs**: Get real-time notifications for control log entries
- **Subscribe to Car Control Logs**: Monitor control logs for a specific car
- **Subscribe to In-Car Driver Event**: Receive live in-car driver mode updates

**Note**: After subscribing, keep the application running to receive updates. All real-time data is logged to the console.

### 3. External Telemetry

Manage driver and video metadata for cars:

- **Set Driver External Telemetry**: Associate driver information with cars
- **Remove Driver External Telemetry**: Clear driver associations
- **Set Video External Telemetry**: Link video streams to cars
- **Remove Video External Telemetry**: Clear video stream associations

Drivers and videos can be linked by:
- Event ID + Car Number
- Transponder ID

## Example Workflows

### Viewing Live Event Data

1. Select **Status API (REST)** → **Load Recent Events**
2. Note an event ID from the results
3. Select **Load Event Status** and enter the event ID
4. View the current session state, flag status, and more

### Monitoring Real-Time Updates

1. Select **Status Subscriptions (SignalR)** → **Subscribe to Event**
2. Enter an event ID for a live event
3. Leave the application running
4. Watch the console for real-time updates as the session progresses

### Setting Driver Information

1. Edit the `EVENTID` constant in `Program.cs` to match your event
2. Select **External Telemetry** → **Set Driver External Telemetry**
3. The sample will associate driver information with specific cars

## Troubleshooting

### Authentication Errors

**Problem**: Receiving 401 Unauthorized errors

**Solution**: 
- Verify your Client ID and Client Secret are correct
- Ensure credentials are properly configured in `appsettings.json` or User Secrets
- Check that your credentials have the necessary permissions

### SignalR Connection Issues

**Problem**: SignalR subscriptions not receiving updates

**Solution**:
- Ensure you're subscribing to an active/live event
- Check network connectivity and firewall settings
- Enable `Debug` logging to see detailed connection information
- Verify the Hub URL is correct in `appsettings.json`

### No Events Found

**Problem**: Load Recent Events returns empty list

**Solution**:
- Verify your organization has events configured
- Check that your credentials have access to view events
- Ensure you're connected to the correct environment

## Support

For API access, technical support, or questions:
- Visit: [https://redmist.racing](https://redmist.racing)
- Email: support@redmist.racing

## License

This sample project is provided as-is for demonstration purposes.
