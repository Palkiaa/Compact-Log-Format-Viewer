﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog.Events;
using Serilog.Parsing;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Serilog.Formatting.Reader
{
    /// <summary>
    /// Reads files produced by <em>Serilog.Formatting.Compact.CompactJsonFormatter</em>. Events
    /// are expected to be encoded as newline-separated JSON documents.
    /// </summary>
    public class LogEventReader : IDisposable
    {
        static readonly MessageTemplateParser Parser = new();
        static readonly Rendering[] NoRenderings = Array.Empty<Rendering>();
        readonly TextReader _text;
        readonly JsonSerializer _serializer;

        int _lineNumber;

        /// <summary>
        /// Construct a <see cref="LogEventReader"/>.
        /// </summary>
        /// <param name="text">Text to read from.</param>
        /// <param name="serializer">If specified, a JSON serializer used when converting event documents.</param>
        public LogEventReader(TextReader text, JsonSerializer? serializer = null)
        {
            _text = text ?? throw new ArgumentNullException(nameof(text));
            _serializer = serializer ?? CreateSerializer();
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _text.Dispose();
        }

        /// <summary>
        /// Read a line from the input. Blank lines are skipped.
        /// </summary>
        /// <param name="evt"></param>
        /// <returns>True if an event could be read; false if the end-of-file was encountered.</returns>
        /// <exception cref="InvalidDataException">The data format is invalid.</exception>
        public bool TryRead([NotNullWhen(true)] out LogEvent? evt)
        {
            var line = _text.ReadLine();
            _lineNumber++;
            while (string.IsNullOrWhiteSpace(line))
            {
                if (line == null)
                {
                    evt = null;
                    return false;
                }
                line = _text.ReadLine();
                _lineNumber++;
            }

            evt = ParseLine(line);
            return true;
        }

        /// <summary>
        /// Read a line from the input asynchronously. Blank lines are skipped.
        /// </summary>
        /// <returns>The parsed <see cref="LogEvent" /> if one could be read; <see langword="null"/> if the end-of-file was encountered.</returns>
        /// <exception cref="InvalidDataException">The data format is invalid.</exception>
        public async Task<LogEvent?> TryReadAsync()
        {
            var line = await _text.ReadLineAsync().ConfigureAwait(false);
            _lineNumber++;
            while (string.IsNullOrWhiteSpace(line))
            {
                if (line == null)
                {
                    return null;
                }
                line = await _text.ReadLineAsync().ConfigureAwait(false);
                _lineNumber++;
            }

            return ParseLine(line);
        }

#if FEATURE_READ_LINE_ASYNC_CANCELLATION
    /// <inheritdoc cref="TryReadAsync()" />
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    public async Task<LogEvent?> TryReadAsync(CancellationToken cancellationToken)
    {
        var line = await _text.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        _lineNumber++;
        while (string.IsNullOrWhiteSpace(line))
        {
            if (line == null)
            {
                return null;
            }
            line = await _text.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            _lineNumber++;
        }

        return ParseLine(line);
    }
#endif

        /// <summary>
        /// Read a single log event from a JSON-encoded document.
        /// </summary>
        /// <param name="document">The event in compact-JSON.</param>
        /// <param name="serializer">If specified, a JSON serializer used when converting event documents.</param>
        /// <returns>The log event.</returns>
        /// <exception cref="InvalidDataException">The data format is invalid.</exception>
        public static LogEvent ReadFromString(string document, JsonSerializer? serializer = null)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));

            serializer ??= CreateSerializer();
            object? result;
            try
            {
                using var reader = new JsonTextReader(new StringReader(document));
                result = serializer.Deserialize(reader);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("The document could not be deserialized.", ex);
            }

            if (result is not JObject jObject)
                throw new InvalidDataException("The document is not a complete JSON object.");

            return ReadFromJObject(jObject);
        }

        /// <summary>
        /// Read a single log event from an already-deserialized JSON object.
        /// </summary>
        /// <param name="jObject">The deserialized compact-JSON event.</param>
        /// <returns>The log event.</returns>
        /// <exception cref="InvalidDataException">The data format is invalid.</exception>
        public static LogEvent ReadFromJObject(JObject jObject)
        {
            if (jObject == null) throw new ArgumentNullException(nameof(jObject));
            return ReadFromJObject(1, jObject);
        }

        LogEvent ParseLine(string line)
        {
            object? data;
            try
            {
                using var reader = new JsonTextReader(new StringReader(line));
                data = _serializer.Deserialize(reader);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"The data on line {_lineNumber} could not be deserialized.", ex);
            }

            if (data is not JObject fields)
                throw new InvalidDataException($"The data on line {_lineNumber} is not a complete JSON object.");

            return ReadFromJObject(_lineNumber, fields);
        }

        static LogEvent ReadFromJObject(int lineNumber, JObject jObject)
        {
            var timestamp = GetRequiredTimestampField(lineNumber, jObject, ClefFields.Timestamp);

            string? messageTemplate;
            if (TryGetOptionalField(lineNumber, jObject, ClefFields.MessageTemplate, out var mt))
                messageTemplate = mt;
            else if (TryGetOptionalField(lineNumber, jObject, ClefFields.Message, out var m))
                messageTemplate = MessageTemplateSyntax.Escape(m);
            else
                messageTemplate = null;

            var level = LogEventLevel.Information;
            if (TryGetOptionalField(lineNumber, jObject, ClefFields.Level, out var l) && !Enum.TryParse(l, true, out level))
                throw new InvalidDataException($"The `{ClefFields.Level}` value on line {lineNumber} is not a valid `{nameof(LogEventLevel)}`.");

            Exception? exception = null;
            if (TryGetOptionalField(lineNumber, jObject, ClefFields.Exception, out var ex))
                exception = new TextException(ex);

            ActivityTraceId traceId = default;
            if (TryGetOptionalField(lineNumber, jObject, ClefFields.TraceId, out var tr))
                traceId = ActivityTraceId.CreateFromString(tr.AsSpan());

            ActivitySpanId spanId = default;
            if (TryGetOptionalField(lineNumber, jObject, ClefFields.SpanId, out var sp))
                spanId = ActivitySpanId.CreateFromString(sp.AsSpan());

            var parsedTemplate = messageTemplate == null ?
                new MessageTemplate(Array.Empty<MessageTemplateToken>()) :
                Parser.Parse(messageTemplate);

            var renderings = NoRenderings;
            var properties = new List<LogEventProperty>();

            if (jObject.TryGetValue(ClefFields.Renderings, out var r))
            {
                if (r is not JObject props)
                    throw new InvalidDataException($"The `{ClefFields.Renderings}` value on line {lineNumber} is not an object as expected.");


                var temp = new List<Rendering>();
                foreach (MessageTemplateToken token in parsedTemplate.Tokens)
                {
                    var regex = new Regex(@"\{([^\}]+)\}");
                    var matches = regex.Matches(token.ToString());
                    foreach (Match match in matches)
                    {
                        var origKey = match.Groups[1].Value;
                        var key = origKey;
                        if (key.StartsWith('@'))
                            key = key[1..];

                        temp.Add(new Rendering(origKey, string.Empty, props[key]?.Value<string>()!));
                    }
                }
                renderings = temp.ToArray();

                properties = props
                    .Properties()
                    .Select(f =>
                    {
                        var name = ClefFields.Unescape(f.Name);
                        var renderingsByFormat = renderings.Length != 0 ? renderings.Where(rd => rd.Name == name).ToArray() : NoRenderings;
                        return PropertyFactory.CreateProperty(name, f.Value, renderingsByFormat);
                    })
                    .ToList();
            }

            if (TryGetOptionalEventId(lineNumber, jObject, ClefFields.EventId, out var eventId))
            {
                properties.Add(new LogEventProperty("@i", new ScalarValue(eventId)));
            }

            return new LogEvent(timestamp, level, exception, parsedTemplate, properties, traceId, spanId);
        }

        static bool TryGetOptionalField(int lineNumber, JObject data, string field, [NotNullWhen(true)] out string? value)
        {
            if (!data.TryGetValue(field, out var token) || token.Type == JTokenType.Null)
            {
                value = null;
                return false;
            }

            if (token.Type != JTokenType.String)
                throw new InvalidDataException($"The value of `{field}` on line {lineNumber} is not in a supported format.");

            value = token.Value<string>()!;
            return true;
        }

        static bool TryGetOptionalEventId(int lineNumber, JObject data, string field, out object? eventId)
        {
            if (!data.TryGetValue(field, out var token) || token.Type == JTokenType.Null)
            {
                eventId = null;
                return false;
            }

            switch (token.Type)
            {
                case JTokenType.String:
                    eventId = token.Value<string>();
                    return true;
                case JTokenType.Integer:
                    eventId = token.Value<uint>();
                    return true;
                default:
                    throw new InvalidDataException(
                        $"The value of `{field}` on line {lineNumber} is not in a supported format.");
            }
        }

        static DateTimeOffset GetRequiredTimestampField(int lineNumber, JObject data, string field)
        {
            if (!data.TryGetValue(field, out var token) || token.Type == JTokenType.Null)
                throw new InvalidDataException($"The data on line {lineNumber} does not include the required `{field}` field.");

            if (token.Type == JTokenType.Date)
            {
                var dt = token.Value<JValue>()!.Value;
                if (dt is DateTimeOffset offset)
                    return offset;

                return (DateTime)dt!;
            }
            else
            {
                if (token.Type != JTokenType.String)
                    throw new InvalidDataException($"The value of `{field}` on line {lineNumber} is not in a supported format.");

                var text = token.Value<string>()!;
                if (!DateTimeOffset.TryParse(text, out var offset))
                    throw new InvalidDataException($"The value of `{field}` on line {lineNumber} is not in a supported timestamp format.");

                return offset;
            }
        }

        static JsonSerializer CreateSerializer()
        {
            return JsonSerializer.Create(new JsonSerializerSettings
            {
                DateParseHandling = DateParseHandling.None,
                Culture = CultureInfo.InvariantCulture
            });
        }
    }
}
