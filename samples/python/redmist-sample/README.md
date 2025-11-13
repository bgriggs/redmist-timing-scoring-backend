# RedMist Sample

A simple console application that demonstrates RedMist API authentication.

## Setup

1. Install dependencies:

```bash
poetry install
```

2. Create a `.env` file from the example:

```bash
cp .env.example .env
```

3. Edit `.env` and add your RedMist API credentials:

```
CLIENT_ID=your-actual-client-id
CLIENT_SECRET=your-actual-client-secret
API_BASE_URL=https://api.redmist.racing
SIGNALR_HUB_URL=https://api.redmist.racing/status/event-status
EVENT_ID=1
```

**Note:** The `.env` file is automatically ignored by git to keep your secrets safe.

## Usage

Run the main application. This includes getting the full race session state, driver info, and video streams:

```bash
poetry run python main.py
```

Run the API test to fetch session state. This is the full race status:

```bash
poetry run python test_session_state_api.py
```

Run the SignalR client to receive real-time updates. This include changed fields for session state and car positions:

```bash
poetry run python signalr_client.py
```
