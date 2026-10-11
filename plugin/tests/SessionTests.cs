using System;
using System.Collections.Generic;
using MechrevoPtl;

public static class SessionTests
{
    private sealed class Fake : IEcHardware
    {
        public int Sets, Reads, Restores, Disposals;
        public bool FailSet, FailRead;
        public int FailRestoreCount;
        public (int, int) ReadFans()
        {
            Reads++;
            if (FailRead) throw new TimeoutException("read timeout");
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
            Throws<InvalidOperationException>(() => s.Set(75));
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
        return passed.ToArray();
    }
}
