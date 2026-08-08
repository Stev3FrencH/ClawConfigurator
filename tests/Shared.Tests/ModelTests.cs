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
            MaxPl1Dc = 25,
            MaxPl2Dc = 30,
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
            Assert.Equal(25, parsed.MaxPl1Dc);
            Assert.Equal(30, parsed.MaxPl2Dc);
            Assert.Equal(2, parsed.Pl2MinOffset);
            Assert.Equal(TdpBackendKind.Wmi, parsed.TdpBackend);
            Assert.Equal(58, parsed.FanDutyFloor);
            Assert.True(parsed.HasIgcl);
        }

        [Theory]
        // Under both ceilings: untouched.
        [InlineData(8, 10, 8, 10)]
        [InlineData(17, 19, 17, 19)]
        [InlineData(25, 30, 25, 30)]
        // A coupled pair keeps its gap: the AC knee 35/37 becomes the DC knee 25/27.
        [InlineData(35, 37, 25, 27)]
        [InlineData(30, 32, 25, 27)]
        // Over PL2 only.
        [InlineData(20, 45, 20, 30)]
        // Over both: capped to the battery ceilings.
        [InlineData(35, 45, 25, 30)]
        [InlineData(30, 40, 25, 30)]
        public void ClampForBattery_CapsToTheBatteryCeilings(int pl1, int pl2, int expectedPl1, int expectedPl2)
        {
            var caps = Claw8Ex();
            caps.ClampPowerLimitsForBattery(ref pl1, ref pl2);

            Assert.Equal(expectedPl1, pl1);
            Assert.Equal(expectedPl2, pl2);
        }

        [Theory]
        // Walking PL1 up drags PL2 with it, holding the 2 W headroom.
        [InlineData(8, 10)]
        [InlineData(9, 11)]
        [InlineData(17, 19)]
        [InlineData(34, 36)]
        // The knee: PL1 tops out at 35, PL2 at 37.
        [InlineData(35, 37)]
        public void CoupleFromPl1_DragsPl2AlongAtTheMinimumHeadroom(int pl1, int expectedPl2)
        {
            var caps = Claw8Ex();
            int actualPl1 = pl1, actualPl2 = 0;
            caps.CoupleFromPl1(ref actualPl1, ref actualPl2);

            Assert.Equal(pl1, actualPl1);
            Assert.Equal(expectedPl2, actualPl2);
        }

        [Theory]
        // Below the knee PL1 follows PL2 down, staying 2 W under.
        [InlineData(10, 8)]
        [InlineData(19, 17)]
        [InlineData(37, 35)]
        // Past the knee PL1 is pinned and PL2 travels alone to its own ceiling.
        [InlineData(38, 35)]
        [InlineData(45, 35)]
        public void CoupleFromPl2_PinsPl1AtItsCeiling(int pl2, int expectedPl1)
        {
            var caps = Claw8Ex();
            int actualPl1 = 0, actualPl2 = pl2;
            caps.CoupleFromPl2(ref actualPl1, ref actualPl2);

            Assert.Equal(pl2, actualPl2);
            Assert.Equal(expectedPl1, actualPl1);
        }

        [Fact]
        public void Coupling_HoldsTheHeadroomAcrossTheWholeRange()
        {
            // Whichever slider is driven, and wherever it lands, the pair the user ends up with
            // must be one the firmware accepts. This is the invariant the two sliders exist to
            // preserve, so it is checked exhaustively rather than at a few sample points.
            var caps = Claw8Ex();

            for (int v = caps.MinPl1; v <= caps.MaxPl1; v++)
            {
                int pl1 = v, pl2 = 0;
                caps.CoupleFromPl1(ref pl1, ref pl2);
                Assert.True(pl2 - pl1 >= caps.Pl2MinOffset, $"PL1={v} gave {pl1}/{pl2}");
                Assert.InRange(pl1, caps.MinPl1, caps.MaxPl1);
                Assert.InRange(pl2, caps.MinPl1 + caps.Pl2MinOffset, caps.MaxPl2);
            }

            for (int v = caps.MinPl1 + caps.Pl2MinOffset; v <= caps.MaxPl2; v++)
            {
                int pl1 = 0, pl2 = v;
                caps.CoupleFromPl2(ref pl1, ref pl2);
                Assert.True(pl2 - pl1 >= caps.Pl2MinOffset, $"PL2={v} gave {pl1}/{pl2}");
                Assert.InRange(pl1, caps.MinPl1, caps.MaxPl1);
                Assert.InRange(pl2, caps.MinPl1 + caps.Pl2MinOffset, caps.MaxPl2);
            }
        }

        [Fact]
        public void Coupling_IsReversibleBelowTheKnee()
        {
            // Drive PL1 to 20, then drive PL2 back to what it produced: PL1 must return to 20.
            // Below the knee the two sliders are two views of one value, and a round trip that
            // drifted would let a user walk the pair somewhere by nudging back and forth.
            var caps = Claw8Ex();

            int pl1 = 20, pl2 = 0;
            caps.CoupleFromPl1(ref pl1, ref pl2);

            int backPl1 = 0, backPl2 = pl2;
            caps.CoupleFromPl2(ref backPl1, ref backPl2);

            Assert.Equal(20, backPl1);
            Assert.Equal(pl2, backPl2);
        }

        [Fact]
        public void CoupleFromPl1_ClosesAGapOpenedAtTheCeiling()
        {
            // 35/45 is the widened pair. Stepping PL1 down to 34 pulls PL2 back to 36 - the
            // documented consequence of "the sliders move together", and the only way to close a
            // gap once it has been opened.
            var caps = Claw8Ex();

            int pl1 = 34, pl2 = 45;
            caps.CoupleFromPl1(ref pl1, ref pl2);

            Assert.Equal(34, pl1);
            Assert.Equal(36, pl2);
        }

        [Fact]
        public void ClampForBattery_KeepsTheHeadroomRuleAfterCapping()
        {
            // The reason this is a clamp and not two Math.Min calls. Capping PL1 and PL2
            // independently could leave less than Pl2MinOffset between them, which is a pair the
            // firmware rejects - so every AC-valid input must still be valid after the DC cap.
            var caps = Claw8Ex();

            for (int pl1 = caps.MinPl1; pl1 <= caps.MaxPl1; pl1++)
            {
                for (int pl2 = pl1 + caps.Pl2MinOffset; pl2 <= caps.MaxPl2; pl2++)
                {
                    int dcPl1 = pl1, dcPl2 = pl2;
                    caps.ClampPowerLimitsForBattery(ref dcPl1, ref dcPl2);

                    Assert.True(dcPl2 - dcPl1 >= caps.Pl2MinOffset,
                        $"{pl1}/{pl2} W became {dcPl1}/{dcPl2} W, below the required headroom");
                    Assert.InRange(dcPl1, caps.MinPl1, caps.MaxPl1Dc);
                    Assert.InRange(dcPl2, caps.MinPl1, caps.MaxPl2Dc);
                }
            }
        }

        [Fact]
        public void ClampForBattery_NeverRaisesTheLimitAboveAc()
        {
            // A DC ceiling above the AC one is a bad capability value, not permission to draw
            // more power unplugged than plugged in.
            var caps = Claw8Ex();
            caps.MaxPl1Dc = 99;
            caps.MaxPl2Dc = 99;

            int pl1 = 35, pl2 = 45;
            caps.ClampPowerLimitsForBattery(ref pl1, ref pl2);

            Assert.Equal(35, pl1);
            Assert.Equal(45, pl2);
        }

        [Fact]
        public void ClampForBattery_IsNeverHigherThanTheAcClamp()
        {
            var caps = Claw8Ex();

            int acPl1 = 35, acPl2 = 45;
            caps.ClampPowerLimits(ref acPl1, ref acPl2);

            int dcPl1 = 35, dcPl2 = 45;
            caps.ClampPowerLimitsForBattery(ref dcPl1, ref dcPl2);

            Assert.True(dcPl1 <= acPl1);
            Assert.True(dcPl2 <= acPl2);
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

    public class PerfModeTests
    {
        [Fact]
        public void OrdinalsMatchTheWidgetDropdownOrder()
        {
            // The widget sets SelectedIndex from the enum value directly, so the dropdown order
            // in MainWidget.xaml is part of this contract: Endurance, User Scenario, AI Engine.
            Assert.Equal(0, (int)PerfMode.Endurance);
            Assert.Equal(1, (int)PerfMode.UserScenario);
            Assert.Equal(2, (int)PerfMode.AiEngine);
        }

        [Fact]
        public void UnknownIsOutsideTheDropdownRange()
        {
            // Unknown must never be a selectable index, or a mode we do not model would be
            // painted as one we do.
            Assert.True((int)PerfMode.Unknown > 2);
        }

        [Theory]
        [InlineData(PerfMode.Endurance)]
        [InlineData(PerfMode.UserScenario)]
        [InlineData(PerfMode.AiEngine)]
        [InlineData(PerfMode.Unknown)]
        public void RoundTripsThroughTheWire(PerfMode mode)
        {
            var wire = PipeEnvelope.FromEnum(mode);
            var envelope = new PipeEnvelope(1, Command.Response, Function.PerfMode, wire);

            Assert.Equal(mode, envelope.AsEnum(PerfMode.Unknown));
        }

        [Fact]
        public void PerfModeHasItsOwnOrdinalInTheTdpGroup()
        {
            // Ordinals are never reused. PerfMode belongs to the TDP group because it gates it.
            Assert.Equal(13, (int)Function.PerfMode);
            Assert.NotEqual((int)Function.Pl1, (int)Function.PerfMode);
            Assert.NotEqual((int)Function.Pl2, (int)Function.PerfMode);
            Assert.NotEqual((int)Function.TdpBackend, (int)Function.PerfMode);
        }
    }

    public class ChargeLevelsTests
    {
        [Theory]
        [InlineData(100, 100)]
        [InlineData(80, 80)]
        [InlineData(60, 60)]
        [InlineData(95, 100)]
        [InlineData(75, 80)]
        [InlineData(65, 60)]
        [InlineData(0, 60)]
        [InlineData(999, 100)]
        [InlineData(-40, 60)]
        public void Snap_RoundsToALevelTheDeviceCanHold(int input, int expected)
        {
            Assert.Equal(expected, ChargeLevels.Snap(input));
        }

        [Fact]
        public void Snap_BreaksATieTowardsTheLowerLevel()
        {
            // 70 and 90 are equidistant between two levels. The lower limit is the safer default
            // for the battery, so a tie must never round up.
            Assert.Equal(60, ChargeLevels.Snap(70));
            Assert.Equal(80, ChargeLevels.Snap(90));
        }

        [Fact]
        public void Snap_AlwaysReturnsASelectableLevel()
        {
            var levels = ChargeLevels.All();
            for (int i = -10; i <= 130; i++)
                Assert.Contains(ChargeLevels.Snap(i), levels);
        }

        [Fact]
        public void IndexRoundTripsForEveryLevel()
        {
            var levels = ChargeLevels.All();
            for (int i = 0; i < levels.Length; i++)
            {
                Assert.Equal(levels[i], ChargeLevels.FromIndex(i));
                Assert.Equal(i, ChargeLevels.ToIndex(levels[i]));
            }
        }

        [Fact]
        public void FromIndex_FallsBackToTheDefaultOutOfRange()
        {
            Assert.Equal(ChargeLevels.Default, ChargeLevels.FromIndex(-1));
            Assert.Equal(ChargeLevels.Default, ChargeLevels.FromIndex(7));
        }

        [Fact]
        public void LevelsAreEvenlySpacedSoASliderCanReachExactlyThem()
        {
            // The widget renders these as a Slider with Minimum 60, Maximum 100 and
            // StepFrequency 20. That only lands on the real levels if they are evenly spaced and
            // span the whole range - so this test is what keeps the XAML honest. Adding a level
            // (say 70) breaks the spacing and must fail here rather than silently produce a
            // control that can select a value the hardware cannot hold.
            var levels = ChargeLevels.All();

            Assert.Equal(60, levels[0]);
            Assert.Equal(100, levels[levels.Length - 1]);

            int step = levels[1] - levels[0];
            Assert.Equal(20, step);
            for (int i = 1; i < levels.Length; i++)
                Assert.Equal(step, levels[i] - levels[i - 1]);
        }

        [Fact]
        public void EverySliderStopSnapsToItself()
        {
            // Walking the slider's reachable positions must never move the value.
            for (int v = 60; v <= 100; v += 20)
                Assert.Equal(v, ChargeLevels.Snap(v));
        }

        [Fact]
        public void AllIsAscendingSoTheDropdownOrderIsStable()
        {
            var levels = ChargeLevels.All();
            for (int i = 1; i < levels.Length; i++)
                Assert.True(levels[i] > levels[i - 1]);
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
                Matches = false,
            };

            var parsed = FanState.Parse(state.Serialize());

            Assert.Equal(state.Table, parsed.Table);
            Assert.True(parsed.ControlEnabled);
            Assert.True(parsed.ReadOk);
            Assert.False(parsed.FullSpeed);
            Assert.Equal(3571, parsed.Rpm);
            Assert.Equal(state.Temps, parsed.Temps);
            Assert.False(parsed.Matches);
        }

        [Fact]
        public void Parse_DefaultsMatchesToTrueWhenAbsent()
        {
            // A payload without the trailing field must not be read as "mismatch" - that would
            // put a permanent warning under the fan card for a state nobody actually reported.
            var parsed = FanState.Parse("0,0,40,49,58,67,75,94|1|1|0|3571|44,54,64,74,82");

            Assert.True(parsed.Matches);
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
