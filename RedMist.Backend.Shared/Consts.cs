namespace RedMist.Backend.Shared;

public class Consts
{
    public const string STATUS_CHANNEL_PREFIX = "event-status";
    public const string EVENT_STATUS_STREAM_KEY = "evt-st-{0}";
    public const string SEND_FULL_STATUS = "fullstatus";
    public const string SEND_COMPETITOR_METADATA = "competitor-metadata";
    public const string RELAY_EVENT_CONNECTIONS = "relay-evt-conns";
    public const string STATUS_EVENT_CONNECTIONS = "st-evt-{0}-conns";
    public const string STATUS_CONNECTIONS = "st-conns";
    public const string RELAY_HEARTBEAT = "relay-hb-evt-{0}";
    public const string RELAY_CONNECTION = "relay-connid-{0}";
    public const string CONTROL_LOG = "control-log-evt-{0}";
    public const string CONTROL_LOG_CAR_PENALTIES = "control-log-penalties-evt-{0}";
    public const string CONTROL_LOG_CAR = "control-log-evt-{0}-car-{1}";
    public const string EVENT_RMON_STREAM_FIELD = "rmonitor-{0}-{1}";
    public const string EVENT_MULTILOOP_STREAM_FIELD = "multiloop-{0}-{1}";
    public const string EVENT_X2_LOOPS_STREAM_FIELD = "x2loop-{0}-999999";
    public const string EVENT_X2_PASSINGS_STREAM_FIELD = "x2pass-{0}-{1}";
    public const string EVENT_FLAGS_STREAM_FIELD = "flags-{0}-{1}";
    public const string EVENT_COMPETITORS = "competitors-{0}-999999";
    public const string RELAY_GROUP_PREFIX = "relay-event-{0}";
    public const string CLIENT_ID = "ts-client-{0}";
    public static readonly string[] PRACTICE_QUAL_TERMS = ["Practice", "Qualifying", "Qual"];
    public const string COMPETITOR_METADATA = "cm-{0}-evt-{1}";
    public const string EVENT_PAYLOAD = "evt-{0}-payload";
    public const string IN_CAR_EVENT_SUB = "in-car-evt-{0}-car-{1}";
    public const string IN_CAR_DATA = "in-car-data-{0}-{1}";
    public const string EVENT_CONFIGURATION_CHANGED = "event-configuration-changed";

    #region Metrics
    public const string CLIENT_CONNECTIONS_KEY = "total_client_connections";
    public const string EVENT_CONNECTIONS_KEY = "total_event_connections";
    public const string CONTROL_LOG_CONNECTIONS_KEY = "total_control_log_connections";
    public const string CAR_CONTROL_LOG_CONNECTIONS_KEY = "total_car_control_log_connections";
    public const string IN_CAR_CONNECTIONS_KEY = "total_in_car_connections";

    #endregion
}
