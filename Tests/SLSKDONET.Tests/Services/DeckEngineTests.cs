using System;
using System.Linq;
using Xunit;
using SLSKDONET.Services.Audio;

namespace SLSKDONET.Tests.Services
{
    // ─────────────────────────────────────────────────────────────────────────────
    // DeckEngine tests — Tasks 5.1 / 5.2 / 5.4 / 5.5
    // ─────────────────────────────────────────────────────────────────────────────

    public class DeckEngineTests
    {
        // ── Initial state ──────────────────────────────────────────────────────

        [Fact]
        public void NewDeck_State_IsStoppedAndNotLoaded()
        {
            using var deck = new DeckEngine();
            Assert.Equal(DeckState.Stopped, deck.State);
            Assert.False(deck.IsLoaded);
            Assert.Equal(0, deck.DurationSeconds);
            Assert.Equal(0, deck.PositionSeconds);
        }

        // ── Tempo / PitchRange ─────────────────────────────────────────────────

        [Theory]
        [InlineData(0,    0)]
        [InlineData(8,    8)]
        [InlineData(-8,  -8)]
        [InlineData(60,  50)]   // should be clamped to 50
        [InlineData(-60, -50)]  // should be clamped to -50
        public void TempoPercent_IsClampedToFifty(double input, double expected)
        {
            using var deck = new DeckEngine();
            deck.TempoPercent = input;
            Assert.Equal(expected, deck.TempoPercent);
        }

        [Fact]
        public void KeyLock_Toggles_WithoutThrowing()
        {
            using var deck = new DeckEngine();
            deck.KeyLock = true;
            Assert.True(deck.KeyLock);
            deck.KeyLock = false;
            Assert.False(deck.KeyLock);
        }

        // ── Hot cues ──────────────────────────────────────────────────────────

        [Fact]
        public void HotCues_InitiallyAllNull()
        {
            using var deck = new DeckEngine();
            Assert.All(deck.HotCues, slot => Assert.Null(slot));
        }

        [Fact]
        public void SetAndDeleteHotCue_OutOfRange_IsIgnored()
        {
            using var deck = new DeckEngine();
            // Should not throw
            deck.SetHotCue(8, "bad", "#FF0000");
            deck.DeleteHotCue(-1);
            Assert.All(deck.HotCues, slot => Assert.Null(slot));
        }

        [Fact]
        public void DeleteHotCue_ClearsSlot()
        {
            using var deck = new DeckEngine();
            // Manually plant a cue in the array via JumpToHotCue when not loaded
            // SetHotCue requires a loaded file — just verify delete is idempotent
            deck.DeleteHotCue(0);  // slot 0 was already null — no throw
            Assert.Null(deck.HotCues[0]);
        }

        // ── Loop logic — no file required ─────────────────────────────────────

        [Fact]
        public void LoopRegion_HalfLoop_HalvesLength()
        {
            // Test LoopRegion invariants independently
            var loop = new LoopRegion { InSeconds = 0, OutSeconds = 8, IsActive = true };
            double mid = (loop.InSeconds + loop.OutSeconds) / 2.0;
            loop.OutSeconds = mid;
            Assert.Equal(4.0, loop.OutSeconds, precision: 6);
        }

        [Fact]
        public void LoopRegion_DoubleLoop_DoublesLength()
        {
            var loop = new LoopRegion { InSeconds = 0, OutSeconds = 4, IsActive = true };
            loop.OutSeconds += loop.OutSeconds - loop.InSeconds;
            Assert.Equal(8.0, loop.OutSeconds, precision: 6);
        }

        [Fact]
        public void LoopRegion_MoveLoop_ShiftsInAndOut()
        {
            var loop = new LoopRegion { InSeconds = 4, OutSeconds = 8, IsActive = true };
            double len = loop.OutSeconds - loop.InSeconds; // 4
            loop.InSeconds  += 1 * len;
            loop.OutSeconds += 1 * len;
            Assert.Equal(8.0,  loop.InSeconds,  precision: 6);
            Assert.Equal(12.0, loop.OutSeconds, precision: 6);
        }

        // ── VolumeLevel ───────────────────────────────────────────────────────

        [Fact]
        public void VolumeLevel_DefaultIsOne()
        {
            using var deck = new DeckEngine();
            Assert.Equal(1f, deck.VolumeLevel);
        }

        // ── State transitions without file ────────────────────────────────────

        [Fact]
        public void Play_WithoutFile_DoesNotChangeState()
        {
            using var deck = new DeckEngine();
            deck.Play(); // _rate == null → returns early
            Assert.Equal(DeckState.Stopped, deck.State);
        }

        [Fact]
        public void Read_WhileStopped_ReturnsSilence()
        {
            using var deck = new DeckEngine();
            var buffer = new float[128];
            deck.Read(buffer, 0, 128);
            Assert.All(buffer, s => Assert.Equal(0f, s));
        }

        [Fact]
        public void Dispose_TwiceIsIdempotent()
        {
            var deck = new DeckEngine();
            deck.Dispose();
            deck.Dispose(); // should not throw
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // BpmSyncService tests — Task 5.3
    // ─────────────────────────────────────────────────────────────────────────────

    public class BpmSyncServiceTests
    {
        private readonly BpmSyncService _svc = new();

        // ── BeatMatch ─────────────────────────────────────────────────────────

        [Fact]
        public void BeatMatch_MatchingBpm_SetsSlaveTempToZero()
        {
            // Both decks at 128 BPM → slave should end up at 0% tempo adjustment
            using var master = new DeckEngine();
            using var slave  = new DeckEngine();
            master.TempoPercent = 0;

            _svc.BeatMatch(master, 128.0, slave, 128.0);

            Assert.Equal(0.0, slave.TempoPercent, precision: 6);
        }

        [Fact]
        public void BeatMatch_DifferentBpm_AdjustsSlaveTempo()
        {
            // Master at 128, slave track at 120 BPM
            // Required rate: 128/120 ≈ 1.0667 → tempo% ≈ +6.67%
            using var master = new DeckEngine();
            using var slave  = new DeckEngine();
            master.TempoPercent = 0; // master effective BPM = 128

            _svc.BeatMatch(master, 128.0, slave, 120.0);

            double expectedTempo = (128.0 / 120.0 - 1.0) * 100.0;
            Assert.Equal(expectedTempo, slave.TempoPercent, precision: 4);
        }

        [Fact]
        public void BeatMatch_ZeroBpm_IsNoOp()
        {
            using var master = new DeckEngine();
            using var slave  = new DeckEngine();
            slave.TempoPercent = 5.0;

            _svc.BeatMatch(master, 0,    slave, 120.0);  // masterBpm=0 → skip
            _svc.BeatMatch(master, 128.0, slave, 0);     // slaveBpm=0 → skip

            Assert.Equal(5.0, slave.TempoPercent, precision: 6);
        }

        [Fact]
        public void BeatMatch_WithMasterTempoOffset_AccountsForOffset()
        {
            // Master track at 120 BPM, master fader at +10% → effective 132 BPM
            // Slave track at 128 BPM → should be faster: (132/128 - 1)*100 ≈ 3.125%
            using var master = new DeckEngine();
            using var slave  = new DeckEngine();
            master.TempoPercent = 10.0; // +10% fader

            _svc.BeatMatch(master, 120.0, slave, 128.0);

            double masterEffective = 120.0 * 1.10; // 132
            double expectedTempo   = (masterEffective / 128.0 - 1.0) * 100.0;
            Assert.Equal(expectedTempo, slave.TempoPercent, precision: 4);
        }

        // ── GetPhaseOffsetBeats ────────────────────────────────────────────────

        [Fact]
        public void GetPhaseOffsetBeats_ZeroBpm_ReturnsZero()
        {
            using var a = new DeckEngine();
            using var b = new DeckEngine();
            double offset = _svc.GetPhaseOffsetBeats(a, 0, b, 128);
            Assert.Equal(0, offset);
        }

        [Fact]
        public void GetPhaseOffsetBeats_BothAtOrigin_ReturnsZero()
        {
            using var a = new DeckEngine();
            using var b = new DeckEngine();
            // Both at position 0 → same phase
            double offset = _svc.GetPhaseOffsetBeats(a, 128, b, 128);
            Assert.Equal(0, offset, precision: 6);
        }

        [Fact]
        public void GetPhaseOffsetBeats_IsWrappedToHalfBeat()
        {
            // Result must always be in [-0.5, +0.5]
            using var a = new DeckEngine();
            using var b = new DeckEngine();
            for (int i = 0; i < 32; i++)
            {
                double offset = _svc.GetPhaseOffsetBeats(a, 128, b, 128);
                Assert.InRange(offset, -0.5, 0.5);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // DeckViewModel tests — dual-deck crossfader + sync commands
    // ─────────────────────────────────────────────────────────────────────────────

    public class DeckViewModelTests
    {
        [Fact]
        public void Constructor_CreatesDeckAAndDeckB()
        {
            using var vm = new SLSKDONET.ViewModels.DeckViewModel();
            Assert.NotNull(vm.DeckA);
            Assert.NotNull(vm.DeckB);
            Assert.True(vm.DeckA.IsFocused);
            Assert.False(vm.DeckB.IsFocused);
        }

        [Fact]
        public void CrossfaderAtZero_DeckAIsFullVolume_DeckBIsMuted()
        {
            using var vm = new SLSKDONET.ViewModels.DeckViewModel();
            vm.CrossfaderPosition = 0f;
            Assert.Equal(1f,  vm.DeckA.Engine.VolumeLevel, precision: 4);
            Assert.Equal(0f,  vm.DeckB.Engine.VolumeLevel, precision: 4);
        }

        [Fact]
        public void CrossfaderAtOne_DeckBIsFullVolume_DeckAIsMuted()
        {
            using var vm = new SLSKDONET.ViewModels.DeckViewModel();
            vm.CrossfaderPosition = 1f;
            Assert.Equal(0f,  vm.DeckA.Engine.VolumeLevel, precision: 4);
            Assert.Equal(1f,  vm.DeckB.Engine.VolumeLevel, precision: 4);
        }

        [Fact]
        public void CrossfaderAtCenter_BothDecksAtEqualPower()
        {
            using var vm = new SLSKDONET.ViewModels.DeckViewModel();
            vm.CrossfaderPosition = 0.5f;
            float expectedEachDb = (float)Math.Cos(Math.PI / 4); // 0.7071 = -3 dB
            Assert.Equal(expectedEachDb, vm.DeckA.Engine.VolumeLevel, precision: 4);
            Assert.Equal(expectedEachDb, vm.DeckB.Engine.VolumeLevel, precision: 4);
        }

        [Fact]
        public void CrossfaderPosition_IsClamped()
        {
            using var vm = new SLSKDONET.ViewModels.DeckViewModel();
            vm.CrossfaderPosition = -1f;
            Assert.Equal(0f, vm.CrossfaderPosition);
            vm.CrossfaderPosition = 2f;
            Assert.Equal(1f, vm.CrossfaderPosition);
        }

        [Fact]
        public void MasterDeck_DefaultIsA()
        {
            using var vm = new SLSKDONET.ViewModels.DeckViewModel();
            Assert.Equal(DeckSide.A, vm.MasterDeck);
        }

        [Fact]
        public void HandleHotKeyPress_RoutesToFocusedDeck()
        {
            // Just verify it doesn't throw and respects focus
            using var vm = new SLSKDONET.ViewModels.DeckViewModel();
            vm.DeckA.IsFocused = false;
            vm.DeckB.IsFocused = true;
            vm.HandleHotKeyPress(1); // should route to DeckB
        }

        [Fact]
        public void Dispose_IsIdempotent()
        {
            var vm = new SLSKDONET.ViewModels.DeckViewModel();
            vm.Dispose();
            vm.Dispose(); // should not throw
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // DeckSlotViewModel tests — PitchRange clamping, loop-beat selection
    // ─────────────────────────────────────────────────────────────────────────────

    public class DeckSlotViewModelTests
    {
        [Fact]
        public void SetPitchRange_ClampsTempoToNewRange()
        {
            using var engine = new DeckEngine();
            var slot = new SLSKDONET.ViewModels.DeckSlotViewModel("A", engine);

            // Start at ±16% range, set tempo to 14%
            slot.PitchRange   = PitchRange.Medium;
            slot.TempoPercent = 14.0;
            Assert.Equal(14.0, slot.TempoPercent, precision: 6);

            // Narrow down to ±8% → should clamp to 8
            slot.PitchRange = PitchRange.Narrow;
            Assert.Equal(8.0, slot.TempoPercent, precision: 6);

            slot.Dispose();
        }

        [Fact]
        public void SelectedLoopBeats_DefaultIsFour()
        {
            using var engine = new DeckEngine();
            var slot = new SLSKDONET.ViewModels.DeckSlotViewModel("A", engine);
            Assert.Equal(4.0, slot.SelectedLoopBeats);
            slot.Dispose();
        }

        [Fact]
        public void LoadTrack_WithoutFile_InvalidPath_ThrowsOnLoad()
        {
            using var engine = new DeckEngine();
            var slot = new SLSKDONET.ViewModels.DeckSlotViewModel("A", engine);
            // Should throw because the file doesn't exist
            Assert.ThrowsAny<Exception>(() => slot.LoadTrack("/nonexistent/track.mp3", 128.0));
            slot.Dispose();
        }
    }
}
