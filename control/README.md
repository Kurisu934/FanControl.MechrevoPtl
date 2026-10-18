# 临时 EC 控制工具

适用 P916F-PTL BIOS 1.01 / EC 映像 01.01.00。需已有且匹配的 inpoutx64 驱动。运行前退出 FanControl 和其他风扇控制软件；在仓库根目录执行：

```powershell
# 查询两路 RPM。
pwsh -NoProfile -File .\control\fan.ps1 -Mode Read
# 两只风扇共享约 50% PWM，持续 30 秒后恢复自动。
pwsh -NoProfile -File .\control\fan.ps1 -Mode Pwm -Percent 50 -Seconds 30
# 两路独立闭环 RPM 目标，持续 30 秒后恢复自动。
pwsh -NoProfile -File .\control\fan.ps1 -Mode Rpm -Rpm0 4000 -Rpm1 6000 -Seconds 30
# 清除两个手动目标，立即恢复 EC 自动。
pwsh -NoProfile -File .\control\fan.ps1 -Mode Auto
```

默认持续 30 秒，`-Seconds` 允许 1–300 秒。正常结束或控制中异常时，同一进程的 `finally` 尝试恢复自动。没有独立恢复进程；失败会报错。进程崩溃或强制结束时不能保证执行恢复。

工具和插件在启动时均进行一次有界同步：持续待取的响应等待 100 毫秒后最多消费一个字节，再确认 20 毫秒空闲；检测到活动或重复输出仍停止。正常命令期间不清空意外输出。结果字段 `StartupDiscardedBytes` 为本次丢弃的残留字节数（0 或 1）。

PWM 参数范围 0–100%，编码 `round(percent × 255 / 100)`，同时作用于两只风扇。独立 RPM 参数范围 0–8000，编码 `round(RPM / 100)`；这只是接口允许范围，并不代表风扇能达到 8000 RPM 或低速稳定起转。已验证 PWM 原始值 128、191、192 和目标 4000、6000 RPM。

日志写入 `control/results/`，包含时间和可能带有本机路径的异常信息，该目录已被 Git 忽略。手动 PWM 绕过普通温度曲线。持续温度曲线控制请使用 [FanControl 插件](../README.md)。
