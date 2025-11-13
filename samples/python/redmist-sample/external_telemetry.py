"""Client for RedMist External Telemetry API."""

import requests
from typing import Any


def update_drivers(
    base_url: str,
    token: str,
    version: str,
    drivers: list[dict[str, Any]],
) -> requests.Response:
    """Update driver information via the External Telemetry API.
    
    Args:
        base_url: The base URL of the API (e.g., "https://api-test.redmist.racing")
        token: The authentication token
        version: The API version (e.g., "1")
        drivers: List of driver dictionaries with fields:
            - eventId: int - The event ID
            - carNumber: str - The car number
            - driverName: str - The driver's name
            - driverId: str - The driver's ID
            - transponderId: int (optional) - The transponder ID
    
    Returns:
        requests.Response: The HTTP response from the API
        
    Raises:
        requests.HTTPError: If the request fails
    """
    url = f"{base_url}/status/v{version}/ExternalTelemetry/UpdateDrivers"
    
    headers = {
        "Authorization": f"Bearer {token}",
        "Content-Type": "application/json",
    }
    
    response = requests.post(url, json=drivers, headers=headers)
    return response


def update_car_videos(
    base_url: str,
    token: str,
    version: str,
    videos: list[dict[str, Any]],
) -> requests.Response:
    """Update car video information via the External Telemetry API.
    
    Args:
        base_url: The base URL of the API (e.g., "https://api-test.redmist.racing")
        token: The authentication token
        version: The API version (e.g., "1")
        videos: List of video dictionaries with fields:
            - eventId: int - The event ID
            - carNumber: str - The car number
            - transponderId: int (optional) - The transponder ID
            - videoUrl: str - The URL of the video
            - videoSystemType: str - Type of video system (e.g., "YouTube", "Facebook")
    
    Returns:
        requests.Response: The HTTP response from the API
        
    Raises:
        requests.HTTPError: If the request fails
    """
    url = f"{base_url}/status/v{version}/ExternalTelemetry/UpdateCarVideos"
    
    headers = {
        "Authorization": f"Bearer {token}",
        "Content-Type": "application/json",
    }
    
    response = requests.post(url, json=videos, headers=headers)
    return response
