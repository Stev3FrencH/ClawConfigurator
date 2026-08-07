using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace McenterLite.Shared.Ipc
{
    /// <summary>
    /// One message on the wire. Messages are newline-delimited UTF-8 JSON objects:
    ///
    /// <code>
    /// {"id":12,"cmd":1,"fn":10,"v":"35"}
    /// {"id":12,"cmd":2,"fn":10,"v":"33"}          &lt;- clamped; the widget renders 33, not 35
    /// {"id":0,"cmd":3,"fn":22,"v":"0,0,58,..."}   &lt;- unsolicited telemetry push
    /// {"id":13,"cmd":4,"fn":30,"v":null,"err":"WMI method not present on this firmware"}
    /// </code>
    ///
    /// <para>
    /// <b><see cref="Value"/> is ALWAYS a JSON string or null - never a bare number or bool.</b>
    /// Typing is the receiver's job, via the <c>As*</c> helpers. The reference project let the
    /// field hold any JSON type and then needed three regex fallbacks (string, then bool, then
    /// number) to read it back, with a documented escaping bug in the unescape path. Fixing the
    /// type at the boundary removes that entire failure class: there is exactly one thing to
    /// serialize and exactly one thing to parse.
    /// </para>
    ///
    /// <para>
    /// Structured payloads (fan state, LED spec, device caps) are carried as strings using their
    /// own compact encodings rather than nested JSON, which keeps this parser flat and total.
    /// </para>
    /// </summary>
    public sealed class PipeEnvelope
    {
        /// <summary>
        /// Correlation id. A <see cref="Command.Response"/> or <see cref="Command.Error"/> echoes
        /// the id of the request it answers. Unsolicited <see cref="Command.Event"/> pushes use 0.
        /// </summary>
        public int Id { get; set; }

        public Command Cmd { get; set; }

        public Function Fn { get; set; }

        /// <summary>The payload, or null. Always transported as a JSON string.</summary>
        public string Value { get; set; }

        /// <summary>Human-readable failure reason. Only meaningful on <see cref="Command.Error"/>.</summary>
        public string Error { get; set; }

        public PipeEnvelope() { }

        public PipeEnvelope(int id, Command cmd, Function fn, string value = null, string error = null)
        {
            Id = id;
            Cmd = cmd;
            Fn = fn;
            Value = value;
            Error = error;
        }

        // ── Construction helpers ────────────────────────────────────────────────

        public static PipeEnvelope Get(int id, Function fn) => new PipeEnvelope(id, Command.Get, fn);

        public static PipeEnvelope Set(int id, Function fn, string value) =>
            new PipeEnvelope(id, Command.Set, fn, value);

        public static PipeEnvelope Response(int id, Function fn, string value) =>
            new PipeEnvelope(id, Command.Response, fn, value);

        public static PipeEnvelope Event(Function fn, string value) =>
            new PipeEnvelope(0, Command.Event, fn, value);

        public static PipeEnvelope Failure(int id, Function fn, string error) =>
            new PipeEnvelope(id, Command.Error, fn, null, error);

        // ── Typed accessors ─────────────────────────────────────────────────────
        // Culture-invariant on purpose: this is a wire format, not display text. A helper
        // running under a comma-decimal locale must not emit "1,5" for a widget to misread.

        public bool AsBool(bool fallback = false)
        {
            if (string.IsNullOrEmpty(Value)) return fallback;
            if (Value == "1") return true;
            if (Value == "0") return false;
            return bool.TryParse(Value, out var b) ? b : fallback;
        }

        public int AsInt(int fallback = 0) =>
            int.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;

        public T AsEnum<T>(T fallback) where T : struct
        {
            if (!int.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                return fallback;
            var boxed = (object)i;
            return Enum.IsDefined(typeof(T), boxed) ? (T)boxed : fallback;
        }

        /// <summary>Canonical bool encoding. Always "1"/"0" so <see cref="AsBool"/> needs no locale rules.</summary>
        public static string FromBool(bool value) => value ? "1" : "0";

        public static string FromInt(int value) => value.ToString(CultureInfo.InvariantCulture);

        public static string FromEnum<T>(T value) where T : struct =>
            System.Convert.ToInt32(value).ToString(CultureInfo.InvariantCulture);

        // ── Serialization ───────────────────────────────────────────────────────

        /// <summary>
        /// Renders this envelope as a single JSON line WITHOUT a trailing newline. The transport
        /// adds the delimiter, so callers can choose their own line ending.
        /// </summary>
        public string Serialize()
        {
            var sb = new StringBuilder(96);
            sb.Append("{\"id\":").Append(Id.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"cmd\":").Append(((int)Cmd).ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"fn\":").Append(((int)Fn).ToString(CultureInfo.InvariantCulture));

            sb.Append(",\"v\":");
            AppendJsonString(sb, Value);

            if (Error != null)
            {
                sb.Append(",\"err\":");
                AppendJsonString(sb, Error);
            }

            sb.Append('}');
            return sb.ToString();
        }

        public override string ToString() => Serialize();

        /// <summary>
        /// Parses one JSON line. Returns false rather than throwing on anything malformed - this
        /// input arrives over an IPC boundary reachable by any UWP app on the machine, so a bad
        /// message must be a dropped message, never an unhandled exception in the elevated helper.
        /// </summary>
        public static bool TryParse(string json, out PipeEnvelope envelope)
        {
            envelope = null;
            if (string.IsNullOrEmpty(json)) return false;

            if (!TryReadFlatObject(json, out var fields)) return false;

            // id/cmd/fn are required and must be integers.
            if (!fields.TryGetValue("id", out var idRaw) || !TryReadInt(idRaw, out var id)) return false;
            if (!fields.TryGetValue("cmd", out var cmdRaw) || !TryReadInt(cmdRaw, out var cmd)) return false;
            if (!fields.TryGetValue("fn", out var fnRaw) || !TryReadInt(fnRaw, out var fn)) return false;

            if (!Enum.IsDefined(typeof(Command), cmd)) return false;

            // An unknown Function is NOT a parse failure. A newer helper may legitimately push a
            // Function this widget build has never heard of; the dispatcher drops it by name later.
            // Rejecting it here would desynchronize the whole connection over one unknown field.

            string value = null;
            if (fields.TryGetValue("v", out var vRaw) && vRaw.Kind == JsonKind.String)
                value = vRaw.Text;

            string error = null;
            if (fields.TryGetValue("err", out var eRaw) && eRaw.Kind == JsonKind.String)
                error = eRaw.Text;

            envelope = new PipeEnvelope
            {
                Id = id,
                Cmd = (Command)cmd,
                Fn = (Function)fn,
                Value = value,
                Error = error,
            };
            return true;
        }

        // ── Minimal JSON (flat object only) ─────────────────────────────────────
        // Hand-written because Shared is netstandard2.0 with zero package references and is
        // consumed by .NET Native. Scope is deliberately tiny: a flat object whose values are
        // strings, numbers, booleans or null. Nested objects and arrays are rejected, which is
        // safe because the format above never produces them.

        private enum JsonKind { String, Number, Literal }

        private struct JsonValue
        {
            public JsonKind Kind;
            public string Text;
        }

        private static bool TryReadInt(JsonValue v, out int result)
        {
            result = 0;
            if (v.Kind != JsonKind.Number) return false;
            return int.TryParse(v.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }

        private static bool TryReadFlatObject(string s, out Dictionary<string, JsonValue> fields)
        {
            fields = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            int i = 0;

            SkipWhitespace(s, ref i);
            if (i >= s.Length || s[i] != '{') return false;
            i++;

            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == '}') return true; // empty object

            while (i < s.Length)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != '"') return false;
                if (!TryReadString(s, ref i, out var key)) return false;

                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != ':') return false;
                i++;

                SkipWhitespace(s, ref i);
                if (i >= s.Length) return false;

                JsonValue value;
                char c = s[i];
                if (c == '"')
                {
                    if (!TryReadString(s, ref i, out var text)) return false;
                    value = new JsonValue { Kind = JsonKind.String, Text = text };
                }
                else if (c == '{' || c == '[')
                {
                    return false; // out of scope by design
                }
                else
                {
                    int start = i;
                    while (i < s.Length && s[i] != ',' && s[i] != '}' && !IsWhitespace(s[i])) i++;
                    if (i == start) return false;
                    var token = s.Substring(start, i - start);
                    value = new JsonValue
                    {
                        Kind = (token == "null" || token == "true" || token == "false")
                            ? JsonKind.Literal
                            : JsonKind.Number,
                        Text = token,
                    };
                }

                fields[key] = value;

                SkipWhitespace(s, ref i);
                if (i >= s.Length) return false;
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') return true;
                return false;
            }

            return false;
        }

        private static bool IsWhitespace(char c) => c == ' ' || c == '\t' || c == '\r' || c == '\n';

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && IsWhitespace(s[i])) i++;
        }

        /// <summary>Reads a JSON string starting at the opening quote, resolving all escapes.</summary>
        private static bool TryReadString(string s, ref int i, out string result)
        {
            result = null;
            if (i >= s.Length || s[i] != '"') return false;
            i++;

            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i];

                if (c == '"')
                {
                    i++;
                    result = sb.ToString();
                    return true;
                }

                if (c != '\\')
                {
                    sb.Append(c);
                    i++;
                    continue;
                }

                i++; // consume backslash
                if (i >= s.Length) return false;

                char esc = s[i];
                switch (esc)
                {
                    case '"': sb.Append('"'); i++; break;
                    case '\\': sb.Append('\\'); i++; break;
                    case '/': sb.Append('/'); i++; break;
                    case 'b': sb.Append('\b'); i++; break;
                    case 'f': sb.Append('\f'); i++; break;
                    case 'n': sb.Append('\n'); i++; break;
                    case 'r': sb.Append('\r'); i++; break;
                    case 't': sb.Append('\t'); i++; break;
                    case 'u':
                        // \uXXXX. Surrogate pairs arrive as two consecutive escapes and are
                        // appended individually - char-by-char append reassembles them correctly.
                        if (i + 4 >= s.Length) return false;
                        var hex = s.Substring(i + 1, 4);
                        if (!ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                            return false;
                        sb.Append((char)code);
                        i += 5;
                        break;
                    default:
                        return false; // unknown escape - reject rather than guess
                }
            }

            return false; // unterminated
        }

        /// <summary>Appends a JSON string literal, or the bare token <c>null</c>.</summary>
        private static void AppendJsonString(StringBuilder sb, string value)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            // Control characters are not legal raw inside a JSON string.
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
