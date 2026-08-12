using McenterLite.Shared.Ipc;
using McenterLite.Shared.Model;
using System.Collections.Generic;
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
            HasHwMouse = true,
            HasIgcl = true,
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
            Assert.True(parsed.HasIgcl);
        }

        [Theory]
        // Only PL2's starting value matters here - PL1's is replaced by the value being requested.
        //
        // PL1 moves on its own while there is headroom above it. This is the whole point: PL2 is
        // an independent value, so it keeps whatever gap the user opened.
        [InlineData(45, 20, 20, 45)]
        [InlineData(45, 34, 34, 45)]   // and downwards too - the gap WIDENS rather than closing
        [InlineData(30, 27, 27, 30)]
        // PL1 rising into PL2 pushes PL2 up rather than being blocked below it. Honouring the
        // requested PL1 is what makes "raise the sustained limit" possible from a minimum pair.
        [InlineData(10, 20, 20, 22)]
        [InlineData(22, 21, 21, 23)]
        [InlineData(10, 35, 35, 37)]
        // Out of range: PL1 is capped at its own ceiling, and PL2 comes along only as far as
        // the rule requires.
        [InlineData(10, 99, 35, 37)]
        [InlineData(45, 0, 8, 45)]
        public void ConstrainFromPl1_MovesPl2OnlyWhenTheHeadroomDemandsIt(
            int startPl2, int requestedPl1, int expectedPl1, int expectedPl2)
        {
            var caps = Claw8Ex();
            int pl1 = requestedPl1, pl2 = startPl2;
            caps.ConstrainFromPl1(ref pl1, ref pl2);

            Assert.Equal(expectedPl1, pl1);
            Assert.Equal(expectedPl2, pl2);
        }

        [Theory]
        // Mirror of the above: only PL1's starting value matters.
        //
        // PL2 moves on its own while it stays clear of PL1.
        [InlineData(20, 40, 20, 40)]
        [InlineData(20, 30, 20, 30)]
        [InlineData(8, 10, 8, 10)]
        // PL2 descending into PL1 pulls PL1 down with it - the mirror of the push above.
        [InlineData(35, 36, 34, 36)]
        [InlineData(35, 20, 18, 20)]
        // Out of range.
        [InlineData(20, 99, 20, 45)]
        [InlineData(20, 0, 8, 10)]
        public void ConstrainFromPl2_MovesPl1OnlyWhenTheHeadroomDemandsIt(
            int startPl1, int requestedPl2, int expectedPl1, int expectedPl2)
        {
            var caps = Claw8Ex();
            int pl1 = startPl1, pl2 = requestedPl2;
            caps.ConstrainFromPl2(ref pl1, ref pl2);

            Assert.Equal(expectedPl1, pl1);
            Assert.Equal(expectedPl2, pl2);
        }

        [Fact]
        public void Constraining_HoldsTheHeadroomFromEveryPairToEveryTarget()
        {
            // Whichever slider is driven, from wherever the pair currently sits, and wherever it
            // lands, the result must be a pair the firmware accepts. That is the invariant the two
            // sliders exist to preserve, and with the limits independent the starting pair is now
            // part of the input - so this sweeps every valid start against every target rather
            // than driving one slider from a fixed seed.
            var caps = Claw8Ex();

            for (int startPl1 = caps.MinPl1; startPl1 <= caps.MaxPl1; startPl1++)
            {
                for (int startPl2 = startPl1 + caps.Pl2MinOffset; startPl2 <= caps.MaxPl2; startPl2++)
                {
                    for (int target = caps.MinPl1; target <= caps.MaxPl2; target++)
                    {
                        int pl1 = target, pl2 = startPl2;
                        caps.ConstrainFromPl1(ref pl1, ref pl2);
                        AssertValidPair(caps, pl1, pl2, $"PL1 {startPl1}/{startPl2} -> {target}");

                        pl1 = startPl1;
                        pl2 = target;
                        caps.ConstrainFromPl2(ref pl1, ref pl2);
                        AssertValidPair(caps, pl1, pl2, $"PL2 {startPl1}/{startPl2} -> {target}");
                    }
                }
            }
        }

        [Fact]
        public void Constraining_LeavesTheUntouchedLimitAloneWhereverItLegallyCan()
        {
            // The independence itself, stated as an invariant rather than at sample points: the
            // limit the user did NOT move may only change when leaving it put would break the
            // headroom rule. Without this a regression back to rigid coupling still satisfies
            // every "is the pair valid" assertion above.
            var caps = Claw8Ex();

            for (int startPl1 = caps.MinPl1; startPl1 <= caps.MaxPl1; startPl1++)
            {
                for (int startPl2 = startPl1 + caps.Pl2MinOffset; startPl2 <= caps.MaxPl2; startPl2++)
                {
                    for (int target = caps.MinPl1; target <= caps.MaxPl2; target++)
                    {
                        int pl1 = target, pl2 = startPl2;
                        caps.ConstrainFromPl1(ref pl1, ref pl2);
                        if (startPl2 - pl1 >= caps.Pl2MinOffset)
                        {
                            Assert.True(pl2 == startPl2,
                                $"PL1 {startPl1}/{startPl2} -> {target} moved PL2 to {pl2} with no need to");
                        }

                        pl1 = startPl1;
                        pl2 = target;
                        caps.ConstrainFromPl2(ref pl1, ref pl2);
                        if (pl2 - startPl1 >= caps.Pl2MinOffset)
                        {
                            Assert.True(pl1 == startPl1,
                                $"PL2 {startPl1}/{startPl2} -> {target} moved PL1 to {pl1} with no need to");
                        }
                    }
                }
            }
        }

        [Fact]
        public void Constraining_IsReversible()
        {
            // Push PL1 up until it carries PL2, then drag PL2 back down: PL1 must come back to
            // exactly where it started. A round trip that drifted would let a user walk the pair
            // somewhere by nudging back and forth.
            var caps = Claw8Ex();

            int pl1 = 20, pl2 = 22;

            caps.ConstrainFromPl1(ref pl1, ref pl2);
            Assert.Equal(20, pl1);
            Assert.Equal(22, pl2);

            pl1 = 30;
            caps.ConstrainFromPl1(ref pl1, ref pl2);
            Assert.Equal(30, pl1);
            Assert.Equal(32, pl2);

            pl2 = 22;
            caps.ConstrainFromPl2(ref pl1, ref pl2);
            Assert.Equal(20, pl1);
            Assert.Equal(22, pl2);
        }

        [Fact]
        public void ConstrainFromPl1_PreservesAWidenedGap()
        {
            // 35/45 is the widest legal pair. Stepping PL1 down to 34 must leave PL2 at 45.
            //
            // This test is the INVERSE of the one it replaced, which asserted 34/36 - the old rigid
            // coupling dragged PL2 down to PL1 + 2 and there was no way to keep a gap open. If this
            // ever fails with 36, the coupling has come back.
            var caps = Claw8Ex();

            int pl1 = 34, pl2 = 45;
            caps.ConstrainFromPl1(ref pl1, ref pl2);

            Assert.Equal(34, pl1);
            Assert.Equal(45, pl2);
        }

        [Fact]
        public void ClampPowerLimits_PreservesAWidenedGap()
        {
            // The helper re-clamps every pair the widget sends, so a clamp that closed gaps would
            // undo the independence on the way through the pipe no matter what the widget computed.
            var caps = Claw8Ex();

            int pl1 = 10, pl2 = 45;
            caps.ClampPowerLimits(ref pl1, ref pl2);

            Assert.Equal(10, pl1);
            Assert.Equal(45, pl2);
        }

        private static void AssertValidPair(DeviceCaps caps, int pl1, int pl2, string because)
        {
            Assert.True(pl2 - pl1 >= caps.Pl2MinOffset, $"{because} gave {pl1}/{pl2}");
            Assert.InRange(pl1, caps.MinPl1, caps.MaxPl1);
            Assert.InRange(pl2, caps.MinPl1 + caps.Pl2MinOffset, caps.MaxPl2);
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
}
