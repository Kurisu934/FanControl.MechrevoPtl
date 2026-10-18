using System;
using System.Collections.Generic;
using MechrevoPtl;

public static class SessionTests
{
    private sealed class Fake : IEcHardware
    {
        public int Sets, Reads, Restores, Disposals;
        public bool FailSet, FailRead;
        public int FailRestoreCount, UnstableReads;
        public (int, int) ReadFans()
        {
            Reads++;
            if (FailRead) throw new TimeoutException("read timeout");
            if (UnstableReads-- > 0) throw new RpmSampleUnavailableException();
            return (4000, 6000);
        }
        public int SetPwm(double p)
        {
            Sets++;
            if (FailSet) throw new TimeoutException("partial write");
            return (int)Math.Round(p * 255 / 100, MidpointRounding.AwayFromZero);
        }
        public void RestoreAutomatic()
        {
            Restores++;
            if (FailRestoreCount-- > 0) throw new TimeoutException("restore timeout");
        }
        public void Dispose() { Disposals++; }
    }
    private static FanSession Session(Fake h) => new(h, _ => { });
    private static void Check(bool b, string message) { if (!b) throw new Exception(message); }
    private static void Throws<T>(Action a) where T : Exception
    {
        try { a(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    public static string[] Run()
    {
        var passed = new List<string>();
        void Test(string name, Action test) { test(); passed.Add(name); }
        Test("Read-only session never writes", () => {
            var h = new Fake(); var s = Session(h);
            s.Update(); s.Reset(); s.Dispose(); s.Dispose();
            Check(h.Sets == 0 && h.Restores == 0 && h.Disposals == 1, "read-only lifecycle wrote EC");
        });
        Test("PWM quantization, RPM readings and disable", () => {
            var h = new Fake(); using var s = Session(h);
            s.Set(50); s.Update();
            Check(Math.Abs(s.Pwm!.Value - 128 * 100f / 255) < 0.0001, "PWM value");
            Check(s.Fan0 == 4000 && s.Fan1 == 6000, "RPM values");
            s.Reset(); s.Reset();
            Check(s.Pwm == null && h.Restores == 1, "Reset must restore once");
        });
        Test("Close restores active control once", () => {
            var h = new Fake(); var s = Session(h);
            s.Set(75); s.Dispose(); s.Dispose(); s.Set(50);
            Check(h.Restores == 1 && h.Disposals == 1 && h.Sets == 1, "close lifecycle");
        });
        Test("Partially failed Set restores and rejects further writes", () => {
            var h = new Fake { FailSet = true }; using var s = Session(h);
            Throws<TimeoutException>(() => s.Set(50));
            for (int tick = 0; tick < 30; tick++) s.Set(75);
            s.Update();
            Check(s.Faulted && s.Pwm == null && h.Sets == 1 && h.Restores == 1 && h.Reads == 0, "failed setter");
        });
        Test("Read failure restores manual control and clears sensors", () => {
            var h = new Fake(); using var s = Session(h);
            s.Set(50); s.Update(); h.FailRead = true;
            Throws<TimeoutException>(s.Update); s.Update();
            Check(s.Faulted && s.Fan0 == null && s.Fan1 == null && s.Pwm == null && h.Restores == 1 && h.Reads == 2, "failed read");
        });
        Test("Read-only failure does not change EC mode", () => {
            var h = new Fake { FailRead = true }; using var s = Session(h);
            Throws<TimeoutException>(s.Update);
            Check(h.Restores == 0, "read-only failure wrote EC");
        });
        Test("Invalid percentage fails before a PWM write", () => {
            foreach (float value in new[] { float.NaN, float.PositiveInfinity, -1, 101 }) {
                var h = new Fake(); using var s = Session(h);
                Throws<ArgumentOutOfRangeException>(() => s.Set(value));
                Check(h.Sets == 0 && h.Restores == 0 && s.Faulted, "invalid value wrote EC");
            }
        });
        Test("Failed automatic restore is retried and reported", () => {
            var h = new Fake { FailRestoreCount = 1 }; using var s = Session(h);
            s.Set(50); Throws<TimeoutException>(s.Reset);
            Check(s.Faulted && h.Restores == 2 && s.Pwm == null, "restore retry");
        });
        Test("Close disposes handle even if restore fails", () => {
            var h = new Fake { FailRestoreCount = 10 }; var s = Session(h);
            s.Set(50); Throws<TimeoutException>(s.Dispose); s.Dispose();
            Check(h.Restores == 1 && h.Disposals == 1, "failed close leaked handle");
        });
        Test("Suspend restores and prevents further access", () => {
            var h = new Fake(); using var s = Session(h);
            s.Set(50); s.Update(); s.Suspend(); s.Set(75); s.Update();
            Check(h.Restores == 1 && h.Sets == 1 && h.Reads == 1 && s.Pwm == null && s.Fan0 == null, "suspend");
        });
        Test("Logger failure cannot prevent automatic recovery", () => {
            var h = new Fake { FailSet = true };
            using var s = new FanSession(h, _ => throw new Exception("logger unavailable"));
            Throws<TimeoutException>(() => s.Set(50));
            Check(h.Restores == 1, "logger prevented restore");
        });
        Test("0 and 100 percent encode endpoints", () => {
            var h = new Fake(); using var s = Session(h);
            s.Set(0); Check(s.Pwm == 0, "zero endpoint");
            s.Set(100); Check(s.Pwm == 100, "full endpoint");
        });
        Test("Changing RPM sample leaves manual PWM usable and hides stale RPM", () => {
            var h = new Fake(); using var s = Session(h);
            s.Update(); s.Set(50); h.UnstableReads = 1; s.Update();
            Check(!s.Faulted && s.Pwm.HasValue && s.Fan0 == null && s.Fan1 == null && h.Restores == 0, "transient sample disabled control");
            s.Set(75); s.Update();
            Check(h.Sets == 2 && s.Pwm > 74 && s.Fan0 == 4000 && s.Fan1 == 6000, "manual control did not recover");
        });
        Test("Changing read-only RPM sample does not poison later manual control", () => {
            var h = new Fake { UnstableReads = 1 }; using var s = Session(h);
            s.Update(); s.Set(50); s.Update();
            Check(!s.Faulted && h.Sets == 1 && h.Restores == 0 && s.Fan0 == 4000, "read-only sample blocked manual control");
        });
        Test("Persistent RPM sampling changes do not fault or repeat errors", () => {
            var h = new Fake { UnstableReads = 60 }; var messages = new List<string>();
            using var s = new FanSession(h, messages.Add);
            s.Set(50);
            for (int tick = 0; tick < 60; tick++) { s.Set(75); s.Update(); }
            Check(!s.Faulted && h.Sets == 61 && h.Reads == 60 && h.Restores == 0 && s.Fan0 == null, "sampling blocked control");
            Check(messages.Count == 1, "sampling log flooded");
            s.Update(); Check(s.Fan0 == 4000 && messages.Count == 2, "sampling did not recover");
        });
        Test("Successful samples end an unavailable burst", () => {
            var h = new Fake(); using var s = Session(h);
            for (int burst = 0; burst < 3; burst++) {
                h.UnstableReads = 4;
                for (int tick = 0; tick < 4; tick++) s.Update();
                s.Update();
            }
            s.Set(50); Check(!s.Faulted && h.Sets == 1 && h.Restores == 0, "sampling bursts blocked control");
        });
        Test("Transient RPM failures log once per burst and once on recovery", () => {
            var h = new Fake { UnstableReads = 3 }; var messages = new List<string>();
            using var s = new FanSession(h, messages.Add);
            for (int tick = 0; tick < 3; tick++) s.Update();
            Check(messages.Count == 1, "transient log flooded");
            s.Update(); Check(messages.Count == 2, "recovery log missing");
        });
        Test("RPM sampler prefers coherent retry and uses the correct fan queries", () => {
            var bytes = new Queue<byte>(new byte[] { 15, 240, 16, 16, 20, 16 });
            var queries = new List<byte>();
            int rpm = RpmSampler.Read(q => { queries.Add(q); return bytes.Dequeue(); }, 1);
            Check(rpm == 4116 && bytes.Count == 0, "coherent retry");
            Check(string.Join(",", queries) == "23,22,23,23,22,23", "fan1 queries");
        });
        Test("RPM sampler tolerates one high-byte step in either direction", () => {
            foreach (byte after in new byte[] { 14, 16 }) {
                var bytes = new Queue<byte>(new byte[] { 15, 240, after, 15, 220, after, 15, 200, after });
                int rpm = RpmSampler.Read(_ => bytes.Dequeue(), 0);
                Check(rpm == after * 256 + 200 && bytes.Count == 0, "bounded latest sample");
            }
        });
        Test("RPM sampler rejects larger ambiguity without masking transport errors", () => {
            var bytes = new Queue<byte>(new byte[] { 15, 240, 16, 15, 220, 16, 15, 200, 17 });
            Throws<RpmSampleUnavailableException>(() => RpmSampler.Read(_ => bytes.Dequeue(), 0));
            Throws<TimeoutException>(() => RpmSampler.Read(_ => throw new TimeoutException("handshake"), 0));
        });
        Test("Startup sync leaves an idle port untouched", () => {
            int discarded = EcStartupSync.Synchronize(() => 0, () => throw new Exception("unexpected read"), _ => throw new Exception("unexpected wait"));
            Check(discarded == 0, "idle port consumed data");
        });
        Test("Startup sync clears one persistent response after the grace period", () => {
            int reads = 0, elapsed = 0;
            int discarded = EcStartupSync.Synchronize(() => (byte)(reads == 0 ? 1 : 0), () => { Check(elapsed >= 100, "read too soon"); reads++; return 42; }, ms => elapsed += ms);
            Check(discarded == 1 && reads == 1 && elapsed == 120, "startup synchronization");
        });
        Test("Startup sync allows another reader to consume its response", () => {
            int polls = 0;
            int discarded = EcStartupSync.Synchronize(() => (byte)(++polls < 4 ? 1 : 0), () => throw new Exception("stole response"), _ => { });
            Check(discarded == 0 && polls == 4, "natural response completion");
        });
        Test("Startup sync bounds input busy waiting without consuming output", () => {
            int elapsed = 0;
            Throws<TimeoutException>(() => EcStartupSync.Synchronize(() => 3, () => throw new Exception("busy read"), ms => elapsed += ms));
            Check(elapsed == 1000, "busy wait was not bounded");
        });
        Test("Startup sync aborts on concurrent input activity", () => {
            int polls = 0;
            Throws<InvalidOperationException>(() => EcStartupSync.Synchronize(() => (byte)(++polls < 3 ? 1 : 3), () => throw new Exception("stole active response"), _ => { }));
        });
        Test("Startup sync never drains repeated incoming output", () => {
            int reads = 0;
            Throws<InvalidOperationException>(() => EcStartupSync.Synchronize(() => 1, () => { reads++; return 42; }, _ => { }));
            Check(reads == 1, "unbounded drain");
        });
        return passed.ToArray();
    }
}
