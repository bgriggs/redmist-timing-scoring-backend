"""
SignalR client for RedMist status hub.
Connects to the event status hub and receives real-time updates.
"""
import os
import asyncio
import msgpack
from signalrcore.hub_connection_builder import HubConnectionBuilder
from session_state import SessionState, CarPosition, Flags


def _filter_none_values(obj):
    """
    Recursively filter out None values from dictionaries and lists.
    Returns a cleaned version with only non-None values.
    """
    if isinstance(obj, dict):
        return {k: _filter_none_values(v) for k, v in obj.items() if v is not None}
    elif isinstance(obj, list):
        return [_filter_none_values(item) for item in obj if item is not None]
    else:
        return obj


class RedMistStatusHub:
    """Client for connecting to RedMist SignalR status hub."""
    
    def __init__(self, token: str, hub_url: str = None):
        """
        Initialize the SignalR hub connection.
        
        Args:
            token: Authentication token for the API.
            hub_url: URL of the SignalR hub. If not provided, uses SIGNALR_HUB_URL from environment
                     or defaults to https://api-test.redmist.racing/status/event-status
        """
        self.token = token
        self.hub_url = hub_url or os.getenv("SIGNALR_HUB_URL", "https://api-test.redmist.racing/status/event-status")
        self.connection = None
        
    def build_connection(self):
        """Build the SignalR hub connection with authentication."""
        self.connection = (
            HubConnectionBuilder()
            .with_url(
                self.hub_url,
                options={
                    "access_token_factory": lambda: self.token,
                    "headers": {
                        "Authorization": f"Bearer {self.token}"
                    }
                }
            )
            .with_automatic_reconnect({
                "type": "interval",
                "keep_alive_interval": 10,
                "intervals": [0, 2, 10, 30]
            })
            .build()
        )
        
        # Register event handlers
        self.connection.on_open(self._on_open)
        self.connection.on_close(self._on_close)
        self.connection.on_error(self._on_error)
        
        # Register message handlers
        self.connection.on("ReceiveSessionPatch", self._handle_session_patch)
        self.connection.on("ReceiveCarPatches", self._handle_car_patches)
        
        return self.connection
    
    def _on_open(self):
        """Called when connection is opened."""
        print("✓ SignalR connection opened")
    
    def _on_close(self):
        """Called when connection is closed."""
        print("✗ SignalR connection closed")
    
    def _on_error(self, data):
        """Called when an error occurs."""
        print(f"✗ SignalR error: {data}")
    
    def _handle_session_patch(self, data):
        """
        Handle ReceiveSessionPatch event.
        
        Args:
            data: Already-deserialized SessionState patch data (dict or list from MessagePack).
                  Only fields that changed will have non-null values.
        """
        try:
            # Convert list to dict if needed
            if isinstance(data, list):
                data = {i: v for i, v in enumerate(data)}
            elif not isinstance(data, dict):
                print(f"⚠️  Unexpected data type: {type(data)}")
                return
            
            print(f"\n📊 Session Update Received:")
            
            # Show only non-null fields (these are the changes)
            # The data comes as a dict with camelCase keys from C#
            change_count = 0
            for key, value in data.items():
                if value is not None:
                    # Convert camelCase to snake_case for display
                    import re
                    if isinstance(key, str):
                        field_name = re.sub(r'(?<!^)(?=[A-Z])', '_', key).lower()
                    else:
                        field_name = f'field_{key}'
                    
                    # Format the value appropriately
                    if field_name == 'current_flag' or key == 'currentFlag':
                        try:
                            flag = Flags(value)
                            print(f"   {field_name}: {flag.name}")
                        except:
                            print(f"   {field_name}: {value}")
                    elif key in ['carPositions', 'eventEntries', 'announcements', 'sections', 'flagDurations']:
                        if isinstance(value, list):
                            print(f"   {field_name}: {len(value)} items")
                        else:
                            print(f"   {field_name}: {value}")
                    elif key == 'classColors':
                        if isinstance(value, dict):
                            print(f"   {field_name}: {len(value)} classes")
                        else:
                            print(f"   {field_name}: {value}")
                    elif isinstance(value, dict):
                        # For nested objects, filter out None values before displaying
                        filtered_value = _filter_none_values(value)
                        if filtered_value:  # Only print if there's actual data
                            print(f"   {field_name}: {filtered_value}")
                            change_count += 1
                        continue
                    else:
                        print(f"   {field_name}: {value}")
                    change_count += 1
            
            if change_count == 0:
                print("   (No changes)")
            
        except Exception as e:
            print(f"✗ Error handling session patch: {e}")
            import traceback
            traceback.print_exc()
    
    def _handle_car_patches(self, data):
        """
        Handle ReceiveCarPatches event.
        
        Args:
            data: Already-deserialized array of CarPosition patch data from MessagePack.
                  Only fields that changed will have non-null values.
        """
        try:
            if not isinstance(data, list):
                print(f"✗ Expected array of cars, got: {type(data)}")
                return
            
            print(f"\n🏎️  Car Updates Received: {len(data)} cars")
            
            import re
            
            # Process each car patch
            for car_data in data:
                # Debug: see what type/structure we're getting
                # print(f"DEBUG: car_data type={type(car_data)}, keys={list(car_data.keys())[:5] if isinstance(car_data, dict) else 'N/A'}")
                
                # Convert to dict if it's a list
                if isinstance(car_data, list):
                    car_dict = {i: v for i, v in enumerate(car_data)}
                elif isinstance(car_data, dict):
                    car_dict = car_data
                else:
                    print(f"✗ Unexpected car data format: {type(car_data)}")
                    continue
                
                # Find car identifier from the patch (number or driverName)
                # Note: In patch mode, most fields will be None except what changed
                # Check top-level first (string keys)
                car_number = None
                driver_name = None
                
                # Try to get number from top-level dict
                if 'number' in car_dict:
                    num_value = car_dict['number']
                    if num_value is not None and not isinstance(num_value, dict):
                        car_number = num_value
                elif 2 in car_dict:
                    num_value = car_dict[2]
                    if num_value is not None and not isinstance(num_value, dict):
                        car_number = num_value
                    
                # Try to get driver name from top-level dict
                if 'driverName' in car_dict:
                    name_value = car_dict['driverName']
                    if name_value is not None and not isinstance(name_value, dict):
                        driver_name = name_value
                elif 44 in car_dict:
                    name_value = car_dict[44]
                    if name_value is not None and not isinstance(name_value, dict):
                        driver_name = name_value
                
                # If no number/name found at top level, try to extract from nested dicts (field_0, field_1, etc.)
                if not car_number and not driver_name:
                    # Check numeric keys first (0, 1, 2...) which represent the array indices
                    for key in sorted([k for k in car_dict.keys() if isinstance(k, int)]):
                        value = car_dict[key]
                        if isinstance(value, dict):
                            if 'number' in value and value['number'] is not None:
                                car_number = value['number']
                                break
                            elif 'driverName' in value and value['driverName'] is not None:
                                driver_name = value['driverName']
                                break
                
                if car_number:
                    car_id = str(car_number)
                elif driver_name:
                    car_id = f"Driver: {driver_name}"
                else:
                    car_id = "Unknown"
                
                # Safety check: ensure car_id is a simple string, not a complex object
                if len(str(car_id)) > 50:  # If it's too long, it's probably a dict
                    car_id = "Unknown"
                
                print(f"   Car #{car_id}:")
                
                # Show only non-null fields (these are the changes)
                change_count = 0
                for key, value in car_dict.items():
                    if value is not None:
                        # Convert camelCase to snake_case for display
                        if isinstance(key, str):
                            field_name = re.sub(r'(?<!^)(?=[A-Z])', '_', key).lower()
                        else:
                            field_name = f'field_{key}'
                        
                        # Format special fields
                        if key in ['completedSections', 'inCarVideo'] and isinstance(value, list):
                            print(f"      {field_name}: {len(value)} items")
                        elif isinstance(value, dict):
                            # For nested objects, filter out None values before displaying
                            filtered_value = _filter_none_values(value)
                            if filtered_value:  # Only print if there's actual data
                                print(f"      {field_name}: {filtered_value}")
                                change_count += 1
                            continue
                        else:
                            print(f"      {field_name}: {value}")
                        change_count += 1
                
                if change_count == 0:
                    print(f"      (No changes)")
            
        except Exception as e:
            print(f"✗ Error handling car patches: {e}")
            import traceback
            traceback.print_exc()
    
    def subscribe_to_event(self, event_id: int):
        """
        Subscribe to event updates.
        
        Args:
            event_id: The event ID to subscribe to.
        """
        try:
            print(f"\n📡 Subscribing to event {event_id}...")
            # Use send() method for server invocation
            self.connection.send("SubscribeToEventV2", [event_id])
            print(f"✓ Subscribed to event {event_id}")
        except Exception as e:
            print(f"✗ Error subscribing to event: {e}")
            import traceback
            traceback.print_exc()
    
    async def start(self, event_id: int):
        """
        Start the SignalR connection and subscribe to event.
        
        Args:
            event_id: The event ID to subscribe to.
        """
        print(f"\n🔌 Connecting to SignalR hub: {self.hub_url}")
        self.build_connection()
        self.connection.start()
        
        # Wait for connection to establish
        await asyncio.sleep(2)
        
        # Subscribe to event
        self.subscribe_to_event(event_id)
    
    def stop(self):
        """Stop the SignalR connection."""
        if self.connection:
            print("\n🔌 Disconnecting from SignalR hub...")
            self.connection.stop()


async def main_example(token: str, event_id: int = 2):
    """
    Example usage of RedMist SignalR status hub.
    
    Args:
        token: Authentication token.
        event_id: Event ID to subscribe to (default: 2).
    """
    hub = RedMistStatusHub(token)
    
    try:
        # Start connection and subscribe
        await hub.start(event_id)
        
        print("\n✓ Connected and subscribed. Listening for updates...")
        print("Press Ctrl+C to stop\n")
        
        # Keep connection alive
        while True:
            await asyncio.sleep(1)
            
    except KeyboardInterrupt:
        print("\n\nStopping...")
    finally:
        hub.stop()


if __name__ == "__main__":
    import os
    from dotenv import load_dotenv
    from main import get_access_token
    
    # Load environment variables
    load_dotenv()
    
    # Get event ID from environment
    EVENT_ID = int(os.environ.get("EVENT_ID", "2"))
    
    # Get token
    print("Getting access token...")
    token = get_access_token()
    print("✓ Access token obtained\n")
    
    # Run the SignalR client
    asyncio.run(main_example(token, event_id=EVENT_ID))
