using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

// The port transactions succeeded, but separate RPM bytes did not form a stable sample.
public sealed class RpmSampleUnavailableException : InvalidOperationException
{
    public RpmSampleUnavailableException() : base("RPM changed across all coherent-read attempts.") { }
}

// Raised only before any byte of the new command has been written.
public sealed class EcOutputPendingException : InvalidOperationException
{
    public EcOutputPendingException() : base("EC PM2 output is pending before the next transaction.") { }
}

public static class EcTransactionRecovery
{
    public static T Execute<T>(Func<T> transaction, Action synchronize)
    {
        try { return transaction(); }
        catch (EcOutputPendingException)
        {
            synchronize();
            return transaction(); // One retry; exceptions here propagate without another recovery.
        }
    }
}

public static class RpmSampler
{
    // Adjacent high bytes leave at most one byte (256 RPM) of ambiguity.
    public const int ToleranceRpm = 256;

    public static int Read(Func<byte, byte> read, int fan)
    {
        if (fan != 0 && fan != 1) throw new ArgumentOutOfRangeException(nameof(fan));
        byte lowQuery = (byte)(fan == 0 ? 0x18 : 0x16);
        byte highQuery = (byte)(fan == 0 ? 0x19 : 0x17);
        int? approximate = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            byte before = read(highQuery);
            byte lo = read(lowQuery);
            byte after = read(highQuery);
            if (before == after) return lo + 256 * after;
            // Prefer a coherent retry. Fall back only to the latest bounded sample.
            approximate = Math.Abs(after - before) * 256 <= ToleranceRpm
                ? lo + 256 * after : null;
        }
        if (approximate.HasValue) return approximate.Value;
        throw new RpmSampleUnavailableException();
    }
}

public static class EcStartupSync
{
    // Only between transactions, under the caller's mutex. Never drain an in-flight transaction.
    public static int Synchronize(Func<byte> status, Func<byte> readOutput, Action<int> pause)
    {
        byte current = status();
        int waits = 0;
        while ((current & 2) != 0)
        {
            if (++waits > 200) throw new TimeoutException("EC PM2 input remained busy at startup.");
            pause(5); current = status();
        }
        if ((current & 1) == 0) return 0;

        // Give a live reader 100 ms to collect its response. Input activity means contention.
        for (int i = 0; i < 20; i++)
        {
            pause(5); current = status();
            if ((current & 2) != 0)
                throw new InvalidOperationException("EC PM2 activity detected during startup synchronization.");
            if ((current & 1) == 0) return 0;
        }
        readOutput(); // At most one pending byte, never an unbounded drain loop.
        for (int i = 0; i < 4; i++)
        {
            pause(5);
            if ((status() & 3) != 0)
                throw new InvalidOperationException("EC PM2 did not remain idle after startup synchronization.");
        }
        return 1;
    }
}

// Protocol recovered from this machine's EC 01.01.00 and verified on 2026-10-12.
// No driver installation, physical-memory access, or firmware flashing.
public sealed class MechrevoPtlEc : IDisposable
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code,
        byte[] input, int inputSize, byte[] output, int outputSize,
        out int returned, IntPtr overlapped);
    private readonly SafeFileHandle handle;
    public int RecoveredTransactions { get; private set; }

    public MechrevoPtlEc()
    {
        handle = CreateFile(@"\\.\inpoutx64", 0xc0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private byte ReadPort(ushort port)
    {
        if (port != 0x68 && port != 0x6c) throw new ArgumentOutOfRangeException("port");
        byte[] input = new byte[16], output = new byte[16];
        input[0] = (byte)port; input[1] = (byte)(port >> 8);
        int n;
        if (!DeviceIoControl(handle, 0x9c402004, input, 2, output, output.Length,
                             out n, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (n != 1) throw new InvalidOperationException("Unexpected I/O read length: " + n);
        return output[0];
    }

    private void WritePort(ushort port, byte value)
    {
        if (port != 0x68 && port != 0x6c) throw new ArgumentOutOfRangeException("port");
        byte[] input = new byte[16], output = new byte[16];
        input[0] = (byte)port; input[1] = (byte)(port >> 8); input[2] = value;
        int n;
        // Installed driver reports ten bytes for writes. Keep the output buffer large enough.
        if (!DeviceIoControl(handle, 0x9c402008, input, 3, output, output.Length,
                             out n, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public byte Status()
    {
        byte s = ReadPort(0x6c);
        if (s == 255) throw new InvalidOperationException("EC PM2 port returned 0xff.");
        return s;
    }

    public int SynchronizeBuffer() => EcStartupSync.Synchronize(Status, () => ReadPort(0x68), Thread.Sleep);
    public int SynchronizeStartup() => SynchronizeBuffer();

    private void Wait(byte mask, byte expected)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while ((Status() & mask) != expected)
        {
            if (timer.ElapsedMilliseconds > 1000) throw new TimeoutException("EC PM2 handshake timeout.");
            Thread.Sleep(1);
        }
    }

    private byte Exchange(byte command, byte parameter, bool response)
    {
        bool synchronized = false;
        byte value = EcTransactionRecovery.Execute(() => ExchangeOnce(command, parameter, response), () =>
        {
            SynchronizeBuffer();
            synchronized = true;
        });
        if (synchronized) RecoveredTransactions++;
        return value;
    }

    private byte ExchangeOnce(byte command, byte parameter, bool response)
    {
        Wait(2, 0);
        if ((Status() & 1) != 0)
            throw new EcOutputPendingException();
        WritePort(0x6c, command); Wait(2, 0);
        WritePort(0x68, parameter);
        if (!response) { Wait(2, 0); return 0; }
        Wait(1, 1); return ReadPort(0x68);
    }

    public byte ReadMode() { return Exchange(0xe7, 0x11, true); }
    public byte ReadCustomFlag() { return Exchange(0xd5, 0x12, true); }
    public int ReadRpm(int fan)
    {
        // EC updates a 16-bit value, but PM2 returns one byte per query.
        return RpmSampler.Read(query => Exchange(0xd5, query, true), fan);
    }
    public int SetPwmPercent(double percent)
    {
        if (double.IsNaN(percent) || double.IsInfinity(percent) || percent < 0 || percent > 100)
            throw new ArgumentOutOfRangeException("percent");
        int raw = (int)Math.Round(percent * 255 / 100, MidpointRounding.AwayFromZero);
        Exchange(0xd6, (byte)raw, false);
        return raw;
    }
    public int SetRpm(int fan, int rpm)
    {
        if ((fan != 0 && fan != 1) || rpm < 0 || rpm > 8000)
            throw new ArgumentOutOfRangeException("rpm");
        int units = (int)Math.Round(rpm / 100.0, MidpointRounding.AwayFromZero);
        Exchange((byte)(fan == 0 ? 0xd7 : 0xd8), (byte)units, false);
        return units * 100;
    }
    public void RestoreAutomatic() { Exchange(0xd5, 0x10, false); }
    public void Dispose() { handle.Dispose(); }
}
