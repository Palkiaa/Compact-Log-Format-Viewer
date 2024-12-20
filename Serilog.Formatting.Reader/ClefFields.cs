namespace Serilog.Formatting.Reader
{
    static class ClefFields
    {
        public const string Timestamp = "Timestamp";
        public const string MessageTemplate = "MessageTemplate";
        public const string Level = "Level";
        public const string Exception = "Exception";
        public const string Renderings = "Properties"; // unused
        public const string EventId = "@i"; // unused
        public const string Message = "@m"; // unused
        public const string TraceId = "@tr"; // unused
        public const string SpanId = "@sp"; // unused

        public static readonly string[] All = new string[] { Timestamp, MessageTemplate, Level, Exception, EventId, Message };

        public static string Unescape(string name) => name;

        public static bool IsUnrecognized(string name) => !All.Contains(name);
    }
}
