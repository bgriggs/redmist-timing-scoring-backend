# Complete Python MessagePack Models - Class Reference

## Overview
This document provides a complete reference for all Python classes implementing the RedMist timing and scoring MessagePack models.

## Class Hierarchy

```
session_state.py (750 lines)
├── Enumerations
│   ├── Flags (8 values)
│   ├── VideoSystemType (5 values)
│   └── VideoDestinationType (5 values)
│
├── Video Classes
│   ├── VideoDestination (5 properties)
│   └── VideoStatus (2 properties)
│
├── Timing Classes
│   ├── CompletedSection (5 properties)
│   ├── Section (4 properties)
│   └── FlagDuration (3 properties)
│
├── Entry Classes
│   ├── EventEntry (4 properties)
│   └── Announcement (3 properties)
│
├── Position Classes
│   └── CarPosition (51 properties) ⭐ FULL IMPLEMENTATION
│
└── Main Class
    └── SessionState (29 properties)
```

## Detailed Class Breakdown

### 1. Enumerations

#### Flags
```python
class Flags(IntEnum):
    UNKNOWN = 0
    GREEN = 1
    YELLOW = 2
    RED = 3
    WHITE = 4
    CHECKERED = 5
    BLACK = 6
    PURPLE35 = 7
```

#### VideoSystemType
```python
class VideoSystemType(IntEnum):
    NONE = 0
    GENERIC = 1
    SENTINEL = 2
    MY_RACES_LIVE = 3
    AUTOSPORT_LABS = 4
```

#### VideoDestinationType
```python
class VideoDestinationType(IntEnum):
    NONE = 0
    YOUTUBE = 1
    FACEBOOK = 2
    VIMEO = 3
    DIRECT_SRT = 4
```

### 2. Video Classes

#### VideoDestination
Location where a car's video is being sent or can be accessed.
- `type` (VideoDestinationType) - Destination such as YouTube
- `url` (str) - Destination's URL
- `host_name` (str) - Destination URL's host name or IP address
- `port` (int) - Destination's port
- `parameters` (str) - Parameters associated with the operation

#### VideoStatus
Status of a car's video system.
- `video_system_type` (VideoSystemType) - Brand/model of the car's video system
- `video_destination` (VideoDestination) - Destination configuration consuming video

### 3. Timing Classes

#### CompletedSection
Represents a completed section in the timing system for a given competitor.
- `number` (str) - Car number
- `section_id` (str) - Section ID from the timing system
- `elapsed_time_ms` (int) - Section time in milliseconds
- `last_section_time_ms` (int) - Previous section time in milliseconds
- `last_lap` (int) - Lap number for the last completed section

#### Section
Track segment or section information.
- `name` (str) - Section name from the timing system
- `length_inches` (int) - Section length in inches from the timing system
- `start_label` (str) - Name of the section start point
- `end_label` (str) - Name of the end of the section

#### FlagDuration
Instance of a flag state during a session.
- `flag` (Flags) - The flag that is or was active
- `start_time` (datetime) - When the flag state started
- `end_time` (datetime) - When the flag state ended, or null if still active

### 4. Entry Classes

#### EventEntry
Competitor entry information for an event.
- `number` (str) - Car's number which can include letters
- `name` (str) - First or Last name depending on timing system configuration
- `team` (str) - Name of the team
- `class_name` (str) - Car's class

#### Announcement
Message to convey to team, drivers, spectators, etc.
- `timestamp` (datetime) - Time at which the announcement was made
- `priority` (str) - Announcement priority ("Urgent", "High", "Normal", "Low")
- `text` (str) - The message

### 5. CarPosition (51 Properties - Complete Implementation)

#### Identity (5 properties)
- `event_id` (str) - Redmist Event ID
- `session_id` (str) - Current session ID
- `number` (str) - Car number
- `transponder_id` (int) - Car's main transponder ID
- `car_class` (str) - Car's class

#### Timing (7 properties)
- `best_time` (str) - Best lap time (HH:mm:ss.fff)
- `best_lap` (int) - Best lap number
- `last_lap_time` (str) - Last lap time (HH:mm:ss.fff)
- `last_lap_completed` (int) - Last lap number
- `total_time` (str) - Total race time (HH:mm:ss.fff)
- `projected_lap_time_ms` (int) - Estimated lap time
- `lap_start_time` (time) - When current lap started (UTC)

#### Position & Gaps (8 properties)
- `overall_position` (int) - Overall position
- `class_position` (int) - Position in class
- `overall_gap` (str) - Time to next car overall
- `overall_difference` (str) - Time to overall leader
- `in_class_gap` (str) - Time to next car in class
- `in_class_difference` (str) - Time to class leader
- `overall_starting_position` (int) - Starting position overall
- `in_class_starting_position` (int) - Starting position in class

#### Position Changes (4 properties)
- `overall_positions_gained` (int) - Positions gained overall (+/-999)
- `in_class_positions_gained` (int) - Positions gained in class (+/-999)
- `is_overall_most_positions_gained` (bool) - Most positions gained overall
- `is_class_most_positions_gained` (bool) - Most positions gained in class

#### Achievements (2 properties)
- `is_best_time` (bool) - Set best lap time of session
- `is_best_time_class` (bool) - Set best lap time in class

#### Laps Led (2 properties)
- `laps_led_overall` (int) - Laps led overall
- `laps_led_in_class` (int) - Laps led in class

#### Pit Information (7 properties)
- `pit_stop_count` (int) - Number of pit stops
- `last_lap_pitted` (int) - Last lap number car pitted
- `is_in_pit` (bool) - Currently in pits
- `is_entered_pit` (bool) - Just passed pit lane entry
- `is_exited_pit` (bool) - Just passed pit lane exit
- `is_pit_start_finish` (bool) - Just passed pit lane S/F
- `lap_included_pit` (bool) - Current lap includes pit stop

#### Penalties (3 properties)
- `penalty_laps` (int) - Laps lost due to penalties
- `penalty_warnings` (int) - Number of warnings issued
- `black_flags` (int) - Number of black flags

#### Flags (3 properties)
- `track_flag` (Flags) - Flag active for overall track
- `local_flag` (Flags) - Current local flag for the car
- `lap_had_local_flag` (bool) - This lap had a local flag

#### Driver (2 properties)
- `driver_name` (str) - Current driver name
- `driver_id` (str) - Current driver ID

#### Location (3 properties)
- `latitude` (float) - Last reported latitude (WGS84)
- `longitude` (float) - Last reported longitude (WGS84)
- `last_loop_name` (str) - Name of last timing loop passed

#### Sections & Video (2 properties)
- `completed_sections` (List[CompletedSection]) - Completed sections for current lap
- `in_car_video` (VideoStatus) - In-car video details

#### Status (3 properties)
- `current_status` (str) - Active, In Pits, DNS, Contact, Mechanical, etc.
- `is_stale` (bool) - Position not updated for a while
- `impact_warning` (bool) - May have been involved in incident

### 6. SessionState (29 Properties)

#### Event Information (4 properties)
- `event_id` (int) - Redmist ID for the event
- `event_name` (str) - Event name
- `session_id` (int) - Current session ID
- `session_name` (str) - Session name

#### Timing (4 properties)
- `laps_to_go` (int) - Number of laps remaining
- `time_to_go` (str) - Time remaining (HH:mm:ss)
- `local_time_of_day` (str) - Local time (HH:mm:ss)
- `running_race_time` (str) - Race running time (HH:mm:ss)

#### Session Status (5 properties)
- `is_practice_qualifying` (bool) - Is practice/qualifying session
- `session_start_time` (datetime) - When session started
- `session_end_time` (datetime) - When session ended
- `local_time_zone_offset` (float) - Local timezone offset from UTC (hours)
- `is_live` (bool) - Organizer connected and sending data

#### Entries & Positions (2 properties)
- `event_entries` (List[EventEntry]) - Signed up entries
- `car_positions` (List[CarPosition]) - Current position and status of each car

#### Flag Information (2 properties)
- `current_flag` (Flags) - Current flag state
- `flag_durations` (List[FlagDuration]) - Flag states with durations

#### Multiloop Statistics (8 properties)
- `green_time_ms` (int) - Time under green (ms)
- `green_laps` (int) - Laps under green
- `yellow_time_ms` (int) - Time under yellow (ms)
- `yellow_laps` (int) - Laps under yellow
- `number_of_yellows` (int) - Yellow flag periods
- `red_time_ms` (int) - Time under red (ms)
- `average_race_speed` (str) - Average race speed
- `lead_changes` (int) - Overall lead changes

#### Track & Announcements (4 properties)
- `sections` (List[Section]) - Track sections
- `class_colors` (Dict[str, str]) - Class colors (#RRGGBB)
- `announcements` (List[Announcement]) - Session announcements
- `last_updated` (datetime) - Last update time

## MessagePack Methods

Every class includes:
- `to_msgpack()` - Converts object to MessagePack array format
- `from_msgpack(data)` - Creates object from MessagePack array

Top-level classes (SessionState) also include:
- `to_msgpack_dict()` - Converts to MessagePack dict with integer keys
- `from_msgpack_dict(data)` - Creates from MessagePack dict
- `pack()` - Serializes to binary MessagePack
- `unpack(data)` - Deserializes from binary MessagePack

## Constants

- `CarPosition.INVALID_POSITION = -999` - Indicates position info not available

## Examples

### Basic Usage
```python
from session_state import SessionState, CarPosition, Flags

# Create a session
session = SessionState(
    event_id=1,
    event_name="My Race",
    is_live=True,
    current_flag=Flags.GREEN
)

# Serialize
packed = session.pack()

# Deserialize
unpacked = SessionState.unpack(packed)
```

### Full CarPosition
```python
car = CarPosition(
    number="42",
    driver_name="John Doe",
    overall_position=3,
    best_time="00:01:23.456",
    pit_stop_count=2,
    latitude=28.4747,
    longitude=-81.3473
)
```

See `example_session_state.py` and `example_advanced_car_position.py` for complete examples.

## File Statistics

- **Total Lines**: 750
- **Total Classes**: 13 (3 enums, 10 data classes)
- **Total Properties**: ~120 across all classes
- **MessagePack Keys**: 0-50 (CarPosition), 0-28 (SessionState)
- **Full C# Parity**: ✅ Yes (all 345 lines of CarPosition implemented)
- **XML Comments**: ✅ Preserved as Python docstrings
