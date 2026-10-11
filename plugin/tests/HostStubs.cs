using System.Collections.Generic;
using FanControl.Plugins;

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
