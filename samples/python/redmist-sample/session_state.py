"""
SessionState model for RedMist timing and scoring system.
Represents the overall state of a race session at any given time.
"""
from datetime import datetime, time
from typing import Optional, List, Dict
from enum import IntEnum
import msgpack


def _parse_datetime(value) -> Optional[datetime]:
    """Parse datetime from various formats (string, datetime, int timestamp)."""
    if value is None:
        return None
    if isinstance(value, datetime):
        return value
    if isinstance(value, str):
        return datetime.fromisoformat(value)
    if isinstance(value, (int, float)):
        # Unix timestamp
        return datetime.fromtimestamp(value)
    return None


def _parse_time(value) -> Optional[time]:
    """Parse time from various formats (string, time object)."""
    if value is None:
        return None
    if isinstance(value, time):
        return value
    if isinstance(value, str):
        return time.fromisoformat(value)
    return None


class Flags(IntEnum):
    """Flag states during a racing session."""
    UNKNOWN = 0
    GREEN = 1
    YELLOW = 2
    RED = 3
    WHITE = 4
    CHECKERED = 5
    BLACK = 6
    PURPLE35 = 7


class VideoSystemType(IntEnum):
    """
    Types of source video systems.
    """
    NONE = 0
    GENERIC = 1
    SENTINEL = 2
    MY_RACES_LIVE = 3
    AUTOSPORT_LABS = 4


class VideoDestinationType(IntEnum):
    """
    Where the video is available for viewing.
    """
    NONE = 0
    YOUTUBE = 1
    FACEBOOK = 2
    VIMEO = 3
    DIRECT_SRT = 4


class VideoDestination:
    """
    Location where a car's video is being sent or can be accessed.
    """
    
    def __init__(
        self,
        destination_type: VideoDestinationType = VideoDestinationType.NONE,
        url: str = "",
        host_name: str = "",
        port: int = 0,
        parameters: str = ""
    ):
        # Destination such as YouTube.
        self.type = destination_type  # Key 0
        # Destination's URL.
        self.url = url  # Key 1
        # Destination URL's host name or IP address.
        self.host_name = host_name  # Key 2
        # Destination's port.
        self.port = port  # Key 3
        # Gets or sets the parameters associated with the operation.
        self.parameters = parameters  # Key 4
    
    def to_msgpack(self) -> list:
        """Convert to MessagePack array format."""
        return [int(self.type), self.url, self.host_name, self.port, self.parameters]
    
    @classmethod
    def from_msgpack(cls, data: list) -> 'VideoDestination':
        """Create from MessagePack array format."""
        return cls(
            destination_type=VideoDestinationType(data[0]) if len(data) > 0 else VideoDestinationType.NONE,
            url=data[1] if len(data) > 1 else "",
            host_name=data[2] if len(data) > 2 else "",
            port=data[3] if len(data) > 3 else 0,
            parameters=data[4] if len(data) > 4 else ""
        )


class VideoStatus:
    """
    Status of a car's video system.
    """
    
    def __init__(
        self,
        video_system_type: VideoSystemType = VideoSystemType.NONE,
        video_destination: Optional[VideoDestination] = None
    ):
        # Brand/model of the car's video system.
        self.video_system_type = video_system_type  # Key 0
        # Gets or sets the destination configuration consuming video.
        self.video_destination = video_destination or VideoDestination()  # Key 1
    
    def to_msgpack(self) -> list:
        """Convert to MessagePack array format."""
        return [int(self.video_system_type), self.video_destination.to_msgpack()]
    
    @classmethod
    def from_msgpack(cls, data: list) -> 'VideoStatus':
        """Create from MessagePack array format."""
        return cls(
            video_system_type=VideoSystemType(data[0]) if len(data) > 0 else VideoSystemType.NONE,
            video_destination=VideoDestination.from_msgpack(data[1]) if len(data) > 1 and data[1] else VideoDestination()
        )


class CompletedSection:
    """
    Represents a completed section in the timing system for a given competitor.
    Multiloop support is required to utilize this feature.
    """
    
    def __init__(
        self,
        number: str = "",
        section_id: str = "",
        elapsed_time_ms: int = 0,
        last_section_time_ms: int = 0,
        last_lap: int = 0
    ):
        # Car number.
        # Max length is 4 if multiloop is in use
        self.number = number  # Key 0
        # Section ID from the timing system.
        self.section_id = section_id  # Key 1
        # Section time in milliseconds.
        self.elapsed_time_ms = elapsed_time_ms  # Key 2
        # Previous section time in milliseconds.
        self.last_section_time_ms = last_section_time_ms  # Key 3
        # Lap number for the last completed section.
        self.last_lap = last_lap  # Key 4
    
    def to_msgpack(self) -> list:
        """Convert to MessagePack array format."""
        return [self.number, self.section_id, self.elapsed_time_ms, self.last_section_time_ms, self.last_lap]
    
    @classmethod
    def from_msgpack(cls, data: list) -> 'CompletedSection':
        """Create from MessagePack array format."""
        return cls(
            number=data[0] if len(data) > 0 else "",
            section_id=data[1] if len(data) > 1 else "",
            elapsed_time_ms=data[2] if len(data) > 2 else 0,
            last_section_time_ms=data[3] if len(data) > 3 else 0,
            last_lap=data[4] if len(data) > 4 else 0
        )


class EventEntry:
    """
    Competitor entry information for an event.
    """
    
    def __init__(
        self,
        number: str = "",
        name: str = "",
        team: str = "",
        class_name: str = ""
    ):
        # Car's number which can include letters, such as 99x.
        self.number = number  # Key 0
        # Typically associated with First or Last name depending on configuration of the timing system.
        self.name = name  # Key 1
        # Typically the name of the team depending on configuration of the timing system.
        self.team = team  # Key 2
        # Car's class. This can be empty.
        self.class_name = class_name  # Key 3
    
    def to_msgpack(self) -> list:
        """Convert to MessagePack array format."""
        return [self.number, self.name, self.team, self.class_name]
    
    @classmethod
    def from_msgpack(cls, data: list) -> 'EventEntry':
        """Create from MessagePack array format."""
        return cls(
            number=data[0] if len(data) > 0 else "",
            name=data[1] if len(data) > 1 else "",
            team=data[2] if len(data) > 2 else "",
            class_name=data[3] if len(data) > 3 else ""
        )


class CarPosition:
    """
    Status data for a car.
    """
    
    # Used to indicate position information is not available.
    INVALID_POSITION = -999
    
    def __init__(
        self,
        event_id: Optional[str] = None,
        session_id: Optional[str] = None,
        number: Optional[str] = None,
        transponder_id: int = 0,
        car_class: Optional[str] = None,
        best_time: Optional[str] = None,
        best_lap: int = 0,
        is_best_time: bool = False,
        is_best_time_class: bool = False,
        in_class_gap: Optional[str] = None,
        in_class_difference: Optional[str] = None,
        overall_gap: Optional[str] = None,
        overall_difference: Optional[str] = None,
        total_time: Optional[str] = None,
        last_lap_time: Optional[str] = None,
        last_lap_completed: int = 0,
        pit_stop_count: Optional[int] = None,
        last_lap_pitted: Optional[int] = None,
        laps_led_overall: Optional[int] = None,
        laps_led_in_class: Optional[int] = None,
        overall_position: int = 0,
        class_position: int = 0,
        overall_starting_position: int = 0,
        overall_positions_gained: int = INVALID_POSITION,
        in_class_starting_position: int = 0,
        in_class_positions_gained: int = INVALID_POSITION,
        is_overall_most_positions_gained: bool = False,
        is_class_most_positions_gained: bool = False,
        penalty_laps: int = 0,
        penalty_warnings: int = 0,
        black_flags: int = 0,
        is_entered_pit: bool = False,
        is_pit_start_finish: bool = False,
        is_exited_pit: bool = False,
        is_in_pit: bool = False,
        lap_included_pit: bool = False,
        last_loop_name: str = "",
        is_stale: bool = False,
        track_flag: Flags = Flags.UNKNOWN,
        local_flag: Flags = Flags.UNKNOWN,
        lap_had_local_flag: Optional[bool] = None,
        completed_sections: Optional[List[CompletedSection]] = None,
        projected_lap_time_ms: int = 0,
        lap_start_time: Optional[time] = None,
        driver_name: str = "",
        driver_id: str = "",
        in_car_video: Optional[VideoStatus] = None,
        latitude: Optional[float] = None,
        longitude: Optional[float] = None,
        current_status: str = "",
        impact_warning: bool = False
    ):
        # Redmist Event ID.
        self.event_id = event_id  # Key 0
        # Current session ID as reported by the timing system.
        self.session_id = session_id  # Key 1
        # Car number which can include letters such as 99x.
        self.number = number  # Key 2
        # Car's main transponder ID as indicated by the timing system.
        self.transponder_id = transponder_id  # Key 3
        # Car's class as indicated by the timing system.
        self.car_class = car_class  # Key 4
        # Car's best lap time formatted as HH:mm:ss.fff.
        self.best_time = best_time  # Key 5
        # Car's best lap number.
        self.best_lap = best_lap  # Key 6
        # Whether the car set the best lap time of the session.
        self.is_best_time = is_best_time  # Key 7
        # Whether the car set the best lap time in its class.
        self.is_best_time_class = is_best_time_class  # Key 8
        # Time to the next car in the same class formatted as HH:mm:ss.fff.
        self.in_class_gap = in_class_gap  # Key 9
        # Time to the in-class leader formatted as HH:mm:ss.fff.
        self.in_class_difference = in_class_difference  # Key 10
        # Time to the next car overall formatted as HH:mm:ss.fff.
        self.overall_gap = overall_gap  # Key 11
        # Time to the overall leader formatted as HH:mm:ss.fff.
        self.overall_difference = overall_difference  # Key 12
        # Total race time formatted as HH:mm:ss.fff.
        self.total_time = total_time  # Key 13
        # Car's last lap time formatted as HH:mm:ss.fff.
        self.last_lap_time = last_lap_time  # Key 14
        # Last lap number.
        self.last_lap_completed = last_lap_completed  # Key 15
        # Number of times the car pitted. Null if not supported by the timing system.
        self.pit_stop_count = pit_stop_count  # Key 16
        # Last lap number the car pitted. Null if not supported by the timing system.
        self.last_lap_pitted = last_lap_pitted  # Key 17
        # Laps completed by the car. Null if not supported by the timing system.
        self.laps_led_overall = laps_led_overall  # Key 18
        # Laps completed by the car in class. Null if not supported by the timing system.
        self.laps_led_in_class = laps_led_in_class  # Key 19
        # Car's overall position in the race by laps and as reported by the timing system.
        self.overall_position = overall_position  # Key 20
        # Car's position in-class.
        self.class_position = class_position  # Key 21
        # Car's starting overall position inferred by the order the cars pass S/F at the start of the race or by the multiloop timing system if available.
        self.overall_starting_position = overall_starting_position  # Key 22
        # Number of position the car has gained overall. Negative number means positions lost.
        # Value of -999 means not available.
        self.overall_positions_gained = overall_positions_gained  # Key 23
        # Car's starting position in-class inferred by the order the cars pass S/F at the start of the race or by the multiloop timing system if available.
        self.in_class_starting_position = in_class_starting_position  # Key 24
        # Number of position the car has gained in-class. Negative number means positions lost.
        # Value of -999 means not available.
        self.in_class_positions_gained = in_class_positions_gained  # Key 25
        # This car has gained the most positions overall.
        self.is_overall_most_positions_gained = is_overall_most_positions_gained  # Key 26
        # This car has gained the most positions in-class.
        self.is_class_most_positions_gained = is_class_most_positions_gained  # Key 27
        # Laps lost due to penalties.
        self.penalty_laps = penalty_laps  # Key 28
        # Number of warnings issued.
        self.penalty_warnings = penalty_warnings  # Key 29
        # Number of black flags applied to the car.
        self.black_flags = black_flags  # Key 30
        # Car just passed the pit lane entry line.
        self.is_entered_pit = is_entered_pit  # Key 31
        # Car is just passed the pit lane start/finish line.
        self.is_pit_start_finish = is_pit_start_finish  # Key 32
        # Car just passed the pit lane exit line.
        self.is_exited_pit = is_exited_pit  # Key 33
        # Car is currently in pits.
        self.is_in_pit = is_in_pit  # Key 34
        # Current lap includes a pit stop.
        self.lap_included_pit = lap_included_pit  # Key 35
        # Name of the last timing loop passed.
        self.last_loop_name = last_loop_name  # Key 36
        # Car position is stale and has not been updated for a while.
        self.is_stale = is_stale  # Key 37
        # Flag active for the overall track.
        self.track_flag = track_flag  # Key 38
        # Current local flag for the car. Requires specific in-car equipment.
        self.local_flag = local_flag  # Key 39
        # This lap had a local flag for the car. Requires specific in-car equipment.
        self.lap_had_local_flag = lap_had_local_flag  # Key 40
        # Car's completed section for the current lap.
        self.completed_sections = completed_sections or []  # Key 41
        # Estimated lap time for the car.
        self.projected_lap_time_ms = projected_lap_time_ms  # Key 42
        # Time at which the current lap started in UTC.
        self.lap_start_time = lap_start_time  # Key 43
        # Current name of the driver.
        self.driver_name = driver_name  # Key 44
        # Current ID of the driver.
        self.driver_id = driver_id  # Key 45
        # In-car video details if available.
        self.in_car_video = in_car_video  # Key 46
        # Last reported latitude of the car in WGS84 spherical Mercator.
        self.latitude = latitude  # Key 47
        # Last reported longitude of the car in WGS84 spherical Mercator.
        self.longitude = longitude  # Key 48
        # Active, In Pits,DNS, Contact, Mechanical, etc. Only available with multiloop systems.
        self.current_status = current_status  # Key 49
        # Car may have been involved in an incident. Only available with certain in-car systems.
        self.impact_warning = impact_warning  # Key 50
    
    def to_msgpack(self) -> list:
        """Convert to MessagePack array format."""
        return [
            self.event_id,  # 0
            self.session_id,  # 1
            self.number,  # 2
            self.transponder_id,  # 3
            self.car_class,  # 4
            self.best_time,  # 5
            self.best_lap,  # 6
            self.is_best_time,  # 7
            self.is_best_time_class,  # 8
            self.in_class_gap,  # 9
            self.in_class_difference,  # 10
            self.overall_gap,  # 11
            self.overall_difference,  # 12
            self.total_time,  # 13
            self.last_lap_time,  # 14
            self.last_lap_completed,  # 15
            self.pit_stop_count,  # 16
            self.last_lap_pitted,  # 17
            self.laps_led_overall,  # 18
            self.laps_led_in_class,  # 19
            self.overall_position,  # 20
            self.class_position,  # 21
            self.overall_starting_position,  # 22
            self.overall_positions_gained,  # 23
            self.in_class_starting_position,  # 24
            self.in_class_positions_gained,  # 25
            self.is_overall_most_positions_gained,  # 26
            self.is_class_most_positions_gained,  # 27
            self.penalty_laps,  # 28
            self.penalty_warnings,  # 29
            self.black_flags,  # 30
            self.is_entered_pit,  # 31
            self.is_pit_start_finish,  # 32
            self.is_exited_pit,  # 33
            self.is_in_pit,  # 34
            self.lap_included_pit,  # 35
            self.last_loop_name,  # 36
            self.is_stale,  # 37
            int(self.track_flag),  # 38
            int(self.local_flag),  # 39
            self.lap_had_local_flag,  # 40
            [cs.to_msgpack() for cs in self.completed_sections],  # 41
            self.projected_lap_time_ms,  # 42
            self.lap_start_time.isoformat() if self.lap_start_time else None,  # 43
            self.driver_name,  # 44
            self.driver_id,  # 45
            self.in_car_video.to_msgpack() if self.in_car_video else None,  # 46
            self.latitude,  # 47
            self.longitude,  # 48
            self.current_status,  # 49
            self.impact_warning,  # 50
        ]
    
    @classmethod
    def from_msgpack(cls, data: list) -> 'CarPosition':
        """Create from MessagePack array format."""
        return cls(
            event_id=data[0] if len(data) > 0 else None,
            session_id=data[1] if len(data) > 1 else None,
            number=data[2] if len(data) > 2 else None,
            transponder_id=data[3] if len(data) > 3 else 0,
            car_class=data[4] if len(data) > 4 else None,
            best_time=data[5] if len(data) > 5 else None,
            best_lap=data[6] if len(data) > 6 else 0,
            is_best_time=data[7] if len(data) > 7 else False,
            is_best_time_class=data[8] if len(data) > 8 else False,
            in_class_gap=data[9] if len(data) > 9 else None,
            in_class_difference=data[10] if len(data) > 10 else None,
            overall_gap=data[11] if len(data) > 11 else None,
            overall_difference=data[12] if len(data) > 12 else None,
            total_time=data[13] if len(data) > 13 else None,
            last_lap_time=data[14] if len(data) > 14 else None,
            last_lap_completed=data[15] if len(data) > 15 else 0,
            pit_stop_count=data[16] if len(data) > 16 else None,
            last_lap_pitted=data[17] if len(data) > 17 else None,
            laps_led_overall=data[18] if len(data) > 18 else None,
            laps_led_in_class=data[19] if len(data) > 19 else None,
            overall_position=data[20] if len(data) > 20 else 0,
            class_position=data[21] if len(data) > 21 else 0,
            overall_starting_position=data[22] if len(data) > 22 else 0,
            overall_positions_gained=data[23] if len(data) > 23 else cls.INVALID_POSITION,
            in_class_starting_position=data[24] if len(data) > 24 else 0,
            in_class_positions_gained=data[25] if len(data) > 25 else cls.INVALID_POSITION,
            is_overall_most_positions_gained=data[26] if len(data) > 26 else False,
            is_class_most_positions_gained=data[27] if len(data) > 27 else False,
            penalty_laps=data[28] if len(data) > 28 else 0,
            penalty_warnings=data[29] if len(data) > 29 else 0,
            black_flags=data[30] if len(data) > 30 else 0,
            is_entered_pit=data[31] if len(data) > 31 else False,
            is_pit_start_finish=data[32] if len(data) > 32 else False,
            is_exited_pit=data[33] if len(data) > 33 else False,
            is_in_pit=data[34] if len(data) > 34 else False,
            lap_included_pit=data[35] if len(data) > 35 else False,
            last_loop_name=data[36] if len(data) > 36 else "",
            is_stale=data[37] if len(data) > 37 else False,
            track_flag=Flags(data[38]) if len(data) > 38 else Flags.UNKNOWN,
            local_flag=Flags(data[39]) if len(data) > 39 else Flags.UNKNOWN,
            lap_had_local_flag=data[40] if len(data) > 40 else None,
            completed_sections=[CompletedSection.from_msgpack(cs) for cs in data[41]] if len(data) > 41 and data[41] else [],
            projected_lap_time_ms=data[42] if len(data) > 42 else 0,
            lap_start_time=_parse_time(data[43]) if len(data) > 43 else None,
            driver_name=data[44] if len(data) > 44 else "",
            driver_id=data[45] if len(data) > 45 else "",
            in_car_video=VideoStatus.from_msgpack(data[46]) if len(data) > 46 and data[46] else None,
            latitude=data[47] if len(data) > 47 else None,
            longitude=data[48] if len(data) > 48 else None,
            current_status=data[49] if len(data) > 49 else "",
            impact_warning=data[50] if len(data) > 50 else False
        )


class FlagDuration:
    """
    Instance of a flag state during a session.
    """
    
    def __init__(
        self,
        flag: Flags = Flags.UNKNOWN,
        start_time: Optional[datetime] = None,
        end_time: Optional[datetime] = None
    ):
        # The flag that is or was active.
        self.flag = flag  # Key 0
        # When the flag state started.
        self.start_time = start_time  # Key 1
        # When the flag state ended, or null if it is still active.
        self.end_time = end_time  # Key 2
    
    def to_msgpack(self) -> list:
        """Convert to MessagePack array format."""
        return [
            int(self.flag),
            self.start_time.isoformat() if self.start_time else None,
            self.end_time.isoformat() if self.end_time else None
        ]
    
    @classmethod
    def from_msgpack(cls, data: list) -> 'FlagDuration':
        """Create from MessagePack array format."""
        return cls(
            flag=Flags(data[0]) if len(data) > 0 else Flags.UNKNOWN,
            start_time=_parse_datetime(data[1]) if len(data) > 1 else None,
            end_time=_parse_datetime(data[2]) if len(data) > 2 else None
        )


class Section:
    """
    Track segment or section information, e.g., for a pit lane or track sector.
    Multiloop support is required to utilize this feature.
    """
    
    def __init__(
        self,
        name: str = "",
        length_inches: int = 0,
        start_label: str = "",
        end_label: str = ""
    ):
        # Section name from the timing system.
        self.name = name  # Key 0
        # Section length in inches from the timing system.
        self.length_inches = length_inches  # Key 1
        # Name of the section start point from the timing system.
        self.start_label = start_label  # Key 2
        # Name of the end of the section from the timing system
        self.end_label = end_label  # Key 3
    
    def to_msgpack(self) -> list:
        """Convert to MessagePack array format."""
        return [self.name, self.length_inches, self.start_label, self.end_label]
    
    @classmethod
    def from_msgpack(cls, data: list) -> 'Section':
        """Create from MessagePack array format."""
        return cls(
            name=data[0] if len(data) > 0 else "",
            length_inches=data[1] if len(data) > 1 else 0,
            start_label=data[2] if len(data) > 2 else "",
            end_label=data[3] if len(data) > 3 else ""
        )


class Announcement:
    """
    Message to convey to team, drivers, spectators, etc.
    """
    
    def __init__(
        self,
        timestamp: Optional[datetime] = None,
        priority: str = "",
        text: str = ""
    ):
        # Time at which the announcement was made.
        self.timestamp = timestamp  # Key 0
        # Announcement priority ("Urgent", "High", "Normal", "Low").
        self.priority = priority  # Key 1
        # The message.
        self.text = text  # Key 2
    
    def to_msgpack(self) -> list:
        """Convert to MessagePack array format."""
        return [
            self.timestamp.isoformat() if self.timestamp else None,
            self.priority,
            self.text
        ]
    
    @classmethod
    def from_msgpack(cls, data: list) -> 'Announcement':
        """Create from MessagePack array format."""
        return cls(
            timestamp=_parse_datetime(data[0]) if len(data) > 0 else None,
            priority=data[1] if len(data) > 1 else "",
            text=data[2] if len(data) > 2 else ""
        )


class SessionState:
    """
    The overall state of a race session at any given time.
    This includes data needed to fully represent the race.
    """
    
    def __init__(
        self,
        event_id: int = 0,
        event_name: str = "",
        session_id: int = 0,
        session_name: str = "",
        laps_to_go: int = 0,
        time_to_go: str = "",
        local_time_of_day: str = "",
        running_race_time: str = "",
        is_practice_qualifying: bool = False,
        session_start_time: Optional[datetime] = None,
        session_end_time: Optional[datetime] = None,
        local_time_zone_offset: float = 0.0,
        is_live: bool = False,
        event_entries: Optional[List[EventEntry]] = None,
        car_positions: Optional[List[CarPosition]] = None,
        current_flag: Flags = Flags.UNKNOWN,
        flag_durations: Optional[List[FlagDuration]] = None,
        green_time_ms: Optional[int] = None,
        green_laps: Optional[int] = None,
        yellow_time_ms: Optional[int] = None,
        yellow_laps: Optional[int] = None,
        number_of_yellows: Optional[int] = None,
        red_time_ms: Optional[int] = None,
        average_race_speed: Optional[str] = None,
        lead_changes: Optional[int] = None,
        sections: Optional[List[Section]] = None,
        class_colors: Optional[Dict[str, str]] = None,
        announcements: Optional[List[Announcement]] = None,
        last_updated: Optional[datetime] = None
    ):
        # Redmist ID for the event.
        self.event_id = event_id  # Key 0
        # Event name as indicated by the organizer.
        self.event_name = event_name  # Key 1
        # Session, or run, is the current part of the event being timed such as in individual race, practice, or qualifying session.
        # This is the ID indicated by the timing system.
        self.session_id = session_id  # Key 2
        # Session name as indicated by the timing system.
        self.session_name = session_name  # Key 3
        # Optional number of laps to go if the race is lap based.
        self.laps_to_go = laps_to_go  # Key 4
        # Optional time of the session remaining if the event is time based.
        # Format is HH:mm:ss.
        # Orbits has been seen to have a negative seconds value preceding the start of a race, i.e. HH:mm:-ss
        self.time_to_go = time_to_go  # Key 5
        # Gets or sets the local time of day as a string. This is 24 hour format HH:mm:ss.
        self.local_time_of_day = local_time_of_day  # Key 6
        # Gets or sets the amount of time a race has been running. Format is HH:mm:ss.
        self.running_race_time = running_race_time  # Key 7
        # Whether the current session is a practice qualifying session. This is is not guaranteed
        # to be accurate and comes from the session name.
        self.is_practice_qualifying = is_practice_qualifying  # Key 8
        # When the session started according to the the first time data was received from the timing system for the session.
        self.session_start_time = session_start_time  # Key 9
        # When the session ended according to the timing system change to another session or timeout of data being received, such as at the end of the race.
        self.session_end_time = session_end_time  # Key 10
        # The event's local time zone offset from UTC in hours as indicated by the organizer's system time.
        self.local_time_zone_offset = local_time_zone_offset  # Key 11
        # Indicates whether the organizer is connected and sending data for the event.
        self.is_live = is_live  # Key 12
        # These are the signed up entries indicated by the timing system.
        # They may not be the same as the cars that actually participated in the event.
        self.event_entries = event_entries or []  # Key 13
        # Represents the current position and status of each car in the event.
        self.car_positions = car_positions or []  # Key 14
        # Current flag state for the event.
        self.current_flag = current_flag  # Key 15
        # Flag states for the event, including durations.
        self.flag_durations = flag_durations or []  # Key 16
        # Amount of time in milliseconds the session has been under green. Available with Multiloop timing systems.
        self.green_time_ms = green_time_ms  # Key 17
        # Number of laps the session has been under green. Available with Multiloop timing systems.
        self.green_laps = green_laps  # Key 18
        # Amount of time in milliseconds the session has been under yellow. Available with Multiloop timing systems.
        self.yellow_time_ms = yellow_time_ms  # Key 19
        # Number of laps the session has been under yellow. Available with Multiloop timing systems.
        self.yellow_laps = yellow_laps  # Key 20
        # Number of yellow flag periods in the session. Available with Multiloop timing systems.
        self.number_of_yellows = number_of_yellows  # Key 21
        # Amount of time in milliseconds the session has been under red. Available with Multiloop timing systems.
        self.red_time_ms = red_time_ms  # Key 22
        # Gets or sets the average speed of the race, expressed as a string, e.g. 130.456 mph.
        self.average_race_speed = average_race_speed  # Key 23
        # Count of overall lead changes in the session. Available with Multiloop timing systems.
        self.lead_changes = lead_changes  # Key 24
        # Track sections as indicated by the timing system.
        self.sections = sections or []  # Key 25
        # Class colors in hexadecimal format #RRGGBB (e.g., "#FF0000" for red).
        # Each color corresponds to a racing class for visual identification.
        self.class_colors = class_colors or {}  # Key 26
        # Session announcements as indicated by the timing system.
        self.announcements = announcements or []  # Key 27
        # Last time the session state was updated.
        self.last_updated = last_updated  # Key 28
    
    def to_msgpack_dict(self) -> dict:
        """
        Convert to MessagePack dictionary format with integer keys.
        This matches the C# MessagePackObject format.
        """
        return {
            0: self.event_id,
            1: self.event_name,
            2: self.session_id,
            3: self.session_name,
            4: self.laps_to_go,
            5: self.time_to_go,
            6: self.local_time_of_day,
            7: self.running_race_time,
            8: self.is_practice_qualifying,
            9: self.session_start_time.isoformat() if self.session_start_time else None,
            10: self.session_end_time.isoformat() if self.session_end_time else None,
            11: self.local_time_zone_offset,
            12: self.is_live,
            13: [entry.to_msgpack() for entry in self.event_entries],
            14: [pos.to_msgpack() for pos in self.car_positions],
            15: int(self.current_flag),
            16: [fd.to_msgpack() for fd in self.flag_durations],
            17: self.green_time_ms,
            18: self.green_laps,
            19: self.yellow_time_ms,
            20: self.yellow_laps,
            21: self.number_of_yellows,
            22: self.red_time_ms,
            23: self.average_race_speed,
            24: self.lead_changes,
            25: [section.to_msgpack() for section in self.sections],
            26: self.class_colors,
            27: [ann.to_msgpack() for ann in self.announcements],
            28: self.last_updated.isoformat() if self.last_updated else None,
        }
    
    def pack(self) -> bytes:
        """Serialize to MessagePack binary format."""
        return msgpack.packb(self.to_msgpack_dict(), use_bin_type=True, strict_types=False)
    
    @classmethod
    def from_msgpack_dict(cls, data: dict) -> 'SessionState':
        """
        Create SessionState from MessagePack dictionary with integer keys.
        This matches the C# MessagePackObject format.
        """
        return cls(
            event_id=data.get(0, 0),
            event_name=data.get(1, ""),
            session_id=data.get(2, 0),
            session_name=data.get(3, ""),
            laps_to_go=data.get(4, 0),
            time_to_go=data.get(5, ""),
            local_time_of_day=data.get(6, ""),
            running_race_time=data.get(7, ""),
            is_practice_qualifying=data.get(8, False),
            session_start_time=_parse_datetime(data.get(9)),
            session_end_time=_parse_datetime(data.get(10)),
            local_time_zone_offset=data.get(11, 0.0),
            is_live=data.get(12, False),
            event_entries=[EventEntry.from_msgpack(e) for e in data.get(13, [])],
            car_positions=[CarPosition.from_msgpack(p) for p in data.get(14, [])],
            current_flag=Flags(data.get(15, 0)),
            flag_durations=[FlagDuration.from_msgpack(fd) for fd in data.get(16, [])],
            green_time_ms=data.get(17),
            green_laps=data.get(18),
            yellow_time_ms=data.get(19),
            yellow_laps=data.get(20),
            number_of_yellows=data.get(21),
            red_time_ms=data.get(22),
            average_race_speed=data.get(23),
            lead_changes=data.get(24),
            sections=[Section.from_msgpack(s) for s in data.get(25, [])],
            class_colors=data.get(26, {}),
            announcements=[Announcement.from_msgpack(a) for a in data.get(27, [])],
            last_updated=_parse_datetime(data.get(28)),
        )
    
    @classmethod
    def unpack(cls, packed_data: bytes) -> 'SessionState':
        """Deserialize from MessagePack binary format."""
        data = msgpack.unpackb(packed_data, raw=False, strict_map_key=False)
        return cls.from_msgpack_dict(data)
    
    def __repr__(self) -> str:
        return (
            f"SessionState(event_id={self.event_id}, event_name='{self.event_name}', "
            f"session_id={self.session_id}, session_name='{self.session_name}', "
            f"is_live={self.is_live}, current_flag={self.current_flag.name})"
        )
