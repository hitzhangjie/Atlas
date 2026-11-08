# SignalRGB / Armoury Crate / OpenRGB 生态调研

> 2026-11-08。标记：**【已核实】**＝直接读到官方/仓库/文档；**【推测】**＝基于已核实事实的推断；**【无法核实】**＝无可信来源。

## 0. 结论速览

「signalrgb-pro 开源」**不成立**：SignalRGB 闭源，公开的只有 JavaScript 插件、翻译与厂商插件仓库。且它已同时做灯效 + 风扇/泵控制 + 传感器监控，「SignalRGB + Armoury Crate 二合一」并非功能空白，门槛在设备覆盖、稳定性与内容生态。

## 1. SignalRGB（Whirlwind FX）

**1.1 开源属性**
- **【已核实】闭源。** 运营主体 Whirlwind Virtual Realities Inc.；ToS §5.1 禁止逆向/反编译/提取源码，§6.1 声明软件与内容归 Whirlwind，许可为个人非商用有限许可：<https://signalrgb.com/terms/>。
- **【已核实】无官方 `signalrgb-pro` 仓库。** GitHub 搜该词的 34 个结果均为第三方插件/桥接；GitLab 官方组只有 `signal-plugins`、`signal-translations` 与厂商插件仓库（LianLi、MSI、Govee、Nanoleaf 等），无应用源码：<https://gitlab.com/groups/signalrgb/-/projects>。

**1.2 订阅模式**
- **【已核实】** Pro 为自动续费订阅（ToS §4）；另有 Light Points 虚拟货币与虚拟内容内购；新 Pro 订阅 72 小时退款，Light Points 一般不可退。<https://docs.signalrgb.com/guides/account-billing/refund-policy/>
- **【已核实】** Pro 权益：专属灯效/游戏集成/视频 lightscripts、**用 CPU/GPU/内存传感器的系统监控、支持的风扇控制器上的风扇控制**、逐设备色彩校准、无限宏、LCD 界面。<https://docs.signalrgb.com/guides/account-billing/about-pro-features/>
- **【无法核实】具体价格**：定价页为动态加载：<https://signalrgb.com/pricing/>。

**1.3 插件机制**
- **【已核实】** 单文件 **JavaScript**，按 VID/PID 匹配 USB 设备，导出设备描述与 initialize/render/shutdown；内置插件在 `%localappdata%\VortxEngine\app-<版本>\Signal-x64\plugins`，用户插件在 `Documents\WhirlwindFX\Plugins` 且**覆盖同名内置插件**、跨版本保留；官方称源码可读、欢迎 PR。<https://docs.signalrgb.com/developer/plugins/>、<https://docs.signalrgb.com/developer/plugins/how-is-a-plugin-loaded-/>
- **【已核实】** 公开文档**未见签名/审核规范或沙箱说明**；插件 API 直接暴露 HID/Raw USB、串口、TCP/UDP、DTLS。<https://docs.signalrgb.com/developer/plugins/advanced-communication/>

**1.4 架构线索**
- **【已核实】** 安装目录 `VortxEngine/app-<版本>/Signal-x64/`（含 `components/`、`plugins/`、`Logs/`）；Lightscripts 是 **HTML + JS canvas**（「效果即网页」）：<https://docs.signalrgb.com/guides/installation-setup/file-locations/>、<https://docs.signalrgb.com/developer/lightscripts/it-s-a-webpage/>。
- **【已核实】** 设置页有 **Internal Driver** 与「Enable Onboard Devices」，驱动被阻止时须管理员运行一次；论坛案例通过切 **DX11** 渲染解决高负载崩溃；已有 **Linux Beta**（Arch/Debian）：<https://docs.signalrgb.com/troubleshooting/software-conflicts/blocked-drivers/>、<https://forum.signalrgb.com/t/signal-rgb-closes-when-the-cpu-is-under-hevay-load-i-e-stress-testing-benchmarking/10113>、<https://signalrgb.com/linux/>。
- **【推测】** Internal Driver 应为自研签名驱动（Defender 的 WinRing0 名单不含它），具体实现未核实。

**1.5 稳定性痛点**
- **【已核实】** 官方「已知冲突程序」清单含 Armoury Crate/AURA、iCUE、Synapse、Mystic Light、G Hub 等 12+ 项；故障页覆盖 Freezing/Crashing、Memory、Stuttering、Device Not Detected、Service Issues：<https://docs.signalrgb.com/troubleshooting/software-conflicts/conflicting-programs/>、<https://docs.signalrgb.com/troubleshooting/>。
- **【已核实】** 论坛 2026-09 实例：CPU 满载时进程关闭（灯灭、风扇降速），支持团队承认工单积压；官方称设备「数千款」、集成 50+ 游戏、插件仓库有 40 个厂商目录。<https://forum.signalrgb.com/t/signal-rgb-closes-when-the-cpu-is-under-hevay-load-i-e-stress-testing-benchmarking/10113>、<https://signalrgb.com/devices/>

## 2. Armoury Crate（ASUS）

- **【已核实】** ASUS 官方承认其为**模块化 + 多服务**架构：只下载当前硬件所需组件，「多个独立服务分别管理不同硬件」；不能用 Windows 添加/删除程序干净卸载，必须用官方 **Uninstall Tool**；Aura Creator 为独立可选项，旧 Aura Sync 已并入。<https://rog.asus.com/us/articles/guides/how-to-cleanly-uninstall-and-reinstall-armoury-crate-2/>
- **【已核实】** 服务命名（G-Helper 源码）：`ArmouryCrateService`、`ArmouryCrateSEService`、`ArmouryCrateControlInterface`、`LightingService`，以及 `ASUSSoftwareManager`、`ASUSLiveUpdateAgent`、`AsusCertService`、`ASUSSystemAnalysis/Diagnosis` 等。<https://github.com/seerge/g-helper/blob/main/app/Helpers/AsusService.cs>
- **【已核实（第三方证据）】** 社区 debloat 项目记录卸载残留、DriverStore 幽灵驱动、Windows Update 静默重装，并关联卡死、高 CPU、BSOD；G-Helper（GPL-3.0，**15,945 stars**）以「轻量替代、功能几乎相同」为定位。<https://github.com/Romdhani-void/crystal-clear-asus>、<https://github.com/seerge/g-helper>
- **【推测/无法核实】** 「主程序 .NET/WPF」仅为社区说法，无权威来源。

## 3. OpenRGB

- **【已核实】GPL-2.0**（官网写明 GNU GPL v2）：<https://github.com/CalcProgrammer1/OpenRGB>、<https://openrgb.org/>。
- **【已核实】已迁 Codeberg**：1.0（2026-09-12，近 3 年工作、配置系统重做、后台服务、PawnIO、Qt6）、1.1（2026-10-16，bugfix）；5,898 commits、C++ 97.9%；GitHub 镜像 4,675 stars/482 forks；1.1 Windows x64 MSI 约 3 周下载 **207,744** 次：<https://codeberg.org/OpenRGB/OpenRGB/releases/tag/release_1.0>、<https://codeberg.org/OpenRGB/OpenRGB/releases/tag/release_1.1>、<https://github.com/CalcProgrammer1/OpenRGB>。
- **【已核实】设备覆盖**：官网按版本维护 Supported Devices（显卡/主板/内存/键鼠/灯带/散热/机箱等），ArchWiki 称 **2,500+ 设备**：<https://openrgb.org/devices_1.1.html>、<https://wiki.archlinux.org/title/OpenRGB>。
- **【已核实】Windows 访问路径**：**不自带驱动**，需另装 **PawnIO** 才能访问 RAM、主板与部分设备，且须管理员运行或装 PawnIO 后台服务；代码含 `i2c_smbus/`、`super_io/`、`wmi/`、`hidapi_wrapper/`、`serial_port/`、`SPDAccessor/`（SMBus/I2C、Super I/O、WMI、USB HID/串口、PCI 空间）：<https://codeberg.org/OpenRGB/OpenRGB/releases/tag/release_1.1>、<https://pawnio.eu/>、<https://codeberg.org/OpenRGB/OpenRGB>。
- **【已核实】** 核心只做灯效；风扇控制由 **Fan Sync Plugin** 提供，传感器同步由 Hardware Sync Plugin 提供：<https://openrgb.org/plugin_fan_sync.html>。
- **【已核实】风险**：README 警告逆向协议「过去确有设备被损坏/变砖案例」，只能修复或禁用相关代码：<https://gitlab.com/CalcProgrammer1/OpenRGB/-/raw/master/README.md>；ArchWiki 记录个别 GPU 曾致系统不稳定（已修复）：<https://wiki.archlinux.org/title/OpenRGB>。
- **【已核实】驱动合规**：Microsoft Defender 将 **WinRing0** 标为 `VulnerableDriver:WinNT/Winring0`（CVE-2020-14979），受影响含 OpenRGB、LibreHardwareMonitor、HWiNFO、MSI Afterburner、FanCtrl、SteelSeries Engine 等：<https://support.microsoft.com/en-us/windows/security/threat-malware-protection/microsoft-defender-antivirus-alert-vulnerabledriver-winnt-winring0>。

## 4. 其他项目

| 项目 | 开源/许可 | 能力边界（已核实） |
| --- | --- | --- |
| LibreHardwareMonitor | **MPL-2.0**，C#，9,347 stars | 只做传感器监控（温度/风扇转速/电压/负载），提供 NuGet 库；FanControl 的硬件后端 |
| FanControl | **闭源**（README 明说），免费，.NET | 风扇校准/曲线/混合曲线强；插件为 .NET DLL 放入目录；无灯效 |
| HWiNFO | 闭源免费（商用需 Pro） | 个人非商用免费；Pro 订阅 $29/年起、终身 $199；监控/诊断 |
| AIDA64 | 闭源商业 | 诊断/监控/稳定性测试/LCD；v7.60 起内核驱动与 Microsoft 合作重做 |
| WLED | **EUPL-1.2**，18,995 stars，ESP32 固件 | 走网络控制灯带，是联动对象，不做 PC 外设/主板灯效 |
| Artemis | **PolyForm Noncommercial 1.0.0**：源可用但**非 OSI、禁商用** | C#，1,214 stars，仍维护（2026-10-27 有提交），商业不可直接复用 |
| G-Helper | **GPL-3.0**，C#，15,945 stars | ASUS 笔记本的 Armoury Crate 轻量替代（风扇/功耗/MUX/灯效） |
| PawnIO | 开源（官方签名版 + 无签名版） | 可脚本化通用内核驱动（Pawn 语言），OpenRGB 采用的硬件访问层 |

链接：<https://github.com/LibreHardwareMonitor/LibreHardwareMonitor>、<https://getfancontrol.com/>、<https://www.hwinfo.com/licenses/>、<https://www.aida64.com/>、<https://github.com/wled/WLED>、<https://github.com/Artemis-RGB/Artemis>（License：<https://raw.githubusercontent.com/Artemis-RGB/Artemis/master/LICENSE>）、<https://github.com/seerge/g-helper>、<https://pawnio.eu/>。**【无法核实】Athena**：`Artemis-RGB/Athena` GitHub API 返回 404，无仓库/官网。

## 5. 结论：自研取舍

**可直接复用**：传感器层用 **LibreHardwareMonitor（MPL-2.0）** 的 NuGet 库（文件级 copyleft，可链接进闭源主程序）；内核访问走 **PawnIO**（开源 + 官方签名），避免自签 WinRing0 被 Defender 拉黑；风扇交互参考 **FanControl（闭源）**，实现自研。**OpenRGB 是 GPL-2.0，不可链接进闭源产品**，只能参考协议或用其网络 SDK 解耦集成；WLED/QMK 可作联动目标。

**必须自研**：多厂商 USB/私有协议与设备指纹库（SignalRGB 核心资产是 40+ 厂商目录、数千设备的 VID/PID 与初始化报文，每型号需实测）；冲突治理（与 Armoury Crate/iCUE/Synapse 争同一设备是结构性问题，需要单写者/接管/崩溃恢复）；稳定低占用渲染（SignalRGB 自身高负载崩溃、内存/卡顿投诉说明「效果引擎 + 高刷新写入」很难）；驱动与合规（SMBus/EC/PCI 内核访问是最大法律/安全/兼容风险）。

**SignalRGB 护城河**：不是内核技术（OpenRGB 已做到类似访问），而是**设备覆盖 + 内容生态（效果市场、50+ 游戏集成、Light Points + Pro 订阅）+ 品牌合作与社区**；Armoury Crate 的护城河是与 ASUS 硬件/BIOS/EC 绑定。**【推测】** 正面拼设备数量短期追不上；更现实路线是与 OpenRGB SDK/PawnIO 互操作，聚焦少数高价值品牌的深度控制与稳定性。

## 6. 要点摘要（8 条）

1. **「signalrgb-pro 开源」是错误说法**：SignalRGB 闭源，ToS 禁止逆向/提取源码；官方只公开 JS 插件、翻译、厂商插件仓库。→ <https://signalrgb.com/terms/>、<https://gitlab.com/groups/signalrgb/-/projects>
2. **插件体系**：单文件 JS、按 VID/PID 匹配、用户插件覆盖内置、公开仓库收 PR；未见签名/沙箱文档，但 API 直连 USB/HID/串口/TCP/UDP/DTLS。→ <https://docs.signalrgb.com/developer/plugins/>、<https://docs.signalrgb.com/developer/plugins/how-is-a-plugin-loaded-/>
3. **SignalRGB 已不只做灯效**：Pro 含系统监控与风扇/泵控制（校准、曲线、Cooling profiles），与 Armoury Crate 重叠比预期大。→ <https://docs.signalrgb.com/guides/advanced-features/fan-control/>、<https://docs.signalrgb.com/guides/account-billing/about-pro-features/>
4. **Armoury Crate 是官方承认的多服务/模块化软件**，须专用 Uninstall Tool 才清干净；服务含 `ArmouryCrateService`、`LightingService`、`ArmouryCrateControlInterface` 及多个 ASUS 更新/诊断服务。→ <https://rog.asus.com/us/articles/guides/how-to-cleanly-uninstall-and-reinstall-armoury-crate-2/>
5. **OpenRGB 最新态**：GPL-2.0；已迁 Codeberg；1.0（2026-09）、1.1（2026-10）；2,500+ 设备；Windows 需另装 PawnIO；风扇/传感器靠插件。→ <https://codeberg.org/OpenRGB/OpenRGB/releases/tag/release_1.1>、<https://wiki.archlinux.org/title/OpenRGB>
6. **WinRing0 被 Defender 列为易受攻击驱动（CVE-2020-14979）**，OpenRGB/LHM/HWiNFO/Afterburner 等均受影响——「自研内核驱动」是合规风险而非护城河。→ <https://support.microsoft.com/en-us/windows/security/threat-malware-protection/microsoft-defender-antivirus-alert-vulnerabledriver-winnt-winring0>
7. **许可证陷阱**：LHM=MPL-2.0（可闭源链接）、OpenRGB=GPL-2.0（不可）、WLED=EUPL-1.2、**Artemis=PolyForm Noncommercial（禁商用，非 OSI）**、FanControl/ HWiNFO/ AIDA64=闭源。
8. **用户痛点真实且可量化**：SignalRGB 官方列 12+ 冲突程序并有崩溃/内存/卡顿故障页；Armoury Crate 有 15.9k stars 的 G-Helper 与多个 debloat 项目——「稳定、不冲突、低占用」本身就是差异化卖点。→ <https://docs.signalrgb.com/troubleshooting/software-conflicts/conflicting-programs/>、<https://github.com/seerge/g-helper>

### 附：未核实项

- SignalRGB Pro 具体价格（动态定价页）。
- SignalRGB Internal Driver 的具体驱动名/签名方（仅确认机制与管理员要求）。
- Armoury Crate 是否 .NET/WPF（仅社区说法）。
- 「Athena」（GitHub API 404，无可核实仓库）。
- OpenRGB 精确贡献者人数。
