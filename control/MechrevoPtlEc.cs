using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

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
        Wait(2, 0);
        if ((Status() & 1) != 0)
            throw new InvalidOperationException("EC PM2 output belongs to another transaction.");
        WritePort(0x6c, command); Wait(2, 0);
        WritePort(0x68, parameter);
        if (!response) { Wait(2, 0); return 0; }
        Wait(1, 1); return ReadPort(0x68);
    }

    public byte ReadMode() { return Exchange(0xe7, 0x11, true); }
    public byte ReadCustomFlag() { return Exchange(0xd5, 0x12, true); }
    public int ReadRpm(int fan)
    {
        if (fan != 0 && fan != 1) throw new ArgumentOutOfRangeException("fan");
        byte lowQuery = (byte)(fan == 0 ? 0x18 : 0x16);
        byte highQuery = (byte)(fan == 0 ? 0x19 : 0x17);
        // EC updates a 16-bit value, but PM2 returns one byte per query.
        // Reject a sample whose high byte changed around the low-byte read.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            byte before = Exchange(0xd5, highQuery, true);
            byte lo = Exchange(0xd5, lowQuery, true);
            byte after = Exchange(0xd5, highQuery, true);
            if (before == after) return lo + 256 * after;
        }
        throw new InvalidOperationException("RPM changed across all coherent-read attempts.");
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
