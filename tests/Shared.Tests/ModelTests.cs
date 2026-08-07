using McenterLite.Shared.Ipc;
using McenterLite.Shared.Model;
using Xunit;

namespace McenterLite.Shared.Tests
{
    public class DeviceCapsTests
    {
        private static DeviceCaps Claw8Ex() => new DeviceCaps
        {
            Model = "MSI Claw 8 EX AI+ CG3EM",
            Supported = true,
            MinPl1 = 8,
            MaxPl1 = 35,
            MaxPl2 = 45,
            Pl2MinOffset = 2,
            TdpBackend = TdpBackendKind.Wmi,
            HasFan = true,
            HasChargeLimit = true,
            HasLed = true,
            HasHwMouse = true,
            HasIgcl = true,
            FanDutyFloor = 58,
        };

        [Fact]
        public void RoundTrips()
        {
            var parsed = DeviceCaps.Parse(Claw8Ex().Serialize());

            Assert.Equal("MSI Claw 8 EX AI+ CG3EM", parsed.Model);
            Assert.True(parsed.Supported);
            Assert.Equal(35, parsed.MaxPl1);
            Assert.Equal(45, parsed.MaxPl2);
            Assert.Equal(2, parsed.Pl2MinOffset);
            Assert.Equal(TdpBackendKind.Wmi, parsed.TdpBackend);
            Assert.Equal(58, parsed.FanDutyFloor);
            Assert.True(parsed.HasIgcl);
        }

        [Fact]
        public void RoundTrips_ModelNameContainingDelimiters()
        {
            // The model string comes from firmware; it is not ours to assume well-formed.
            var caps = Claw8Ex();
            caps.Model = "weird;model=name\\here";

            Assert.Equal("weird;model=name\\here", DeviceCaps.Parse(caps.Serialize()).Model);
        }

        [Fact]
        public void Parse_IgnoresUnknownKeys()
        {
            var parsed = DeviceCaps.Parse("model=X;supported=1;futureFeature=42");
            Assert.Equal("X", parsed.Model);
            Assert.True(parsed.Supported);
        }

        [Fact]
        public void Parse_ToleratesGarbage()
        {
            DeviceCaps.Parse("");
            DeviceCaps.Parse(null);
            DeviceCaps.Parse(";;;=;=x;");
            DeviceCaps.Parse("maxPl1=notanumber");
        }

        [Fact]
        public void Parse_DefaultsUnknownBackendToUnavailable()
        {
            Assert.Equal(TdpBackendKind.Unavailable, DeviceCaps.Parse("tdpBackend=1234").TdpBackend);
        }

        // ── Power-limit clamping ────────────────────────────────────────────────
        // These run in the HELPER on every Set. The pipe is reachable by any app on the machine,
        // so the widget's slider bounds are a convenience, never the enforcement point.

        [Fact]
        public void Clamp_HoldsTheDeviceCeiling()
        {
            var caps = Claw8Ex();
            int pl1 = 999, pl2 = 999;

            caps.ClampPowerLimits(ref pl1, ref pl2);

            Assert.Equal(35, pl1);
            Assert.Equal(45, pl2);
        }

        [Fact]
        public void Clamp_HoldsTheDeviceFloor()
        {
            var caps = Claw8Ex();
            int pl1 = -50, pl2 = -50;

            caps.ClampPowerLimits(ref pl1, ref pl2);

            Assert.Equal(8, pl1);
            Assert.Equal(10, pl2); // floor + the 2 W headroom the EX requires
        }

        [Fact]
        public void Clamp_EnforcesPl2Headroom()
        {
            var caps = Claw8Ex();
            int pl1 = 30, pl2 = 30; // no headroom requested

            caps.ClampPowerLimits(ref pl1, ref pl2);

            Assert.True(pl2 - pl1 >= caps.Pl2MinOffset, $"got PL1={pl1} PL2={pl2}");
        }

        [Fact]
        public void Clamp_AppliesTheP11CeilingBeforeHeadroom()
        {
            // On real Claw 8 EX caps there is no conflict to resolve: PL1 hits its own 35 W
            // ceiling first, which leaves far more than the required 2 W of headroom.
            var caps = Claw8Ex();
            int pl1 = 45, pl2 = 45;

            caps.ClampPowerLimits(ref pl1, ref pl2);

            Assert.Equal(35, pl1);
            Assert.Equal(45, pl2);
        }

        [Fact]
        public void Clamp_SacrificesPl1WhenTheCeilingCannotSatisfyHeadroom()
        {
            // Headroom is a firmware rule; PL1 is a preference. When they conflict, PL1 gives way.
            // Unreachable with real Claw 8 EX caps (MaxPl2 exceeds MaxPl1 + offset), so this uses
            // a degenerate capability set - the branch exists to keep a bad DeviceCaps from
            // producing a pair the firmware would reject.
            var caps = new DeviceCaps { MinPl1 = 8, MaxPl1 = 30, MaxPl2 = 30, Pl2MinOffset = 2 };
            int pl1 = 30, pl2 = 30;

            caps.ClampPowerLimits(ref pl1, ref pl2);

            Assert.Equal(30, pl2);
            Assert.Equal(28, pl1);
            Assert.True(pl2 - pl1 >= caps.Pl2MinOffset);
        }

        [Theory]
        [InlineData(8, 10)]
        [InlineData(15, 20)]
        [InlineData(35, 45)]
        public void Clamp_LeavesValidPairsAlone(int inPl1, int inPl2)
        {
            var caps = Claw8Ex();
            int pl1 = inPl1, pl2 = inPl2;

            caps.ClampPowerLimits(ref pl1, ref pl2);

            Assert.Equal(inPl1, pl1);
            Assert.Equal(inPl2, pl2);
        }

        [Fact]
        public void Clamp_AlwaysProducesAValidPair()
        {
            var caps = Claw8Ex();

            for (int a = -20; a <= 80; a += 3)
            for (int b = -20; b <= 80; b += 3)
            {
                int pl1 = a, pl2 = b;
                caps.ClampPowerLimits(ref pl1, ref pl2);

                Assert.InRange(pl1, caps.MinPl1, caps.MaxPl1);
                Assert.InRange(pl2, 0, caps.MaxPl2);
                Assert.True(pl2 - pl1 >= caps.Pl2MinOffset,
                    $"({a},{b}) -> PL1={pl1} PL2={pl2} violates the {caps.Pl2MinOffset}W headroom rule");
            }
        }
    }

    public class FanStateTests
    {
        [Fact]
        public void RoundTrips()
        {
            var state = new FanState
            {
                Table = new byte[] { 0, 0, 40, 49, 58, 67, 75, 94 },
                ControlEnabled = true,
                ReadOk = true,
                FullSpeed = false,
                Rpm = 3571,
                Temps = new[] { 44, 54, 64, 74, 82 },
            };

            var parsed = FanState.Parse(state.Serialize());

            Assert.Equal(state.Table, parsed.Table);
            Assert.True(parsed.ControlEnabled);
            Assert.True(parsed.ReadOk);
            Assert.False(parsed.FullSpeed);
            Assert.Equal(3571, parsed.Rpm);
            Assert.Equal(state.Temps, parsed.Temps);
        }

        [Fact]
        public void RoundTrips_UnavailableRpm()
        {
            var parsed = FanState.Parse(new FanState { ReadOk = false, Rpm = -1 }.Serialize());

            Assert.False(parsed.ReadOk);
            Assert.Equal(-1, parsed.Rpm);
        }

        [Fact]
        public void Parse_ToleratesTruncatedInput()
        {
            FanState.Parse("");
            FanState.Parse(null);
            FanState.Parse("0,0,40|1");
            FanState.Parse("|||||");
            FanState.Parse("garbage|garbage|garbage|garbage|garbage|garbage");
        }
    }

    public class LedSpecTests
    {
        [Fact]
        public void RoundTrips()
        {
            var spec = new LedSpec
            {
                Mode = LedMode.Breathing,
                Brightness = 65,
                Speed = 30,
                Zones = new[] { 0xFF0000, 0x00FF00, 0x0000FF },
            };

            var parsed = LedSpec.Parse(spec.Serialize());

            Assert.Equal(LedMode.Breathing, parsed.Mode);
            Assert.Equal(65, parsed.Brightness);
            Assert.Equal(30, parsed.Speed);
            Assert.Equal(new[] { 0xFF0000, 0x00FF00, 0x0000FF }, parsed.Zones);
        }

        [Fact]
        public void Parse_ClampsOutOfRangeValues()
        {
            var parsed = LedSpec.Parse("1;500;-20;300,-5,999;0,0,0;0,0,0");

            Assert.Equal(100, parsed.Brightness);
            Assert.Equal(0, parsed.Speed);
            Assert.Equal(0xFF00FF, parsed.Zones[0]);
        }

        [Fact]
        public void Parse_DefaultsUnknownMode()
        {
            Assert.Equal(LedMode.Static, LedSpec.Parse("77;80;50;0,0,0;0,0,0;0,0,0").Mode);
        }

        [Fact]
        public void Parse_ToleratesTruncatedInput()
        {
            LedSpec.Parse("");
            LedSpec.Parse(null);
            LedSpec.Parse("1");
            LedSpec.Parse("1;80;50;0,0");     // malformed zone
            LedSpec.Parse("a;b;c;d;e;f");
        }

        [Fact]
        public void IsEquivalentTo_SuppressesRedundantHidWrites()
        {
            var a = new LedSpec { Mode = LedMode.Static, Brightness = 80, Speed = 50 };
            var b = LedSpec.Parse(a.Serialize());

            Assert.True(a.IsEquivalentTo(b));

            b.Zones[1] ^= 0x010101;
            Assert.False(a.IsEquivalentTo(b));
            Assert.False(a.IsEquivalentTo(null));
        }
    }
}
