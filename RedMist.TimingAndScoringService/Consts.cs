namespace RedMist.TimingAndScoringService;

public static class Consts
{
    public const string EVENT_STATUS_STREAM_KEY = "evt-st-{0}";
    public const string EVENT_TO_POD_KEY = "evt{0}-pod";
    public const string POD_WORKLOADS = "ts-pod-workloads";
    public const string CLIENT_ID = "ts-client-{0}";
    public const string EVENT_REF_ID = "org-{0}-eref-{1}";
    public const string SEND_FULL_STATUS = "fullstatus";
    public const string LOG_EVENT_DATA = "logevt{0}";
    public const string SEND_CONTROL_LOG = "controlLog";
    public const string SEND_COMPETITOR_METADATA = "competitor-metadata";
    public const string EVENT_RMON_STREAM_FIELD = "rmonitor-{0}-{1}";
    public const string EVENT_X2_LOOPS_STREAM_FIELD = "x2loop-{0}-999999";
    public const string EVENT_X2_PASSINGS_STREAM_FIELD = "x2pass-{0}-{1}";
    public const string EVENT_FLAGS_STREAM_FIELD = "flags-{0}-{1}";
    public const string EVENT_COMPETITORS = "competitors-{0}-999999";
}
