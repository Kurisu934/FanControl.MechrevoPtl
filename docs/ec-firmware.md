# P916F-PTL：EC 风扇协议与固件原理

研究和实测日期：2026-10-12。平台为 Intel Panther Lake 的 MECHREVO P916F-PTL，BIOS 1.01，主机 SPI 中的 EC 映像版本 01.01.00。共享 PWM、独立目标 RPM、RPM 查询和恢复自动均已在该平台验证。

本文公开协议事实、偏移和原创伪代码，不提供原始固件或厂商反编译源码。不同平台或 EC 版本不能直接沿用这些地址和命令。

## EC 对象和实际控制器

EC（Embedded Controller）是主板上的嵌入式控制器。ACPI 中的 EC 对象是操作系统访问控制器功能的描述与接口，不是 EC 固件本身。风扇的 PWM 输出、测速和反馈分支位于 ITE 系列控制器的 RV32 固件中；Intel PTL SoC、ACPI 和厂商服务可向 EC 提交模式或其他请求。

在这个平台上找到的精确调速入口是 EC 的 **PM2 私有命令口**。它不是公开承诺兼容的厂商 SDK，也不依赖模式切换来模拟百分比。主板上 EC 芯片的完整型号尚未通过芯片标识或丝印确认。

## 证据链与映像边界

原生 Linux 下，通过 Intel `8086:E423` SPI 控制器和 `intel-spi` 驱动只读取得两份 32 MiB 主机 SPI 镜像，两份一致。SPI 描述符表明其中包含描述符、ME、BIOS 等区域，不能把整个镜像都称作纯 BIOS。实际 BIOS 区域在镜像偏移 `0x1000000..0x2000000`。

Insyde FlashDeviceMap 将 GUID `A73EF3BF-33CC-43A9-B39C-A912C7489A57` 的区域标为 EC Firmware，映射地址 `0xFF000000`、长度 1 MiB。它对应 SPI 镜像偏移 `0x1000000`。提取区域的 SHA256 与映射表校验值一致：

```text
525a04a0d33e36fe2354eaf2706725afa5990688455cc53591445a1e9cb9041e
```

映像头和版本标识：

```text
ITE51300-SB-V.00120230800
ITE Tech. Inc. $INTEL PTL$MECHREVO$A0$VER:01.01.00$
```

复位入口、异常入口、堆栈初始化及连续控制流与 **RV32 RISC-V + 压缩指令** 一致。下面的处理函数地址是提取 EC 映像的偏移；RAM 和外设地址属于 EC 的内部地址空间。

提取的是主机 SPI 中存储的 EC 映像，尚未取得独立活动 EC 闪存的哈希。协议功能与实机响应一致，但不能据此声称活动 EC 闪存与该区域逐字节一致。包含个人配置或标识的原始 SPI/NVRAM、完整设备清单和本机原始日志均不公开。

## 主机端口与握手

本机 ACPI 的 `Q_EC.IOPT` 将 `EDAT` 映射到数据端口 **0x68**，将 `EADD` 映射到命令/状态端口 **0x6C**。EC 内部 PM2 寄存器为：状态 `0xF01510`、输入 `0xF01514`、输出 `0xF01511`。

主机状态位：IBF 是 bit 1，OBF 是 bit 0。每条事务只有一个命令字节和一个参数字节：

1. 等待 IBF 清零。若 OBF 已置位，拒绝事务，不消费可能属于其他客户端的响应。
2. 向 `0x6C` 写命令。
3. 等待 IBF 清零，向 `0x68` 写参数。
4. 查询命令等待 OBF 置位，再从 `0x68` 读一个字节。
5. 设置命令等待 IBF 清零后结束。没有 ACK 或成功状态字节，必须用后续 RPM 响应判断是否生效。

实现对每次握手设 1 秒超时，并拒绝状态 `0xFF`。互斥锁 `Local\MechrevoPtlFanDiagnostic` 协调本项目的插件与临时工具；其他厂商软件不遵循这个锁，不能保证与它们并发访问安全。

## 命令表

下表命令、固定参数及偏移均为 **十六进制**。

| 命令 | 参数 | 行为 | 处理函数偏移 |
|---|---|---|---|
| `D6` | 原始 PWM 字节 | 两只风扇进入直接 PWM 模式，使用同一目标 | `0xC8CE` |
| `D7` | 目标 RPM / 100 | 风扇 0 进入 RPM 闭环 | `0xC8FE` |
| `D8` | 目标 RPM / 100 | 风扇 1 进入 RPM 闭环 | `0xC922` |
| `D5` | `10` | 清除两只手动目标，恢复自动 | `0xC7F2` |
| `D5` | `16` / `17` | 风扇 1 RPM 低 / 高字节 | `0xC870` / `0xC888` |
| `D5` | `18` / `19` | 风扇 0 RPM 低 / 高字节 | `0xC89E` / `0xC8B6` |
| `E7` | `11` | 读取散热模式 | `0xD09E` |
| `D5` | `12` | 读取另一自定义曲线标志，返回 0 或 8 | `0xC83E` |

编码示例：

```text
50% PWM   -> round(50 * 255 / 100) = 128 = 0x80
75% PWM   -> round(75 * 255 / 100) = 191 = 0xBF
100% PWM  -> 255 = 0xFF
4000 RPM  -> 40  = 0x28
6000 RPM  -> 60  = 0x3C
```

取整采用正数的四舍五入，半值远离零。原始 PWM 192 是约 75.294%，与请求 75% 得到的原始 191 有区别。**发送十进制 100 只相当于约 39.216%，不是 100% PWM。** 百分比按周期值 255 归一化，没有用示波器标定实际电气占空比。

`D5/12` 在直接 PWM/RPM 控制期间仍可返回 0，所以它不能单独表示手动控制是否开启。

## 命令分派与 RAM 状态

`0xD292` 区分 PM2 的命令/数据输入，将命令保存到 RAM `0x80203A`，参数保存到 `0x80203C`。`0xD248` 设置待接收参数计数，`0xD0BE` 分派 `D5..D8`。`D5` 子命令跳转表在 `0x15188`，子命令 `0x10` 指向恢复函数 `0xC7F2`。

| 状态 | 风扇 0 | 风扇 1 |
|---|---|---|
| 实测 RPM，16 位 | `0x800400` | `0x800410` |
| 手动类型：0=自动，1=RPM，2=PWM | `0x800406` | `0x800416` |
| 原始 PWM 目标，8 位 | `0x800407` | `0x800417` |
| RPM/100 目标，8 位 | `0x800408` | `0x800418` |
| PWM 外设寄存器 | `0xF01804` | `0xF01803` |

这些地址不能当作主机的 ACPI EC 索引寄存器。插件访问的是上面的主机 PM2 端口，不通过驱动直接读写 EC 内部 RAM。

从控制流归纳的行为如下；这是说明性的原创伪代码，省略了其他状态和细节：

```text
on D6(raw):
    for fan in [0, 1]:
        fan.manual_type = PWM
        fan.pwm_target = raw

on D7(units): fan0.manual_type = RPM; fan0.rpm_target_units = units
on D8(units): fan1.manual_type = RPM; fan1.rpm_target_units = units

on D5(0x10):
    for fan in [0, 1]:
        clear fan.manual_type, fan.pwm_target, fan.rpm_target_units
    clear associated custom-curve flags
    preserve thermal mode

periodic fan update:
    measure RPM
    if manual_type == PWM: write raw target to PWM peripheral
    if manual_type == RPM: adjust PWM toward target_units * 100
    if manual_type == AUTO: follow normal mode / temperature feedback
```

`0x8BF6` 根据手动类型选择 PWM 或 RPM 分支。类型 2 直接把目标写入 PWM 外设，绕过普通温度曲线；类型 1 将目标字节乘 100，与实际 RPM 比较，再用 1、2、4 等步长调节 PWM。反馈存在约 100 RPM 的死区。`0x8CF6` 获取两只风扇测速值并调用该分支；类型 0 使用 `0x8B18` 的自动反馈。

因此，百分比是原始 PWM 控制，目标 RPM 是由 EC 自己反馈调节。两者不呈简单线性关系；相同 PWM 下两只风扇也可有不同 RPM。目前没有验证独立 PWM 命令。

## 测速与一致性读取

`0x88B8` 将两个测速字节组合为计数。计数为零时 RPM 为 0，否则计算 `2156250 / count` 并存为 16 位 RPM。本次查询和目标 RPM 响应相符。

RPM 查询每次只返回一个字节，不是原子快照。源码采用高 / 低 / 高读取：两次高字节一致时组合为 16 位值，否则重试，最多三次。该方法检测跨高字节变化，不能证明多次命令构成严格的硬件原子采样。插件还拒绝大于 16000 RPM 的样本，遇到异常停止控制。

## PWM 初始化与自动模式

PWM 初始化函数 `0xAC5C` 遍历从 `0x14D88` 起的 21 项寄存器表。`0x14DA0` 对应的表项将 `0xF01842` 设置为 255，支持以 255 归一化原始 PWM。直接 PWM 分支没有百分比转换，也未在该分支中限制输入。

模式命令 `0x94/0x95/0x96` 在 `0x7162` 分派后，将 `0x80033F` 分别设为 1/2/3，与本机 ACPI 的 `0x66` 命令口模式路径一致。`0x88E0` 选择 RAM 曲线 `0x8041C0`、`0x804250`、`0x804340`。复位初始化从 flash `0x170F8` 起复制数据到 RAM `0x804000`，据此定位静态曲线。

模式 1/2/3 分别有 9/15/17 条 16 字节记录，可识别两只风扇的目标 RPM。最高记录目标分别为 4900/4700、6500/6300、7100/7000 RPM；其余字段尚未全部解释，不能将全部字节直接当成温度。曲线目标不保证在所有条件下可达到。

## 恢复与失效边界

`D5/10` 清除两路手动类型及目标，并保留散热模式。测试后转速逐步下降，符合恢复自动反馈的行为。

另一个路径 `0x9348` 在 RAM `0x8001FC` 倒计时归零时清除两个手动类型；`0xB7C8` 的某电源状态分支会装入 60，并临时设置 2700/2600 RPM。它不是本次 PM2 设置的通用超时保证，不能依靠它处理软件崩溃。

当前插件在单进程内处理正常退出、禁用、休眠事件和通信错误。没有独立 watchdog，进程崩溃或强制终止时不能保证恢复。模拟测试覆盖休眠状态逻辑，实际睡眠/唤醒、低速起转、全 PWM 区间、长期负载和第三方并发仍未验证。

## Windows 驱动调用

通过已运行、已核验的 `inpoutx64` 驱动访问端口：

| 操作 | IOCTL | 输入 | 输出 |
|---|---|---|---|
| 读字节 | `0x9C402004` | 小端 ushort 端口，2 字节 | 1 字节 |
| 写字节 | `0x9C402008` | 小端 ushort 端口 + 数据字节 | 使用 16 字节输出缓冲区 |

已核验驱动的写调用会报告 10 字节输出，不能使用过小的输出缓冲区。本项目只调用字节端口 I/O，不使用该驱动的物理内存或其他接口。源码按机型、BIOS 和驱动哈希限制使用。

## 复核与参考

使用用户自行合法取得的 EC 区域，可在离线环境安装 `tools/requirements.txt` 中的 Capstone，然后运行 `tools/disasm_ec.py` 检查对应范围。输出含代码和可能的数据，自动反汇编不是函数边界证明；需继续复核分支目标、初始化路径、RAM 引用及实机行为。工具不会执行固件或访问硬件。

参考：[Linux Intel SPI PCI 驱动](https://github.com/torvalds/linux/blob/master/drivers/spi/spi-intel-pci.c)、[Intel SPI 驱动](https://github.com/torvalds/linux/blob/master/drivers/spi/spi-intel.c)、[PTL 支持提交](https://github.com/torvalds/linux/commit/ceb259e43bf572ba7d766e1679ba73861d16203a)、[Capstone](https://github.com/capstone-engine/capstone)、[AMD P916F-STX 的独立研究](https://github.com/YMGPwcca/Mechrevo-Xingyao-14)。STX 的平台及协议研究不能代替 PTL 的验证。
