"""
Test script to demonstrate getting SessionState from the API.
"""
import os
from dotenv import load_dotenv
from main import get_access_token, get_current_session_state

# Load environment variables
load_dotenv()

# Get event ID from environment
EVENT_ID = int(os.environ.get("EVENT_ID", "2"))

def main():
    """Test getting session state for event ID 2."""
    try:
        print("Getting access token...")
        token = get_access_token()
        print("✓ Access token obtained")
        
        # Get session state for event ID 2
        session_state = get_current_session_state(token, event_id=EVENT_ID)
        
        if session_state:
            print("\n" + "="*60)
            print("SESSION STATE DETAILS")
            print("="*60)
            
            # Additional details if available
            if session_state.session_start_time:
                print(f"  Session Started: {session_state.session_start_time}")
            
            if session_state.running_race_time:
                print(f"  Running Time: {session_state.running_race_time}")
            
            if session_state.local_time_of_day:
                print(f"  Local Time: {session_state.local_time_of_day}")
            
            if session_state.class_colors:
                print(f"\n  Class Colors:")
                for class_name, color in session_state.class_colors.items():
                    print(f"    {class_name}: {color}")
            
            # Detailed car information
            if session_state.car_positions:
                print(f"\n  Detailed Car Positions:")
                for car in sorted(session_state.car_positions, key=lambda c: c.overall_position)[:10]:
                    print(f"\n    Position {car.overall_position}: #{car.number}")
                    print(f"      Driver: {car.driver_name}")
                    print(f"      Class: {car.car_class} (P{car.class_position} in class)")
                    
                    if car.best_time:
                        print(f"      Best Lap: {car.best_time} (Lap {car.best_lap})")
                        if car.is_best_time:
                            print(f"      ⭐ OVERALL BEST LAP")
                        elif car.is_best_time_class:
                            print(f"      ⭐ CLASS BEST LAP")
                    
                    if car.last_lap_time:
                        print(f"      Last Lap: {car.last_lap_time} (Lap {car.last_lap_completed})")
                    
                    if car.overall_difference:
                        print(f"      Gap to Leader: {car.overall_difference}")
                    
                    if car.pit_stop_count is not None and car.pit_stop_count > 0:
                        print(f"      Pit Stops: {car.pit_stop_count}")
                    
                    if car.is_in_pit:
                        print(f"      🔧 IN PIT")
                    
                    if car.overall_positions_gained != -999:
                        if car.overall_positions_gained > 0:
                            print(f"      📈 Gained {car.overall_positions_gained} positions")
                        elif car.overall_positions_gained < 0:
                            print(f"      📉 Lost {abs(car.overall_positions_gained)} positions")
            
            print("\n" + "="*60)
            print("✅ Session state successfully retrieved and deserialized!")
            print("="*60)
        else:
            print("\n❌ Failed to retrieve session state")
            
    except Exception as e:
        print(f"\n❌ Error: {e}")
        import traceback
        traceback.print_exc()


if __name__ == "__main__":
    main()
