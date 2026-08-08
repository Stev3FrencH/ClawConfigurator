using McenterLite.Shared.Ipc;
using Xunit;

namespace McenterLite.Shared.Tests
{
    public class PipeEnvelopeTests
    {
        private static PipeEnvelope RoundTrip(PipeEnvelope source)
        {
            var wire = source.Serialize();
            Assert.True(PipeEnvelope.TryParse(wire, out var parsed), $"failed to parse: {wire}");
            return parsed;
        }

        [Fact]
        public void RoundTrips_AllFields()
        {
            var parsed = RoundTrip(new PipeEnvelope(42, Command.Set, Function.Pl1, "35"));

            Assert.Equal(42, parsed.Id);
            Assert.Equal(Command.Set, parsed.Cmd);
            Assert.Equal(Function.Pl1, parsed.Fn);
            Assert.Equal("35", parsed.Value);
            Assert.Null(parsed.Error);
        }

        [Fact]
        public void RoundTrips_NullValue()
        {
            var parsed = RoundTrip(new PipeEnvelope(1, Command.Get, Function.FanState));
            Assert.Null(parsed.Value);
        }

        [Fact]
        public void RoundTrips_ErrorMessage()
        {
            var parsed = RoundTrip(PipeEnvelope.Failure(7, Function.ChargeLimitEnabled, "not present"));

            Assert.Equal(Command.Error, parsed.Cmd);
            Assert.Equal("not present", parsed.Error);
            Assert.Null(parsed.Value);
        }

        /// <summary>
        /// The whole reason this type hand-rolls serialization instead of using string
        /// concatenation with a regex reader. Every one of these payloads is something a regex
        /// "extract between quotes" parser gets wrong.
        /// </summary>
        [Theory]
        [InlineData("has \"quotes\" inside")]
        [InlineData(@"has \backslashes\ inside")]
        [InlineData("trailing backslash \\")]
        [InlineData("json-ish: {\"v\":\"nested\"}")]
        [InlineData("newline\nand\ttab")]
        [InlineData("carriage\r\nreturn")]
        [InlineData("control\u0001char")]
        [InlineData("unicode: éü中文")]
        [InlineData("emoji: \U0001F525")]
        [InlineData("delimiters ; = | , used by other encodings")]
        [InlineData("")]
        public void RoundTrips_HostilePayloads(string payload)
        {
            var parsed = RoundTrip(new PipeEnvelope(3, Command.Set, Function.LedEnabled, payload));
            Assert.Equal(payload, parsed.Value);
        }

        [Fact]
        public void RoundTrips_HostileErrorText()
        {
            var parsed = RoundTrip(
                PipeEnvelope.Failure(9, Function.Pl1, "WMI said: \"denied\"\n\tat ring \\ 0"));

            Assert.Equal("WMI said: \"denied\"\n\tat ring \\ 0", parsed.Error);
        }

        [Fact]
        public void Serialize_EscapesControlCharacters()
        {
            var payload = "a\u0001b\u001fc";
            var wire = new PipeEnvelope(1, Command.Set, Function.LedEnabled, payload).Serialize();

            // A raw control character inside a JSON string is malformed JSON, so the widget's
            // platform parser would reject the whole message.
            // IndexOf(char) is ordinal. Assert.DoesNotContain(string, string) is CULTURE-sensitive,
            // and ICU gives control characters zero collation weight - so it reports a match at
            // position 0 of every string and would pass no matter what Serialize produced.
            Assert.Equal(-1, wire.IndexOf('\u0001'));
            Assert.Equal(-1, wire.IndexOf('\u001f'));
            Assert.Contains("\\u0001", wire);
            Assert.Contains("\\u001f", wire);

            Assert.True(PipeEnvelope.TryParse(wire, out var parsed));
            Assert.Equal(payload, parsed.Value);
        }

        [Fact]
        public void Parse_RejectsMalformedInput()
        {
            // This arrives over an ACL that admits any app on the machine. Malformed input must
            // be a dropped message, never an exception inside the elevated helper.
            Assert.False(PipeEnvelope.TryParse(null, out _));
            Assert.False(PipeEnvelope.TryParse("", out _));
            Assert.False(PipeEnvelope.TryParse("not json", out _));
            Assert.False(PipeEnvelope.TryParse("{", out _));
            Assert.False(PipeEnvelope.TryParse("{\"id\":1}", out _));                  // missing cmd/fn
            Assert.False(PipeEnvelope.TryParse("{\"id\":\"x\",\"cmd\":1,\"fn\":1}", out _)); // id not a number
            Assert.False(PipeEnvelope.TryParse("{\"id\":1,\"cmd\":99,\"fn\":1}", out _));    // undefined command
            Assert.False(PipeEnvelope.TryParse("{\"id\":1,\"cmd\":1,\"fn\":1,\"v\":\"unterminated}", out _));
            Assert.False(PipeEnvelope.TryParse("{\"id\":1,\"cmd\":1,\"fn\":1,\"v\":{\"a\":1}}", out _)); // nested
        }

        [Fact]
        public void Parse_AcceptsUnknownFunction()
        {
            // A newer helper may push a Function this build has never heard of. Rejecting the
            // message would desynchronize the connection over one unknown field; the dispatcher
            // drops it by name instead.
            Assert.True(PipeEnvelope.TryParse("{\"id\":1,\"cmd\":3,\"fn\":31337,\"v\":\"x\"}", out var parsed));
            Assert.Equal(31337, (int)parsed.Fn);
        }

        [Fact]
        public void Parse_IgnoresUnknownFields()
        {
            Assert.True(PipeEnvelope.TryParse(
                "{\"id\":1,\"cmd\":0,\"fn\":10,\"v\":null,\"future\":\"ignored\"}", out var parsed));
            Assert.Equal(Function.Pl1, parsed.Fn);
        }

        [Fact]
        public void Parse_ToleratesWhitespace()
        {
            Assert.True(PipeEnvelope.TryParse(
                "  { \"id\" : 5 , \"cmd\" : 1 , \"fn\" : 10 , \"v\" : \"20\" }  ", out var parsed));
            Assert.Equal(5, parsed.Id);
            Assert.Equal("20", parsed.Value);
        }

        [Fact]
        public void Serialize_ProducesSingleLine()
        {
            // The transport is newline-delimited, so an embedded newline would split one message
            // into two unparseable halves.
            var wire = new PipeEnvelope(1, Command.Set, Function.LedEnabled, "a\nb\r\nc").Serialize();
            Assert.Equal(-1, wire.IndexOf('\n'));
            Assert.Equal(-1, wire.IndexOf('\r'));
        }

        [Fact]
        public void AsBool_ReadsCanonicalEncoding()
        {
            Assert.True(new PipeEnvelope(1, Command.Set, Function.CpuBoost, PipeEnvelope.FromBool(true)).AsBool());
            Assert.False(new PipeEnvelope(1, Command.Set, Function.CpuBoost, PipeEnvelope.FromBool(false)).AsBool());

            // Tolerated for robustness, but FromBool is what we emit.
            Assert.True(new PipeEnvelope(1, Command.Set, Function.CpuBoost, "true").AsBool());
            Assert.False(new PipeEnvelope(1, Command.Set, Function.CpuBoost, "false").AsBool());
        }

        [Fact]
        public void AsBool_FallsBackOnGarbage()
        {
            Assert.True(new PipeEnvelope(1, Command.Set, Function.CpuBoost, "banana").AsBool(fallback: true));
            Assert.False(new PipeEnvelope(1, Command.Set, Function.CpuBoost, null).AsBool(fallback: false));
        }

        [Fact]
        public void AsInt_FallsBackOnGarbage()
        {
            Assert.Equal(17, new PipeEnvelope(1, Command.Set, Function.Pl1, "17").AsInt());
            Assert.Equal(-1, new PipeEnvelope(1, Command.Set, Function.Pl1, "banana").AsInt(-1));
            Assert.Equal(-1, new PipeEnvelope(1, Command.Set, Function.Pl1, null).AsInt(-1));
        }

        [Fact]
        public void AsEnum_RejectsOutOfRangeValues()
        {
            var ok = new PipeEnvelope(1, Command.Set, Function.FanPreset,
                PipeEnvelope.FromEnum(FanPreset.Cooling));
            Assert.Equal(FanPreset.Cooling, ok.AsEnum(FanPreset.Default));

            // An out-of-range ordinal must not become an undefined enum value that later
            // switch statements silently fall through.
            var bad = new PipeEnvelope(1, Command.Set, Function.FanPreset, "77");
            Assert.Equal(FanPreset.Default, bad.AsEnum(FanPreset.Default));
        }

        [Fact]
        public void Event_UsesZeroCorrelationId()
        {
            var evt = PipeEnvelope.Event(Function.FanState, "x");
            Assert.Equal(0, evt.Id);
            Assert.Equal(Command.Event, evt.Cmd);
        }
    }
}
