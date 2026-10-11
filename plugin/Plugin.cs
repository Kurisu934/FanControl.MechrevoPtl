using System;
using FanControl.Plugins;
using MechrevoPtl;
using Microsoft.Win32;

namespace FanControl.MechrevoPtl;

public sealed class Plugin : IPlugin3
{
    private readonly IPluginLogger logger;
    private FanSession? session;
    private bool powerSubscribed;
    public const string DisplayName = "MECHREVO P916F-PTL";
    public string Name => DisplayName;
    public event Action? RefreshRequested;
    public Plugin(IPluginLogger logger) { this.logger = logger; }

    public void Initialize()
    {
        Close();
        NativeEcHardware? hardware = null;
        try
        {
            hardware = new NativeEcHardware();
            session = new FanSession(hardware, Log);
            hardware = null; // Session owns disposal from here.
            session.Update();
            if (session.Faulted) throw new InvalidOperationException("Initial EC sensor read failed.");
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            powerSubscribed = true;
            Log("Initialized: shared PWM and two RPM sensors. Automatic EC control remains active until Set.");
        }
        catch { hardware?.Dispose(); Close(); throw; }
    }
    public void Load(IPluginSensorsContainer container)
    {
        if (session == null) throw new InvalidOperationException("Plugin is not initialized.");
        container.ControlSensors.Add(new SharedPwm(session));
        container.FanSensors.Add(new FanRpm(session, 0));
        container.FanSensors.Add(new FanRpm(session, 1));
    }
    public void Update() => session?.Update();
    public void Close()
    {
        if (powerSubscribed) { SystemEvents.PowerModeChanged -= OnPowerModeChanged; powerSubscribed = false; }
        FanSession? old = session; session = null;
        old?.Dispose();
    }
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        try
        {
            if (e.Mode == PowerModes.Suspend) session?.Suspend();
            if (e.Mode == PowerModes.Resume)
            {
                // Revalidate the backend after waking; never reuse stale manual state.
                session?.Suspend();
                RefreshRequested?.Invoke();
            }
        }
        catch (Exception error) { Log("Power transition failed: " + error.Message); }
    }
    private void Log(string message) { try { logger.Log(Name + ": " + message); } catch { } }

    private sealed class SharedPwm(FanSession session) : IPluginControlSensor2
    {
        public string Id => "MechrevoPtl.SharedPwm";
        public string Name => "星耀14 双风扇 PWM";
        // FanControl 282 compares the full backend identifier, including the plugin name.
        public string PairedFanSensorId => DisplayName + "/MechrevoPtl.Fan0";
        public float? Value => session.Pwm;
        public void Update() { } // IPlugin2.Update reads both fans once per host cycle.
        public void Set(float value) => session.Set(value);
        public void Reset() => session.Reset();
    }
    private sealed class FanRpm(FanSession session, int index) : IPluginSensor
    {
        public string Id => "MechrevoPtl.Fan" + index;
        public string Name => "星耀14 风扇 " + index;
        public float? Value => index == 0 ? session.Fan0 : session.Fan1;
        public void Update() { }
    }
}
