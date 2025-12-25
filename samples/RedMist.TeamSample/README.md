# Red Mist Team Sample

A C# console application demonstrating how to monitor a specific race car and its immediate competition using the Red Mist Racing timing and scoring platform. This sample is designed for race teams to track their car's position, lap times, pit status, and gaps to competitors in real-time.

## Overview

The Red Mist Team Sample provides a focused example of how a race team can:

- **Monitor a specific car** by number (e.g., car #2)
- **Track real-time position updates** including overall and class positions
- **Monitor lap times** as they are recorded
- **Check pit status** to know when cars are in or out of the pits
- **Calculate gaps** to the overall leader and class leader
- **Display competition updates** for the overall leader and class leader

This is a simpler, more focused application compared to the full-featured `RedMist.SampleProject`, making it ideal for teams that want to monitor their car without navigating through complex menus.

## Prerequisites

- **.NET 10 SDK** or later
- **Visual Studio 2022** (17.12 or later) or **Visual Studio Code** with C# extension
- **Red Mist API credentials** (Client ID and Client Secret)

## Getting Started

### 1. Obtain API Credentials

Contact Red Mist Racing or visit [https://redmist.racing](https://redmist.racing) to obtain your API credentials:
- **Client ID**
- **Client Secret**

These credentials are required to authenticate with the Red Mist API.

### 2. Configure the Application

Open `appsettings.json` in the `samples/RedMist.TeamSample` directory and add your credentials to the `Keycloak` section:

```json
{
  "Keycloak": {
    "Realm": "redmist",
    "AuthServerUrl": "https://auth.redmist.racing",
    "ClientId": "YOUR_CLIENT_ID_HERE",
    "ClientSecret": "YOUR_CLIENT_SECRET_HERE"
  }
}
```

**Important**: Replace `YOUR_CLIENT_ID_HERE` and `YOUR_CLIENT_SECRET_HERE` with your actual credentials.

### 3. Configure Your Car and Class

In `Program.cs`, update these constants at the top of the file to match your car:

```csharp
const string MyCarNumber = "2";      // Your car number
const string MyClass = "GTO";         // Your racing class
```

### 4. Run the Application

#### Using Visual Studio 2022

1. Open the solution file `RedMist.TimingAndScoring.sln` in Visual Studio
2. In Solution Explorer, navigate to `samples/RedMist.TeamSample`
3. Right-click on the `RedMist.TeamSample` project and select **Set as Startup Project**
4. Press **F5** or click the **Start** button to run with debugging
   - Or press **Ctrl+F5** to run without debugging

#### Using Visual Studio Code

1. Open the `samples/RedMist.TeamSample` folder in VS Code
2. Open the integrated terminal (**Terminal > New Terminal**)
3. Run the following command:
   ```bash
   dotnet run
   ```

#### Using Command Line

1. Navigate to the sample project directory:
   ```bash
   cd samples/RedMist.TeamSample
   ```
2. Run the application:
   ```bash
   dotnet run
   ```

## What to Expect

When you run the application:

1. **Event List**: The application will display all available events and automatically connect to the first live event
2. **Initial Status**: You'll see the current session information loaded
3. **Real-time Updates**: As cars complete laps, you'll receive updates showing:
   - Your car's position, last lap time, and pit status
   - Gap to the overall leader and class leader
   - Updates when the overall leader completes a lap
   - Updates when your class leader completes a lap

### Sample Output

```
Event ID: 123, Name: Summer Racing Championship, Track: Laguna Seca, Live: True
Loaded event status for event ID 123, current session: Race 1
Connected to subscription service
Application started

Car 2 is in position 5 last lap 01:45.234.
Car 2 pit status is: False
Car 2 gap to overall leader is 00:12.456 and in class 00:03.789

Car 1 is in the lead.
Car 1 pit status is: False

Car 3 is leading class GTO.
Car 3 pit status is: False
```

The application will continue running and displaying updates until you press **Enter** to exit.

## Understanding the Code

### Key Components

1. **StatusClient**: Handles REST API calls to load event lists and historical data
2. **StatusSubscriptionClient**: Manages the real-time SignalR connection for live updates
3. **Event Handlers**:
   - `CarPatchesReceived`: Processes incoming car position updates
   - `ConnectionStatusChanged`: Handles connection status and reconnection logic

### Customization

You can easily modify the application to:

- Track multiple cars by expanding the logic in `CarPatchesReceived`
- Filter for specific events by modifying the event selection logic
- Display additional data from the `CarPositionPatch` object (speed, sector times, etc.)
- Add alerts for specific conditions (pit stops, position changes, etc.)

### Available Car Data

Each `CarPositionPatch` update includes:
- Car number and driver name
- Overall position and class position
- Last lap time and best lap time
- Pit status (in/out of pits)
- Gaps to leader (overall and in-class)
- Current speed (if available)
- And more...

## Troubleshooting

### "No live event found"

- Make sure there is an active live event in the Red Mist system
- You can modify the code to manually specify an event ID instead of auto-selecting

### Authentication Errors

- Verify your Client ID and Client Secret are correct in `appsettings.json`
- Ensure there are no extra spaces or quotes around your credentials
- Confirm your credentials have not expired

### Connection Issues

- Check your internet connection
- Verify the API endpoints in `appsettings.json` are correct:
  - `Server.EventUrl`: `https://api.redmist.racing/status/v2/Events`
  - `Hub.Url`: `https://api.redmist.racing/status/event-status`

### No Updates Appearing

- Ensure the event is actually live and generating timing data
- Check that your car number matches exactly (including any leading zeros)
- Verify the class name is spelled correctly

## Security Best Practices

### Using User Secrets (Recommended)

Instead of storing credentials in `appsettings.json`, you can use .NET User Secrets for better security:

1. In the project directory, run:
   ```bash
   dotnet user-secrets init
   dotnet user-secrets set "Keycloak:ClientId" "YOUR_CLIENT_ID"
   dotnet user-secrets set "Keycloak:ClientSecret" "YOUR_CLIENT_SECRET"
   ```

2. Remove the credentials from `appsettings.json`

User Secrets are stored outside your project directory and won't be committed to source control.

## Next Steps

- Explore the full-featured **RedMist.SampleProject** for more API capabilities
- Review the Red Mist API documentation for additional endpoints and data
- Build your own custom team dashboard or pit strategy application
- Integrate with data visualization tools or timing displays

## Support

For questions, issues, or feature requests:
- Visit: [https://redmist.racing](https://redmist.racing)
- GitHub Issues: [https://github.com/bgriggs/redmist-timing-scoring-backend/issues](https://github.com/bgriggs/redmist-timing-scoring-backend/issues)

## License

This sample application is provided as-is for demonstration purposes. Please refer to the main repository for licensing information.
