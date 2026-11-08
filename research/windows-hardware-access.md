# Windows 底层硬件访问调研：驱动签名、WinRing0 封禁、PawnIO 与用户态替代路径

- 任务：共享任务板 task-2；调研日期 2026-11-08。
- 证据约定：**[已核实]** = 本次直接抓取/下载并读到的官方或一手内容；**[推测]** = 基于已核实事实的推断。外部内容仅作数据使用，不构成法律意见。

## 0. 核心回答

**不写自己的内核驱动，也能做出可交付的传感器/风扇/灯效产品，但只能覆盖“用户态可达 + 可复用既有签名驱动”的部分；要覆盖主板 SuperIO/EC/板载 ARGB/DRAM RGB，必须复用 PawnIO（作者签名驱动 + 模块）这类既有基础设施，而不是自研驱动。**

## 1. WinRing0 现状与封禁时间线

- **[已核实]** CVE-2020-14979：WinRing0.sys / WinRing0x64.sys 1.2.0（EVGA Precision X1 ≤1.0.6 内置）允许本地低权限进程读写任意物理内存，映射 `\Device\PhysicalMemory` 即可提权到 SYSTEM；CVE 记录发布于 2020-08-11（[CVE 记录](https://www.cve.org/CVERecord?id=CVE-2020-14979)、[SpecterOps 分析](https://posts.specterops.io/cve-2020-14979-local-privilege-escalation-in-evga-precisionx1-cf63c6b95896)）。
- **[已核实]** Microsoft 支持文档确认 Defender 对 `VulnerableDriver:WinNT/Winring0` 的检出 “is valid”，点名 HWiNFO、Libre Hardware Monitor、MSI Afterburner、OpenRGB、FanCtrl、SteelSeries Engine 等受影响，官方缓解办法仅是加 Defender 排除项（[Microsoft Support](https://support.microsoft.com/en-us/windows/security/threat-malware-protection/microsoft-defender-antivirus-alert-vulnerabledriver-winnt-winring0)）。
- **[已核实]** 本次直接下载 `aka.ms/VulnerableDriverBlockList` 解包，在 `DriverPolicy_Enforced.xml` 中检索到 `ID_DENY_WINRING0_SHA1/SHA256` 及 `WinRing0x64.sys`、`WinRing0_1_2_2.sys` 的 **hash 拒绝规则**——WinRing0 已进 Microsoft vulnerable driver blocklist，且按二进制 hash 拦截（[blocklist 说明](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/design/microsoft-recommended-driver-block-rules)、[下载](https://aka.ms/VulnerableDriverBlockList)）。
- **[已核实]** blocklist 自 **Windows 11 2022 update** 起对所有设备默认启用；memory integrity（HVCI）、Smart App Control、S mode 生效时也强制执行；微软称 **HVCI 在多数新 Win11 设备上默认开启**（[blocklist](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/design/microsoft-recommended-driver-block-rules)、[HVCI](https://learn.microsoft.com/en-us/windows/security/hardware-security/enable-virtualization-based-protection-of-code-integrity)）。准确地说：HVCI 不直接拦 WinRing0（它有签名），失效来自 Defender 检出 + blocklist 强制生效。
- **[已核实]** 时间线：2025-03 起 Defender 以 `HackTool:Win32/Winring0` 隔离相关工具，iBuyPower 一度计划重签（[WinFuture 2025-03-14](https://winfuture.de/news,149592.html)、[The Verge](https://www.theverge.com/report/629259/winring0-windows-defender-fan-control-pc-monitoring-alert-quarantine)）；FanControl README 记载 **截至 2025-09-04，V237 及更早内置的 WinRing0 被改判 `Trojan:Win32/Vigorf.A`**，导致传感器检测失败（[FanControl.Releases](https://github.com/Rem0o/FanControl.Releases)）。

**结论 [已核实]：** 依赖 WinRing0 的旧版 LHM / FanControl / OpenRGB 在现代 Win11 上会加载失败或被隔离；hash 规则意味着“重新打包同一个 WinRing0.sys”也无用。

## 2. PawnIO：为什么能替代 WinRing0

- **[已核实]** 定位：可脚本化的通用内核驱动，内核态只提供受控原语（IO/物理内存/MSR 等），硬件逻辑写在 Pawn 语言模块里，用户态经 IOCTL 加载调用：`pawnio_open` → `pawnio_load` → `pawnio_execute`（[pawnio.eu](https://pawnio.eu/)、[PawnIOLib.h](https://github.com/namazso/PawnIO/blob/master/PawnIOLib/include/PawnIOLib.h)）。
- **[已核实]** 四个版本：Official edition（与开源版相同但**经数字签名**）、Unrestricted edition（未签名、**关闭模块签名校验**）、开源版（GPL）、Licensed（可商业定制授权）（[pawnio.eu](https://pawnio.eu/)）。驱动内用 SHA-256 + RSA PKCS#1 校验模块签名（[signature.cpp](https://github.com/namazso/PawnIO/blob/master/PawnIO/src/signature.cpp)），官方模块签名构建见 [PawnIO.Modules Releases](https://github.com/namazso/PawnIO.Modules/releases)。**[推测]** 官方版只接受作者签名模块，故第三方自研内核逻辑须上游化由作者签名，或购买商业授权。
- **[已核实]** 许可证为 **GPL-2.0 + 特殊例外**：例外只覆盖“仅通过 device I/O control 接口通信的独立模块”，**明确不覆盖经 Pawn 接口通信的程序——所有加载进 PawnIO 的模块都必须 GPL 兼容（建议 LGPL-2.1）**（[PawnIO README](https://github.com/namazso/PawnIO)）。对闭源商业产品是硬约束：模块开源或谈商业授权。
- **[已核实]** 覆盖：`LpcIO.p`（SuperIO）、`IsaBridgeEC.p`（EC）、`SmbusI801/PIIX4/NCT6793.p`（SMBus）、`IntelMSR.p`/`AMDFamily17.p`/`RyzenSMU.p`、`IntelPCHThermal.p`、`Nvidia.p`、`LedsValve.p` 等（[模块仓库](https://github.com/namazso/PawnIO.Modules)）。
- **[已核实]** 生态迁移：LHM PR #1857 “Swap WinRing0 to PawnIO” **2025-09-16 合并**，已知回归为 “Gigabyte EC is unimplemented for now”（[PR #1857](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/pull/1857)）；FanControl “V238 and above ships with a PawnIO build of LHM”、V241 起显示 PawnIO 版本（[README](https://github.com/Rem0o/FanControl.Releases)）；OpenRGB 在 Windows 上改用 PawnIO 访问 SMBus，需安装 PawnIO 并以管理员运行，文档称 WinRing0/InpOut32 已弃用应卸载（[SMBusAccess.md](https://github.com/CalcProgrammer1/OpenRGB/blob/master/Documentation/SMBusAccess.md)）。
- **硬限制 [已核实+推测]**：依赖第三方签名与发布节奏；模块受 GPL 传染（除非商业授权）；官方版模块不可私改；项目 “as is without warranty … use at your own risk”，产品责任仍在自己。

## 3. 自己写内核驱动：门槛与风险

- **[已核实]** 自 Win10 1607 起，Windows 不加载未经 Dev Portal 签名的新内核驱动；注册 Hardware Dev Center 需要 **EV code signing certificate**（[Driver Signing Policy](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/kernel-mode-code-signing-policy--windows-vista-and-later-)、[code signing requirements](https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/code-signing-reqs)）。
- **[已核实]** **attestation ≠ WHQL**：attestation 无需 HLK、只支持 Win10 Desktop 及以后，**不能发布到面向零售用户的 Windows Update，且不算 Windows Certified、不保证兼容性**；正式零售/预装须走 WHCP/HLK（[Driver signing options](https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/driver-signing-offerings)、[Attestation sign](https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/code-signing-attestation)）。
- **[已核实]** test signing 不能交付：“Test signatures must not be used for production purposes or released to customers”（[Test-Signing](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/introduction-to-test-signing)）；Secure Boot 开启时内核驱动须由 Microsoft 根签名链签发。
- **风险 [已核实/推测]**：AV 误报（FanControl 称 exe/安装器签名可降低误报）；被 blocklist 收录（微软季度+月度更新，官方承认可能造成失效甚至蓝屏）；每版本 HLK/每代芯片组寄存器差异的长期维护；**成本量级 [推测]**：EV 证书年费 + 驱动/逆向人力 + 测试环境，小团队通常无法承担。

## 4. 用户态替代路径盘点

| 路径 | 能读/能控 | 可行性 | 限制 |
|---|---|---|---|
| 厂商灯效 SDK | 各品牌灯效 | 高（需合作） | 需装厂商软件；条款需审阅 |
| NVAPI | 温度/功耗/时钟，官方称含 thermal & cooling controls | 高 | 随驱动分发、Win10+（[NVAPI](https://developer.nvidia.com/nvapi)） |
| AMD ADLX | 含手动风扇调校 | 高 | 需 Radeon 驱动（[IADLXManualFanTuning](https://gpuopen.com/manuals/adlx/adlx-sdk-references/adlx-interfaces/gpu-tuning/iadlxmanualfantuning/)） |
| Intel IGCL | Engine/Fan/Telemetry/温度/功耗等 | 中高 | 二进制随驱动；相关 API 限 64 位（[IGCL](https://github.com/intel/drivers.gpu.control-library)） |
| WMI / ACPI-WMI | 主板/笔电温度、风扇 | 中 | 依机型；已有 ASUS/Dell WMI、Lenovo ACPI 插件（见 FanControl 插件表） |
| 存储 SMART IOCTL | 存储温度/寿命 | 高 | 纯用户态，LHM 已实现 |
| MSR / SMBus / SPD | CPU 电压、DIMM 温度、DRAM RGB | **需内核态** | 用户态不可行，走 PawnIO 模块 |
| SuperIO PWM | 主板风扇 PWM/转速 | **需内核态** | 无公开寄存器手册 |
| EC（笔记本） | 风扇/键盘灯 | 低 | FanControl FAQ：笔电大多不支持（[README](https://github.com/Rem0o/FanControl.Releases)） |
| HWiNFO SDK | 全量清单+传感器 | 中（付费） | 商用需 Pro 授权；非 Pro 共享内存仅 12h（[licenses](https://www.hwinfo.com/licenses/)、[SDK](https://www.hwinfo.com/sdk/)） |
| LibreHardwareMonitorLib | 传感器+风扇控制 | 高 | **MPL-2.0**、NuGet 可集成（[LICENSE](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LICENSE)），但自身依赖 PawnIO 驱动 |

厂商 SDK：**Corsair iCUE SDK** 官方开源 C/C++（[cue-sdk](https://github.com/CorsairOfficial/cue-sdk)）；**Razer Chroma** 已迁至 Wyvrn 文档站且“**不再积极维护**”，定位偏游戏内动画（[Chroma SDK](https://doc.wyvrn.com/docs/chroma-sdk/)）；**Logitech** 走 Developer Alliance 伙伴计划、页面未直接给 LED SDK（[Developer Alliance](https://www-gaming.logitech.com/en-us/programs/developer-alliance)）；**ASUS Aura SDK** 公开提供下载+FAQ+提交邮箱（[Aura SDK](https://www.asus.com/campaign/aura/us/SDK.php)）；**MSI Mystic Light SDK** 有公开 PDF（[MSI PDF](https://storage-asset.msi.com/files/pdf/Mystic_Light_Software_Development_Kit.pdf)）；**Gigabyte RGB Fusion SDK** 页面存在但本次抓取 403（[Gigabyte](https://www.gigabyte.com/tw/mb/rgb/sdk)）；Cooler Master/Kingston 未核实。

## 5. 灯效访问路径与通用标准

- **[已核实]** 主板 ARGB header 归属不统一：X370/Z270、X470/Z370 等老平台板载灯挂 **SMBus**；SMBus “本不是给用户态应用用的”，RGB 软件是例外（[OpenRGB SMBusAccess](https://github.com/CalcProgrammer1/OpenRGB/blob/master/Documentation/SMBusAccess.md)）。较新板卡多由 SuperIO/EC 或独立 USB 控制器管理——**[推测]** 依厂商而定。
- **[已核实]** **DRAM RGB 走 SMBus**（所有带灯 DDR4/DDR5）；地址冲突真实存在（如 Linux `spd5118` 占用 Kingston Fury DDR5 的 I2C 地址，令其他程序不可访问）。
- **[已核实]** 通用标准存在：Windows **Dynamic Lighting** 基于开放 **HID LampArray**（USB HID Usage Tables），可原生调亮度/效果/跨品牌同步，应用可作 ambient controller 抢占控制（[Dynamic Lighting devices](https://learn.microsoft.com/en-us/windows-hardware/design/component-guidelines/dynamic-lighting-devices)、[用户文档](https://support.microsoft.com/en-us/windows/hardware/input-devices/control-dynamic-lighting-devices-in-windows)）。**限制**：仅 **Windows 11**，且只覆盖实现 LampArray 的设备；[推测] GPU/键鼠/耳机灯效多为厂商私有 USB HID 协议。

## 6. 安全与法律风险

- **[已核实原则性]** SuperIO/EC 无公开手册，寄存器语义随型号/BIOS 变化；微软承认 blocklist 可致设备异常甚至蓝屏。风扇寄存器写错可致停转/满转，EC 写错可致关机/充电异常。本次**未找到可引用的“变砖”一手案例**（未核实），应视为结构性风险。
- **[已核实]** PawnIO “as is without warranty”、LHM 为 MPL-2.0（无担保）；终端用户保修/安全责任仍在自己的 EULA。
- **[推测，需律师]** 厂商 EULA 常含反向工程限制；绕过厂商软件直接驱动硬件有合同风险；DRAM/固件逆向在美国法下还可能涉 DMCA §1201 反规避（互操作性抗辩需具体分析）；各厂商 SDK 许可须逐份审阅。
- **[已核实]** 杀软态度是行业分水岭：同一已签名驱动会被 AV 从“工具”改判“Trojan”并连带隔离打包产品——产品设计须假设“暴露物理内存/端口 IO 的驱动都会被重点审查”。

## 7. 对产品化最关键的结论

1. **WinRing0 路线已死 [已核实]**：Defender 检出 + blocklist hash 规则 + Win11 默认开启，旧版 LHM/FanControl/OpenRGB 会失效；新项目不要打包 WinRing0。
2. **不写驱动也能交付，但有边界 [已核实+推测]**：GPU（NVAPI/ADLX/IGCL）、存储 SMART、WMI/ACPI-WMI、厂商灯效 SDK、HID LampArray 均无需自研驱动。
3. **SuperIO/EC/板载 ARGB/DRAM RGB 必须内核态 [已核实]**：这些正是 PawnIO `LpcIO`/`IsaBridgeEC`/`Smbus*` 模块覆盖范围。
4. **PawnIO 是事实标准替代 [已核实]**：LHM、FanControl、OpenRGB 均已迁移；驱动由作者做 Microsoft 签名，规避“自签驱动无法装载/被 blocklist”的最大坑。
5. **PawnIO 限制在“模块”而非“驱动” [已核实+推测]**：官方版校验模块签名，许可证要求模块 GPL 兼容。商业产品内核逻辑只有三条路：模块开源并上游化由作者签名 / 购买商业授权 / 自研驱动走 EV+attestation+（零售）WHQL。
6. **自研驱动合规可行、交付代价高 [已核实]**：须 Dev Portal + EV 证书；attestation 仅 Win10 Desktop、不能进零售 WU、不算 Certified；零售需 WHCP/HLK；test signing 禁止交付。
7. **灯效不存在“一个标准通吃” [已核实]**：Dynamic Lighting 仅 Win11 且只覆盖 LampArray；厂商 SDK 各自为政、需装厂商软件或合作。**灯效覆盖度是商务问题而非纯技术问题**。
8. **传感器应复用+付费兜底 [已核实]**：LHM 为 MPL-2.0 可直接集成（自带 PawnIO 依赖）；HWiNFO SDK 最全但商用付费。自研全量传感器不经济。
9. **风扇控制现实边界 [已核实]**：GPU 可行（ADLX 公开风扇调校、NVAPI 称含 cooling controls、IGCL 有 Fan API 限 64 位）；主板 SuperIO 只能靠 PawnIO；**笔记本 EC 基本不可交付**。
10. **真正门槛是信任与责任 [已核实]**：内核级写错=设备异常/蓝屏且组件作者免责，责任在自己 EULA；杀软对带驱动安装包敌意明显。应优先“已签名 PawnIO + 免驱动路径”，而非自造驱动。

## 8. 未覆盖/待核实

- Cooler Master/Kingston/NZXT/Lian Li 等 SDK 公开性与条款；ASUS Aura/MSI Mystic Light 的 PDF 条款未解析、Gigabyte 页面 403。
- SuperIO/EC 误写导致变砖/风扇失控的可引用案例（未找到一手来源）。
- PawnIO 商业授权价格与流程（需联系作者）；Logitech LED SDK 当前获取方式未确认；WinRing0 经 OEM 重签后能否绕过 hash blocklist（推测可以，无证据）。
