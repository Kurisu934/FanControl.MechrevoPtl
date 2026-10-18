using System;
using System.Collections.Generic;
using System.Reflection;
using FanControl.Domain;
using FanControl.Domain.Interfaces;
using FanControl.Plugins;
using FanControl.Serializable;
using FanControl.Serializable.Interfaces;
using MechrevoPtl;

public sealed class SmokeLogger : IPluginLogger
{
    public List<string> Messages { get; } = new();
    public void Log(string message) { Messages.Add(message); }
}
public sealed class SmokeContainer : IPluginSensorsContainer
{
    public List<IPluginControlSensor> ControlSensors { get; } = new();
    public List<IPluginSensor> FanSensors { get; } = new();
    public List<IPluginSensor> TempSensors { get; } = new();
}

// Manual mode needs no curve or RPM pairing; these repository proxies return no entities.
public class EmptyRepositoryProxy : DispatchProxy
{
    protected override object Invoke(MethodInfo method, object[] args)
    {
        if (method.ReturnType == typeof(void)) return null;
        return method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
    }
}

public static class HostManualChecks
{
    public static Control Create(IControlSensorSource sensor, SerializableControlConfig config)
    {
        var curves = DispatchProxy.Create<IFanCurveRepository<IConfiguredFanCurve<IFanCurveConfig>>, EmptyRepositoryProxy>();
        var sensors = DispatchProxy.Create<ISensorRepository<ISensorSource>, EmptyRepositoryProxy>();
        return new Control(sensor, config, curves, sensors);
    }

    public static SerializableControlConfig Config(int percent) => new()
    {
        Enable = true, ManualControl = true, ManualControlValue = percent,
        SelectedCommandStepUp = 100, SelectedCommandStepDown = 100,
        Calibration = Array.Empty<CalibrationPoint>()
    };

    private sealed class FakeHardware : IEcHardware
    {
        public int Sets, Restores;
        public bool Unstable, FailSet;
        public (int, int) ReadFans()
        {
            if (Unstable) throw new RpmSampleUnavailableException();
            return (4000, 6000);
        }
        public int SetPwm(double percent)
        {
            Sets++;
            if (FailSet) throw new TimeoutException("partial write");
            return (int)Math.Round(percent * 255 / 100, MidpointRounding.AwayFromZero);
        }
        public void RestoreAutomatic() { Restores++; }
        public void Synchronize() { }
        public void Dispose() { }
    }

    public static string[] Run()
    {
        var passed = new List<string>();
        foreach (bool failSet in new[] { false, true })
        {
            var hardware = new FakeHardware { Unstable = true, FailSet = failSet };
            using var session = new FanSession(hardware, _ => { });
            // Use the plugin's actual private control sensor, then the host's real wrapper and engine.
            Type sensorType = typeof(FanControl.MechrevoPtl.Plugin).GetNestedType("SharedPwm", BindingFlags.NonPublic);
            var sensor = (IPluginControlSensor)Activator.CreateInstance(sensorType, session);
            var wrapped = new FanControl.Domain.BackendProviders.Plugin.PluginControlSensor(sensor, FanControl.MechrevoPtl.Plugin.DisplayName);
            using var control = Create(wrapped, Config(50));
            int errors = 0;
            for (int tick = 0; tick < 60; tick++)
            {
                session.Update();
                try { control.Update(); } catch (TimeoutException) { errors++; }
            }
            if (errors != (failSet ? 1 : 0) || hardware.Sets != (failSet ? 1 : 60) || hardware.Restores != (failSet ? 1 : 0))
                throw new Exception("Host manual control repeated an error or lost control.");
            if (!failSet)
            {
                control.UpdateConfig(Config(75)); control.Update();
                if (session.Pwm < 74 || !session.Pwm.HasValue) throw new Exception("Host manual percentage did not change.");
            }
            passed.Add(failSet ? "Host manual ticks report a write error only once" : "Host manual ticks survive unavailable RPM and change percentage");
        }
        return passed.ToArray();
    }
}
