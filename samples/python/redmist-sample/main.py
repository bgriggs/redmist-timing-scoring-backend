"""Main entry point for the RedMist sample application."""

import os
import requests
import msgpack
from dotenv import load_dotenv
import external_telemetry
from session_state import SessionState

# Load environment variables from .env file
load_dotenv()

# Authentication constants
AUTH_URL = "https://auth.redmist.racing/realms/redmist/protocol/openid-connect/token"
CLIENT_ID = os.environ.get("CLIENT_ID", "your-client-id")
CLIENT_SECRET = os.environ.get("CLIENT_SECRET", "your-client-secret")
API_BASE_URL = os.environ.get("API_BASE_URL", "https://api-test.redmist.racing")
EVENT_ID = int(os.environ.get("EVENT_ID", "2"))


def get_access_token() -> str:
    """Get access token using client credentials flow.
    
    Returns:
        str: The access token for API authentication.
        
    Raises:
        requests.HTTPError: If the authentication request fails.
    """
    response = requests.post(
        AUTH_URL,
        data={
            "grant_type": "client_credentials",
            "client_id": CLIENT_ID,
            "client_secret": CLIENT_SECRET,
        },
        headers={
            "Content-Type": "application/x-www-form-urlencoded",
        },
    )
    response.raise_for_status()
    return response.json()["access_token"]


def update_drivers_api(token: str) -> None:
    """Update driver information using the ExternalTelemetry API.
    
    Args:
        token: The access token for API authentication.
    """
    # Create driver data
    drivers = [
        {
            "eventId": EVENT_ID,
            "carNumber": "60",
            "driverName": "Driver1",
            "driverId": "12345",
        }
    ]
    
    # Call the update drivers endpoint
    try:
        response = external_telemetry.update_drivers(
            base_url=API_BASE_URL,
            token=token,
            version="1",
            drivers=drivers,
        )
        
        # Print the result
        if response.status_code == 200:
            print(f"\n✓ Successfully updated driver: Status {response.status_code}")
        else:
            print(f"\n✗ Failed to update driver: Status {response.status_code}")
            print(f"  Response: {response.text}")
    except Exception as e:
        print(f"\n✗ Error updating driver: {e}")

def update_drivers_with_transponder_api(token: str) -> None:
    """Update driver information using the ExternalTelemetry API.
    
    Args:
        token: The access token for API authentication.
    """
    # Create driver data
    drivers = [
        {
            "transponderId": 1329228,
            "driverName": "Driver2",
            "driverId": "12346",
        }
    ]
    
    # Call the update drivers endpoint
    try:
        response = external_telemetry.update_drivers(
            base_url=API_BASE_URL,
            token=token,
            version="1",
            drivers=drivers,
        )
        
        # Print the result
        if response.status_code == 200:
            print(f"\n✓ Successfully updated driver: Status {response.status_code}")
        else:
            print(f"\n✗ Failed to update driver: Status {response.status_code}")
            print(f"  Response: {response.text}")
    except Exception as e:
        print(f"\n✗ Error updating driver: {e}")


def clear_driver_by_car(token: str) -> None:
    """Clear driver information using eventId and carNumber.
    
    Args:
        token: The access token for API authentication.
    """
    # Create driver data with empty strings to clear
    drivers = [
        {
            "eventId": EVENT_ID,
            "carNumber": "60",
            "driverName": "",
            "driverId": "",
        }
    ]
    
    # Call the update drivers endpoint
    try:
        response = external_telemetry.update_drivers(
            base_url=API_BASE_URL,
            token=token,
            version="1",
            drivers=drivers,
        )
        
        # Print the result
        if response.status_code == 200:
            print(f"\n✓ Successfully cleared driver by car: Status {response.status_code}")
        else:
            print(f"\n✗ Failed to clear driver by car: Status {response.status_code}")
            print(f"  Response: {response.text}")
    except Exception as e:
        print(f"\n✗ Error clearing driver by car: {e}")


def clear_driver_by_transponder(token: str) -> None:
    """Clear driver information using transponderId.
    
    Args:
        token: The access token for API authentication.
    """
    # Create driver data with empty strings to clear
    drivers = [
        {
            "transponderId": 1329228,
            "driverName": "",
            "driverId": "",
        }
    ]
    
    # Call the update drivers endpoint
    try:
        response = external_telemetry.update_drivers(
            base_url=API_BASE_URL,
            token=token,
            version="1",
            drivers=drivers,
        )
        
        # Print the result
        if response.status_code == 200:
            print(f"\n✓ Successfully cleared driver by transponder: Status {response.status_code}")
        else:
            print(f"\n✗ Failed to clear driver by transponder: Status {response.status_code}")
            print(f"  Response: {response.text}")
    except Exception as e:
        print(f"\n✗ Error clearing driver by transponder: {e}")


def update_car_videos_api(token: str) -> None:
    """Update car video information using the ExternalTelemetry API.
    
    Args:
        token: The access token for API authentication.
    """
    # First call: using eventId and carNumber
    videos_by_car = [
        {
            "eid": EVENT_ID, # Event ID
            "cn": "60", # Car Number
            "d": [ { # Video Destinations
                    "t": 1, # YouTube
                    "u": "https://youtube.com/watch?v=example1",
                }],
            "t": 3, # MyRacesLive
        }
    ]
    
    try:
        response = external_telemetry.update_car_videos(
            base_url=API_BASE_URL,
            token=token,
            version="1",
            videos=videos_by_car,
        )
        
        if response.status_code == 200:
            print(f"\n✓ Successfully updated video by car: Status {response.status_code}")
        else:
            print(f"\n✗ Failed to update video by car: Status {response.status_code}")
            print(f"  Response: {response.text}")
    except Exception as e:
        print(f"\n✗ Error updating video by car: {e}")
    
    # Second call: using transponder
    videos_by_transponder = [
        {
            "tp": 1329228,
            "d": [ {
                    "t": 4, # SRT
                    "u": "https://youtube.com/watch?v=example1",
                }],
            "t": 1, # Generic
        }
    ]
    
    try:
        response = external_telemetry.update_car_videos(
            base_url=API_BASE_URL,
            token=token,
            version="1",
            videos=videos_by_transponder,
        )
        
        if response.status_code == 200:
            print(f"\n✓ Successfully updated video by transponder: Status {response.status_code}")
        else:
            print(f"\n✗ Failed to update video by transponder: Status {response.status_code}")
            print(f"  Response: {response.text}")
    except Exception as e:
        print(f"\n✗ Error updating video by transponder: {e}")


def clear_car_videos_api(token: str) -> None:
    """Clear car video information using the ExternalTelemetry API.
    
    Args:
        token: The access token for API authentication.
    """
    # First call: clear using eventId and carNumber
    videos_by_car = [
        {
            "eid": EVENT_ID,
            "cn": "60",
        }
    ]
    
    try:
        response = external_telemetry.update_car_videos(
            base_url=API_BASE_URL,
            token=token,
            version="1",
            videos=videos_by_car,
        )
        
        if response.status_code == 200:
            print(f"\n✓ Successfully cleared video by car: Status {response.status_code}")
        else:
            print(f"\n✗ Failed to clear video by car: Status {response.status_code}")
            print(f"  Response: {response.text}")
    except Exception as e:
        print(f"\n✗ Error clearing video by car: {e}")
    
    # Second call: clear using transponder
    videos_by_transponder = [ 
        {
            "tp": 1329228,
        }
    ]
    
    try:
        response = external_telemetry.update_car_videos(
            base_url=API_BASE_URL,
            token=token,
            version="1",
            videos=videos_by_transponder,
        )
        
        if response.status_code == 200:
            print(f"\n✓ Successfully cleared video by transponder: Status {response.status_code}")
        else:
            print(f"\n✗ Failed to clear video by transponder: Status {response.status_code}")
            print(f"  Response: {response.text}")
    except Exception as e:
        print(f"\n✗ Error clearing video by transponder: {e}")


def get_current_session_state(token: str, event_id: int) -> SessionState | None:
    """Get the current session state for an event and deserialize from MessagePack.
    
    Args:
        token: The access token for API authentication.
        event_id: The event ID to get session state for.
        
    Returns:
        SessionState object if successful, None otherwise.
    """
    url = f"{API_BASE_URL}/status/v2/Events/GetCurrentSessionState"
    
    headers = {
        "Authorization": f"Bearer {token}",
        "Accept": "application/x-msgpack",
    }
    
    params = {
        "eventId": event_id,
    }
    
    try:
        print(f"\nFetching session state for event {event_id}...")
        response = requests.get(url, headers=headers, params=params)
        response.raise_for_status()
        
        # Deserialize MessagePack response
        raw_data = msgpack.unpackb(response.content, raw=False, strict_map_key=False)
        
        print(f"Debug: MessagePack data type: {type(raw_data)}")
        if isinstance(raw_data, list):
            print(f"Debug: Array length: {len(raw_data)}")
        
        # Check if it's a dict or list
        if isinstance(raw_data, dict):
            session_state = SessionState.from_msgpack_dict(raw_data)
        elif isinstance(raw_data, list):
            # If it's a list, the data is in array format with integer indices
            # Convert list to dict with integer keys
            data_dict = {i: raw_data[i] for i in range(len(raw_data))}
            session_state = SessionState.from_msgpack_dict(data_dict)
        else:
            raise ValueError(f"Unexpected MessagePack format: {type(raw_data)}")
        
        print(f"✓ Successfully retrieved session state")
        print(f"\n  Event: {session_state.event_name} (ID: {session_state.event_id})")
        print(f"  Session: {session_state.session_name} (ID: {session_state.session_id})")
        print(f"  Is Live: {session_state.is_live}")
        print(f"  Current Flag: {session_state.current_flag.name}")
        print(f"  Laps to Go: {session_state.laps_to_go}")
        print(f"  Time to Go: {session_state.time_to_go}")
        print(f"  Event Entries: {len(session_state.event_entries)}")
        print(f"  Car Positions: {len(session_state.car_positions)}")
        
        if session_state.car_positions:
            print(f"\n  Top 5 Positions:")
            for car in sorted(session_state.car_positions, key=lambda c: c.overall_position)[:5]:
                print(f"    P{car.overall_position} #{car.number} - {car.driver_name} ({car.car_class})")
                if car.best_time:
                    print(f"       Best: {car.best_time}, Last: {car.last_lap_time}")
        
        return session_state
        
    except requests.HTTPError as e:
        print(f"\n✗ Failed to get session state: {e}")
        if hasattr(e.response, 'text'):
            print(f"  Response: {e.response.text}")
        return None
    except Exception as e:
        print(f"\n✗ Error getting session state: {e}")
        import traceback
        traceback.print_exc()
        return None


def main():
    try:
        token = get_access_token()
        
        # Get current session state for event ID 2
        session_state = get_current_session_state(token, event_id=EVENT_ID)
        
        input("\nPress Enter to continue with driver updates...")
        
        # Update driver information
        # Must be set at least once every 10 minutes or drivers will expire
        update_drivers_api(token)
        update_drivers_with_transponder_api(token)

        input("\nPress Enter to clear drivers...")
        
        # Clear driver information
        clear_driver_by_car(token)
        clear_driver_by_transponder(token)

        input("\nPress Enter to set video streams...")
        
        # Update car video information
        # Must be set at least once every 10 minutes or stream will expire
        update_car_videos_api(token)
        
        input("\nPress Enter to clear video streams...")
        
        # Clear car video information
        clear_car_videos_api(token)
        

    except requests.HTTPError as e:
        print(f"\nFailed to get access token: {e}")
    except Exception as e:
        print(f"\nError: {e}")


if __name__ == "__main__":
    main()
