# FanControl.MechrevoPtl

机械革命星耀14 **P916F-PTL** 的 FanControl 插件与 EC 风扇协议研究。已在 **BIOS 1.01 / EC 映像 01.01.00** 上实测共享 PWM 百分比调速和独立目标 RPM 调速。

This project provides a FanControl plugin and documented EC fan commands for the Intel Panther Lake MECHREVO P916F-PTL. It is validated only on BIOS 1.01 with EC image 01.01.00. It is not a generic MECHREVO or AMD P916F-STX plugin.

在线仓库与完整文档：https://github.com/Kurisu934/FanControl.MechrevoPtl 。

## 功能

- 一个双风扇共享 PWM 控制，范围 0–100%，量化步进约 0.392%。
- 两个实际 RPM 传感器，默认将共享控制与风扇 0 配对。
- 支持 FanControl 手动百分比及温度曲线。
- 启动时同步持续未读的 EC 响应，避免残留字节阻止传感器加载；最多消费一个字节。
- 禁用、正常退出和休眠事件时尝试恢复 EC 自动控制。
- RPM 分字节采样先重试，允许 256 RPM 的高字节歧义；超出容差仅跳过该次读数，不停用手动控制。
- 通信失败报错一次、停止继续发送 PWM 并尝试恢复自动；需刷新插件后重新控制。
- 单个 DLL，在 FanControl 进程内运行，没有辅助进程、服务或独立 watchdog。
- 独立目标 RPM 命令通过附带的临时 PowerShell 工具提供，分辨率为 100 RPM。

## 兼容性与依赖

已验证环境：Windows x64、FanControl 282 **.NET 10** 版本、MECHREVO P916F-PTL BIOS 1.01。插件检查主板型号、BIOS 和已安装驱动的 SHA256，不匹配会报错。

需要本机已安装且可用的 `inpoutx64` 驱动，设备名为 `\\.\inpoutx64`。已验证驱动 SHA256：

```text
F8965FDCE668692C3785AFA3559159F9A18287BC0D53ABB21902895A8ECF221B
```

插件不安装驱动，不提供厂商控制中心或驱动文件。它通过现有驱动的字节 I/O 接口，仅访问 `0x68/0x6C`；没有物理内存访问或固件刷写功能。其他 BIOS/EC/驱动版本需单独验证。

## 安装与温度曲线

1. 从本仓库 Releases 下载 `FanControl.MechrevoPtl.dll`，或按下文自行编译。
2. 退出 FanControl，把 DLL 放到 `FanControl/Plugins/MechrevoPtl/`。
3. 启动 FanControl 或刷新传感器，插件名称是 `MECHREVO P916F-PTL`。
4. 在 `星耀14 双风扇 PWM` 控制卡启用手动控制并输入百分比。设置温度曲线时，新建线性或图表曲线，选 CPU 温度传感器、设置温度对应的 PWM 百分比，然后在控制卡选该曲线，关闭手动控制并保持控制卡启用。
5. 如需查看两个 RPM 卡片，在界面设置中取消隐藏风扇速度卡片。两只风扇共享 PWM 曲线，实际转速可能不同。

百分比读数是最近发送的量化命令，不是 EC 的 PWM 回读。自动控制时显示为空。设置命令没有 ACK，应结合实际 RPM 判断响应。

v0.1.2 修复启动时 `EC PM2 output belongs to another transaction` 导致全部插件传感器消失的问题。持有互斥锁后，先等待输入空闲；若输出持续待取，给其他读取者 100 毫秒后最多消费一个残留字节，再检查 20 毫秒空闲并重新查询模式、RPM。检测到输入活动或输出再次出现就报错，不循环清空。此同步仅在初始化执行；正常事务仍拒绝意外输出。互斥锁不能协调不使用它的其他控制程序，运行时应避免并发访问 EC。

v0.1.1 修复手动控制重复报错：EC 转速的高、低字节分开读取，转速变化可能造成采样不一致。插件先尝试三次一致读取，最后一次高字节只相差一级时允许两种组合相差 256 RPM 的近似读数；更大歧义显示为空，继续下一次采样，不锁死控制。容差用于读取同一采样的字节组合，不限制真实转速变化幅度。采样异常只在开始和恢复时记日志；握手超时、驱动或写入错误仍立即报一次并尝试恢复自动，后续宿主回调不再重复抛错或发送 PWM。

禁用控制卡或从托盘正常退出会恢复自动。关闭窗口通常只是留在托盘，控制会继续。进程崩溃或强制结束无法保证恢复；可退出其他控制程序后使用 `control/fan.ps1 -Mode Auto`。手动 PWM 绕过 EC 普通温度曲线；低于约 50% 的起转/停转、长期负载、实际睡眠/唤醒及全 PWM 范围尚未实测。0% 和 100% 目前仅检查了编码。

## 编译与测试

需要 PowerShell 7.6（.NET 10）和 FanControl 282 .NET 10 目录。脚本使用 PowerShell 自带 Roslyn，无需 .NET SDK；自动寻找已安装的 .NET 10 Desktop Runtime，也可显式提供 `-SystemEventsAssembly`。

在仓库根目录运行，替换示例中的 FanControl 目录：

```powershell
pwsh -NoProfile -File .\plugin\build.ps1 -FanControlDirectory 'D:\Apps\FanControl'
pwsh -NoProfile -File .\plugin\test.ps1
# 退出 FanControl 后，安装到自己的目录。
pwsh -NoProfile -File .\plugin\install.ps1 -FanControlDirectory 'D:\Apps\FanControl'
```

输出为 `plugin/dist/FanControl.MechrevoPtl.dll`。`test.ps1` 的 26 项模拟测试不访问硬件；提供 `-FanControlDirectory` 后再验证两个真实宿主 `Control.Update()` 路径（仍使用假硬件），共 28 项。实机检查需要匹配的本机硬件和现有驱动，运行前退出 FanControl：

```powershell
# 包含真实宿主手动控制循环的回归测试，不访问硬件。
pwsh -NoProfile -File .\plugin\test.ps1 -FanControlDirectory 'D:\Apps\FanControl'
# 只读：初始化、RPM、宿主配对、加载/关闭。
pwsh -NoProfile -File .\plugin\hardware-test.ps1 -FanControlDirectory 'D:\Apps\FanControl'
# 主动调速：经宿主 Control.Update() 测试 50% / 75%，同一进程 finally 恢复自动。
pwsh -NoProfile -File .\plugin\hardware-test.ps1 -FanControlDirectory 'D:\Apps\FanControl' -ExerciseControl
```

也可使用 .NET 10 SDK：

```powershell
dotnet build .\plugin\FanControl.MechrevoPtl.csproj -c Release -p:FanControlDirectory='D:\Apps\FanControl'
```

SDK 输出目录为 `plugin/bin/Release/net10.0-windows/`；后续使用上述测试/安装脚本时，先把其中的插件 DLL 复制到 `plugin/dist/`。本次发布实际通过 Roslyn 脚本构建，未在本机验证 SDK 构建。

## 协议与实测

请求 50%（编码 128，实际 50.196%）时测得 5183 / 4968 RPM；请求 75%（编码 191，实际 74.902%）时为 6780 / 6514 RPM。独立目标 4000 / 6000 RPM 时为 3956 / 5956 RPM。这些是特定条件下的实测结果，不是转速保证。

- [EC 协议与固件原理](docs/ec-firmware.md)：端口握手、命令表、RAM 状态、PWM/RPM 分支、自动模式和证据边界。
- [机器可读协议](docs/ec-protocol.json)。
- [验证摘要](docs/validation.md)：硬件与模拟测试结果。
- [临时控制工具](control/README.md)。
- [公开内容与隐私检查](docs/publication-scope.md)。

## 项目结构与许可

`plugin/` 实现 FanControl 生命周期和会话故障处理，`control/MechrevoPtlEc.cs` 实现端口协议，`tools/` 提供读取用户自备 EC 映像的离线分析工具。源码与原创文档采用 [MIT](LICENSE) 许可证。

不包含原始 SPI/BIOS/EC 固件、厂商二进制、反编译的厂商源码或驱动。依赖仍遵循各自许可。

参考：[FanControl](https://github.com/Rem0o/FanControl.Releases)、[FanControl 插件示例](https://github.com/Rem0o/FanControl.Demo)、[独立的 AMD P916F-STX 研究](https://github.com/YMGPwcca/Mechrevo-Xingyao-14)。本仓库协议是在 PTL 机器上分析和实测得到，不能直接套用于 STX。
