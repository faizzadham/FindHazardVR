using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Convai.Infrastructure.Networking.Native;
using NUnit.Framework;
using Hold = Convai.Infrastructure.Networking.Native.AndroidCommunicationModeWorker.Hold;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public sealed class AndroidCommunicationModeWorkerTests
    {
        private const int ModeNormal = 0;
        private const int ModeInCommunication = AndroidCommunicationModeWorker.ModeInCommunication;
        private const int WaitMs = 5000;

        private readonly List<AndroidCommunicationModeWorker> _workers = new();

        [TearDown]
        public void TearDown()
        {
            foreach (AndroidCommunicationModeWorker worker in _workers)
                worker.Dispose();
            _workers.Clear();
        }

        [Test]
        public void Acquire_WaitsUntilTheModeReadsBack_ThenPublishesTheSettledRoute()
        {
            var audio = new FakeAudioManager { StaleReadsAfterSetMode = 3 };
            AndroidCommunicationModeWorker worker = CreateWorker(audio);

            Hold hold = worker.Acquire();

            Assert.That(hold.Settled.Wait(WaitMs), Is.True);
            Assert.That(hold.Settled.Result, Is.True);
            Assert.That(audio.Mode, Is.EqualTo(ModeInCommunication));
            Assert.That(audio.SpeakerphoneOn, Is.True);
            Assert.That(hold.TryGetRouteSignature(out int route), Is.True,
                "The settled route is published before Settled completes.");
            Assert.That(route, Is.EqualTo(ModeInCommunication << AndroidCommunicationModeWorker.RouteModeShift |
                                          AndroidCommunicationModeWorker.RouteSpeakerBit));
        }

        [Test]
        public void Acquire_WithAHeadsetConnected_LeavesTheSpeakerphoneAlone()
        {
            var audio = new FakeAudioManager { WiredHeadsetOn = true };
            AndroidCommunicationModeWorker worker = CreateWorker(audio);

            Hold hold = worker.Acquire();

            Assert.That(hold.Settled.Wait(WaitMs) && hold.Settled.Result, Is.True);
            Assert.That(audio.Mode, Is.EqualTo(ModeInCommunication));
            Assert.That(audio.CountCalls("SetSpeakerphoneOn"), Is.Zero);
        }

        [Test]
        public void Release_RestoresThePreviousModeAndSpeakerphoneAfterTheDelay()
        {
            var audio = new FakeAudioManager();
            AndroidCommunicationModeWorker worker = CreateWorker(audio, restoreDelayMs: 50);
            Hold hold = worker.Acquire();
            Assert.That(hold.Settled.Wait(WaitMs), Is.True);

            hold.Release();

            Assert.That(WaitFor(() => audio.Mode == ModeNormal), Is.True);
            Assert.That(audio.SpeakerphoneOn, Is.False);
            Assert.That(hold.TryGetRouteSignature(out _), Is.False);
        }

        [Test]
        public void StopThenStartWithinTheRestoreDelay_KeepsTheModeWithoutSwitchingAgain()
        {
            var audio = new FakeAudioManager();
            AndroidCommunicationModeWorker worker = CreateWorker(audio, restoreDelayMs: 300);
            Hold first = worker.Acquire();
            Assert.That(first.Settled.Wait(WaitMs), Is.True);

            first.Release();
            Hold second = worker.Acquire();

            Assert.That(second.Settled.Wait(WaitMs) && second.Settled.Result, Is.True);
            Assert.That(audio.CountCalls("SetMode"), Is.EqualTo(1));
            Assert.That(audio.Mode, Is.EqualTo(ModeInCommunication));

            second.Release();
            Assert.That(WaitFor(() => audio.Mode == ModeNormal), Is.True);
            Assert.That(audio.CountCalls("SetMode"), Is.EqualTo(2));
        }

        [Test]
        public void OverlappingHolds_RestoreOnlyAfterTheLastRelease()
        {
            var audio = new FakeAudioManager();
            AndroidCommunicationModeWorker worker = CreateWorker(audio, restoreDelayMs: 20);
            Hold first = worker.Acquire();
            Hold second = worker.Acquire();
            Assert.That(second.Settled.Wait(WaitMs), Is.True);

            first.Release();
            Thread.Sleep(200);
            Assert.That(audio.Mode, Is.EqualTo(ModeInCommunication));

            second.Release();
            Assert.That(WaitFor(() => audio.Mode == ModeNormal), Is.True);
        }

        [Test]
        public void ReleaseBeforeTheAcquireRuns_NeverTouchesTheAudioManager()
        {
            var audio = new FakeAudioManager();
            using var attachGate = new ManualResetEventSlim(false);
            AndroidCommunicationModeWorker worker = CreateWorker(audio, attach: () =>
            {
                attachGate.Wait(WaitMs);
                return true;
            });

            Hold hold = worker.Acquire();
            hold.Release();
            attachGate.Set();

            Assert.That(hold.Settled.Wait(WaitMs), Is.True);
            Assert.That(hold.Settled.Result, Is.False);
            Hold probe = worker.Acquire();
            Assert.That(probe.Settled.Wait(WaitMs), Is.True);
            Assert.That(audio.CountCalls("SetMode"), Is.EqualTo(1), "Only the probe entered the mode.");
        }

        [Test]
        public void AttachFailure_SettlesAsNotAppliedWithoutTouchingTheAudioManager()
        {
            var audio = new FakeAudioManager();
            AndroidCommunicationModeWorker worker = CreateWorker(audio, attach: () => false);

            Hold first = worker.Acquire();
            Assert.That(first.Settled.Wait(WaitMs), Is.True);
            Hold second = worker.Acquire();

            Assert.That(first.Settled.Result, Is.False);
            Assert.That(second.Settled.IsCompleted && !second.Settled.Result, Is.True);
            Assert.That(audio.CallCount, Is.Zero);
        }

        [Test]
        public void RefusedModeChange_SettlesAsNotAppliedAfterTheTimeout()
        {
            var audio = new FakeAudioManager { RefuseSetMode = true };
            AndroidCommunicationModeWorker worker = CreateWorker(audio, settleTimeoutMs: 100);

            Hold hold = worker.Acquire();

            Assert.That(hold.Settled.Wait(WaitMs), Is.True);
            Assert.That(hold.Settled.Result, Is.False);
            Assert.That(hold.TryGetRouteSignature(out int route), Is.True);
            Assert.That(route >> AndroidCommunicationModeWorker.RouteModeShift, Is.EqualTo(ModeNormal));
        }

        [Test]
        public void RouteReads_RunOnTheWorkerAndReportAHeadsetChange()
        {
            var audio = new FakeAudioManager();
            AndroidCommunicationModeWorker worker = CreateWorker(audio);
            Hold hold = worker.Acquire();
            Assert.That(hold.Settled.Wait(WaitMs), Is.True);

            audio.WiredHeadsetOn = true;

            Assert.That(WaitFor(() =>
                hold.TryGetRouteSignature(out int route) &&
                (route & AndroidCommunicationModeWorker.RouteWiredBit) != 0), Is.True);
            Assert.That(audio.CallsOffThread(Thread.CurrentThread.ManagedThreadId), Is.EqualTo(audio.CallCount),
                "No AudioManager call runs on the caller's thread.");
        }

        [Test]
        public void AcquireRightAfterARestore_SavesTheRestoredModeNotItsOwn()
        {
            // getMode keeps reporting the old mode for a while after setMode, in both directions.
            var audio = new FakeAudioManager { StaleReadsAfterSetMode = 3 };
            AndroidCommunicationModeWorker worker = CreateWorker(audio, restoreDelayMs: 10);
            Hold first = worker.Acquire();
            Assert.That(first.Settled.Wait(WaitMs), Is.True);
            first.Release();
            Assert.That(WaitFor(() => audio.CountCalls("SetMode") == 2), Is.True);

            Hold second = worker.Acquire();
            Assert.That(second.Settled.Wait(WaitMs) && second.Settled.Result, Is.True);
            second.Release();

            Assert.That(WaitFor(() => audio.CountCalls("SetMode") == 4), Is.True);
            Assert.That(WaitFor(() => audio.Mode == ModeNormal), Is.True);
            Assert.That(audio.LastSetModeValue, Is.EqualTo(ModeNormal));
        }

        [Test]
        public void SpeakerphoneDroppedWhileTheModeWasKept_IsForcedAgainOnTheNextAcquire()
        {
            var audio = new FakeAudioManager();
            AndroidCommunicationModeWorker worker = CreateWorker(audio, restoreDelayMs: 300);
            Hold first = worker.Acquire();
            Assert.That(first.Settled.Wait(WaitMs), Is.True);

            audio.SpeakerphoneOn = false;
            first.Release();
            Hold second = worker.Acquire();

            Assert.That(second.Settled.Wait(WaitMs) && second.Settled.Result, Is.True);
            Assert.That(audio.SpeakerphoneOn, Is.True);
            Assert.That(audio.CountCalls("SetSpeakerphoneOn"), Is.EqualTo(2));
        }

        [Test]
        public void FailedRestore_KeepsTheSavedStateAndIsRetriedOnTheNextRelease()
        {
            var audio = new FakeAudioManager();
            AndroidCommunicationModeWorker worker = CreateWorker(audio, settleTimeoutMs: 50, restoreDelayMs: 10);
            Hold first = worker.Acquire();
            Assert.That(first.Settled.Wait(WaitMs), Is.True);

            audio.ThrowOnSetSpeakerphoneOn = true;
            audio.RefuseSetMode = true;
            first.Release();
            Assert.That(WaitFor(() => audio.CountCalls("SetMode") == 2), Is.True,
                "The mode restore still runs when the speakerphone restore throws.");
            audio.ThrowOnSetSpeakerphoneOn = false;
            audio.RefuseSetMode = false;

            // Queued behind the restore, which times out still reading communication mode.
            Hold second = worker.Acquire();
            Assert.That(second.Settled.Wait(WaitMs) && second.Settled.Result, Is.True);
            second.Release();

            Assert.That(WaitFor(() => audio.Mode == ModeNormal && !audio.SpeakerphoneOn), Is.True);
            Assert.That(audio.SetModeValues,
                Is.EqualTo(new[] { ModeInCommunication, ModeNormal, ModeInCommunication, ModeNormal }),
                "The mode is entered again after an unconfirmed restore, and the original mode is restored.");
        }

        [Test]
        public void ForcedSpeakerphoneThatNeverReadsBack_DoesNotHoldTheSettleForTheModeTimeout()
        {
            var audio = new FakeAudioManager { RefuseSetSpeakerphoneOn = true };
            AndroidCommunicationModeWorker worker = CreateWorker(audio, settleTimeoutMs: 10000);

            Hold hold = worker.Acquire();

            Assert.That(hold.Settled.Wait(WaitMs), Is.True);
            Assert.That(hold.Settled.Result, Is.True);
            Assert.That(audio.Mode, Is.EqualTo(ModeInCommunication));
        }

        private AndroidCommunicationModeWorker CreateWorker(FakeAudioManager audio, Func<bool> attach = null,
            int settleTimeoutMs = 2000, int restoreDelayMs = 1500)
        {
            var worker = new AndroidCommunicationModeWorker(audio, attach ?? (() => true), () => { },
                settleTimeoutMs, settlePollMs: 1, restoreDelayMs: restoreDelayMs);
            _workers.Add(worker);
            return worker;
        }

        private static bool WaitFor(Func<bool> condition)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (!condition())
            {
                if (elapsed.ElapsedMilliseconds > WaitMs)
                    return false;
                Thread.Sleep(5);
            }

            return true;
        }

        private sealed class FakeAudioManager : IAndroidAudioManager
        {
            private readonly object _gate = new();
            private readonly List<(string Name, int ThreadId)> _calls = new();
            private readonly List<int> _setModeValues = new();
            private int _mode;
            private int _reportedMode;
            private int _staleReadsLeft;
            private bool _speakerphoneOn;
            private bool _wiredHeadsetOn;

            public int StaleReadsAfterSetMode { get; set; }
            public bool RefuseSetMode { get; set; }
            public bool RefuseSetSpeakerphoneOn { get; set; }
            public bool ThrowOnSetSpeakerphoneOn { get; set; }
            public int LastSetModeValue { get; private set; } = -1;

            public int[] SetModeValues
            {
                get { lock (_gate) return _setModeValues.ToArray(); }
            }

            public int Mode
            {
                get { lock (_gate) return _mode; }
            }

            public bool SpeakerphoneOn
            {
                get { lock (_gate) return _speakerphoneOn; }
                set { lock (_gate) _speakerphoneOn = value; }
            }

            public bool WiredHeadsetOn
            {
                get { lock (_gate) return _wiredHeadsetOn; }
                set { lock (_gate) _wiredHeadsetOn = value; }
            }

            public int CallCount
            {
                get { lock (_gate) return _calls.Count; }
            }

            public int CountCalls(string name)
            {
                lock (_gate)
                {
                    int count = 0;
                    foreach ((string Name, int ThreadId) call in _calls)
                        if (call.Name == name)
                            count++;
                    return count;
                }
            }

            public int CallsOffThread(int threadId)
            {
                lock (_gate)
                {
                    int count = 0;
                    foreach ((string Name, int ThreadId) call in _calls)
                        if (call.ThreadId != threadId)
                            count++;
                    return count;
                }
            }

            public int GetMode()
            {
                lock (_gate)
                {
                    Record(nameof(GetMode));
                    if (_staleReadsLeft > 0)
                    {
                        _staleReadsLeft--;
                        return _reportedMode;
                    }

                    return _mode;
                }
            }

            public void SetMode(int mode)
            {
                lock (_gate)
                {
                    Record(nameof(SetMode));
                    _setModeValues.Add(mode);
                    LastSetModeValue = mode;
                    if (RefuseSetMode)
                        return;

                    _reportedMode = _mode;
                    _staleReadsLeft = StaleReadsAfterSetMode;
                    _mode = mode;
                }
            }

            public bool IsSpeakerphoneOn()
            {
                lock (_gate)
                {
                    Record(nameof(IsSpeakerphoneOn));
                    return _speakerphoneOn;
                }
            }

            public void SetSpeakerphoneOn(bool on)
            {
                lock (_gate)
                {
                    Record(nameof(SetSpeakerphoneOn));
                    if (ThrowOnSetSpeakerphoneOn)
                        throw new InvalidOperationException("setSpeakerphoneOn failed");
                    if (!RefuseSetSpeakerphoneOn)
                        _speakerphoneOn = on;
                }
            }

            public bool IsBluetoothScoOn()
            {
                lock (_gate)
                {
                    Record(nameof(IsBluetoothScoOn));
                    return false;
                }
            }

            public bool IsBluetoothA2dpOn()
            {
                lock (_gate)
                {
                    Record(nameof(IsBluetoothA2dpOn));
                    return false;
                }
            }

            public bool IsWiredHeadsetOn()
            {
                lock (_gate)
                {
                    Record(nameof(IsWiredHeadsetOn));
                    return _wiredHeadsetOn;
                }
            }

            private void Record(string name) => _calls.Add((name, Thread.CurrentThread.ManagedThreadId));
        }
    }
}
