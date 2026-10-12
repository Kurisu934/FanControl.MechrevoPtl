using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32;

namespace MechrevoPtl;

public interface IEcHardware : IDisposable
{
    (int Fan0, int Fan1) ReadFans();
    int SetPwm(double percent);
    void RestoreAutomatic();
}

public sealed class NativeEcHardware : IEcHardware
{
    public const string MutexName = @"Local\MechrevoPtlFanDiagnostic";
    public const string DriverHash = "F8965FDCE668692C3785AFA3559159F9A18287BC0D53ABB21902895A8ECF221B";
    private readonly MechrevoPtlEc ec;

    public NativeEcHardware()
    {
        ValidatePlatform();
        ec = new MechrevoPtlEc();
        try
        {
            WithMutex(() =>
            {
                int mode = ec.ReadMode();
                if (mode < 1 || mode > 3)
                    throw new InvalidOperationException("EC mode does not match the validated protocol.");
                if (ec.ReadCustomFlag() != 0)
                    throw new InvalidOperationException("EC custom curve is active.");
                return 0;
            });
        }
        catch { ec.Dispose(); throw; }
    }

    public static void ValidatePlatform()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("This plugin requires Windows x64.");
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        string board = key?.GetValue("BaseBoardProduct") as string ?? "";
        string bios = key?.GetValue("BIOSVersion") as string ?? "";
        string vendor = key?.GetValue("BIOSVendor") as string ?? "";
        if (!board.Contains("P916F-PTL", StringComparison.OrdinalIgnoreCase) ||
            bios != "1.01" || !vendor.Contains("MECHREVO", StringComparison.OrdinalIgnoreCase))
            throw new PlatformNotSupportedException("Validated platform: MECHREVO P916F-PTL BIOS 1.01.");
        string driver = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                                     "System32", "drivers", "inpoutx64.sys");
        using var file = File.OpenRead(driver);
        if (Convert.ToHexString(SHA256.HashData(file)) != DriverHash)
            throw new InvalidOperationException("Installed inpoutx64 driver differs from the verified version.");
    }

    private static T WithMutex<T>(Func<T> action, int timeout = 100)
    {
        using var mutex = new Mutex(false, MutexName);
        bool acquired;
        try { acquired = mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new InvalidOperationException("Another process owns the EC port.");
        try { return action(); }
        finally { mutex.ReleaseMutex(); }
    }

    public (int Fan0, int Fan1) ReadFans() => WithMutex(() =>
    {
        int a = ec.ReadRpm(0), b = ec.ReadRpm(1);
        if (a > 16000 || b > 16000)
            throw new InvalidOperationException("EC returned an implausible RPM sample.");
        return (a, b);
    });
    public int SetPwm(double percent) => WithMutex(() => ec.SetPwmPercent(percent));
    public void RestoreAutomatic() => WithMutex(() => { ec.RestoreAutomatic(); return 0; }, 1000);
    public void Dispose() => ec.Dispose();
}

// State machine shared by the plugin and test harness; no host-specific API here.
public sealed class FanSession : IDisposable
{
    private readonly object gate = new();
    private readonly IEcHardware hardware;
    private readonly Action<string> log;
    private bool active, disposed, suspended;
    private bool sampleUnavailable;
    public bool Faulted { get; private set; }
    public float? Pwm { get; private set; }
    public float? Fan0 { get; private set; }
    public float? Fan1 { get; private set; }

    public FanSession(IEcHardware hardware, Action<string> log)
    {
        this.hardware = hardware; this.log = log;
    }

    public void Update()
    {
        lock (gate)
        {
            if (disposed || Faulted || suspended) return;
            try
            {
                var fans = hardware.ReadFans(); Fan0 = fans.Fan0; Fan1 = fans.Fan1;
                if (sampleUnavailable) Log("RPM readings resumed.");
                sampleUnavailable = false;
            }
            catch (RpmSampleUnavailableException)
            {
                // A changing sample is distinct from a failed EC transaction. Do not publish stale RPM.
                Fan0 = Fan1 = null;
                if (!sampleUnavailable) Log("RPM changed beyond read tolerance; skipped this sample.");
                sampleUnavailable = true;
            }
            catch (Exception e) { Fail(e); throw; }
        }
    }

    public void Set(float percent)
    {
        lock (gate)
        {
            // FanControl calls Set every clock tick. The original error was already reported;
            // faulted callbacks must reject writes without producing an exception every second.
            if (disposed || Faulted || suspended) return;
            try
            {
                if (!float.IsFinite(percent) || percent < 0 || percent > 100)
                    throw new ArgumentOutOfRangeException(nameof(percent));
                active = true; // A setter may fail after it has partially reached the EC.
                int raw = hardware.SetPwm(percent);
                Pwm = raw * 100f / 255;
            }
            catch (Exception e) { Fail(e); throw; }
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            if (disposed) return;
            try { Restore(); }
            catch (Exception e) { Fail(e); throw; }
        }
    }

    public void Suspend()
    {
        lock (gate)
        {
            if (disposed) return;
            suspended = true;
            try { Restore(); }
            catch (Exception e) { Fail(e); throw; }
            Fan0 = Fan1 = null;
        }
    }

    private void Restore()
    {
        if (!active) { Pwm = null; return; }
        hardware.RestoreAutomatic();
        active = false; Pwm = null;
        Log("EC automatic control restored.");
    }

    private void Fail(Exception e)
    {
        Faulted = true; Pwm = Fan0 = Fan1 = null;
        Log("Control stopped: " + e.Message);
        try { Restore(); }
        catch (Exception restoreError)
        {
            Log("Immediate automatic restore failed: " + restoreError.Message);
        }
    }

    private void Log(string message) { try { log(message); } catch { } }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            try { Restore(); }
            catch (Exception e) { Log("Close restore failed: " + e.Message); throw; }
            finally
            {
                disposed = true; Pwm = Fan0 = Fan1 = null;
                hardware.Dispose();
            }
        }
    }
}
