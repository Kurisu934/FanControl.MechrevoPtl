# FanControl.MechrevoPtl 插件

完整安装、温度曲线、构建和使用限制见 [在线仓库 README](https://github.com/Kurisu934/FanControl.MechrevoPtl#readme)。

发布物是一个 `FanControl.MechrevoPtl.dll`，目标为 FanControl 282 .NET 10 / Windows x64。插件提供双风扇共享 PWM 和两路 RPM，没有独立进程。初始化只查询；启用控制后才发送 PWM。禁用、正常关闭和休眠时尝试恢复自动；通信错误报错、停止发送并尝试恢复。强制结束进程无法保证恢复。

插件仅在 P916F-PTL BIOS 1.01、EC 映像 01.01.00 和 README 中指定的现有 inpoutx64 驱动上验证。低速起转、全范围和实际休眠/唤醒未实测。固件原理见 [EC 分析](https://github.com/Kurisu934/FanControl.MechrevoPtl/blob/main/docs/ec-firmware.md)。

v0.1.1：RPM 读取先重试，允许相邻高字节产生的 256 RPM 歧义。更大歧义跳过该次读数并保持控制，日志只在异常开始与恢复时记录。真实通信或写入错误仍报错一次并尝试恢复自动，后续每秒回调不再重复抛错。

v0.1.2：启动时等待持续待取的输出 100 毫秒，最多清掉一个残留响应字节，再检查 20 毫秒空闲，修复传感器无法加载。输入活动或输出再次出现仍报错；正常事务不清空输出。应避免其他控制程序同时访问 EC。

v0.1.3：新命令发送前遇到待取输出时，允许有界同步后重试一次；已发命令的超时不自动重发。最终失败先清理通信状态、再尝试恢复自动，保留原始错误并只抛一次。没有新增辅助进程。
