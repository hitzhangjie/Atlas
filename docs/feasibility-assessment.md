# Atlas 可行性评估：Windows 一体化硬件控制软件（传感器 / 风扇 / 灯效）

> **评估对象**：在 Windows PC 上自研一个类似 Armoury Crate / SignalRGB 的软件，覆盖传感器读取、风扇控制、灯效控制（下称 "Atlas"）。
>
> **评估日期**：2026-11-08　**评估人**：Lead（含 2 名调研队友，原始笔记见 `research/`）
>
> **证据约定**：文中带链接的陈述为「已核实」（官方文档 / 仓库 / 许可证 / Microsoft Learn / 厂商页面）；标注「估算」或「判断」的为本文推断，不是事实。本文不构成法律意见。

---

## 0. 结论先行

**一句话：能做，但「底层控制应该不是很复杂」这个判断只对一半成立。**
传感器和 x86 主板风扇控制，属于「有成熟轮子可复用」的中等难度工程；**灯效控制是协议逆向 + 设备矩阵 + 长期维护的长尾**，一个人短期几乎不可能追平。真正的成本不在写代码，而在**设备覆盖、冲突治理、安全责任和长期维护**。

### 0.1 分模块评级

| 能力 | 难度 | 可行性 | 主要技术路径 | 结论 |
| --- | --- | --- | --- | --- |
| 传感器读取（温度/风扇转速/电压/功耗/负载） | ★★☆☆☆ | 高 | `LibreHardwareMonitor`（MPL-2.0，NuGet）+ PawnIO；GPU 走 NVAPI / ADLX / IGCL；存储走 SMART IOCTL | 可直接复用，不要自研 |
| x86 桌面主板风扇控制 | ★★★☆☆ | 中高 | PawnIO `LpcIO`（SuperIO PWM）+ LHM `IControl` | 能控，但必须做安全兜底 |
| 笔记本风扇/EC 控制 | ★★★★☆ | 低 | 厂商 ACPI/WMI（如 ASUS System Control Interface）；EC 直接写基本不可交付 | 不建议作为首批目标 |
| GPU 风扇控制 | ★★★☆☆ | 中高 | NVAPI cooling controls / AMD ADLX manual fan tuning / Intel IGCL | 可行，但各家 API 差异大 |
| 灯效控制（主板 ARGB + 内存 + 外设） | ★★★★★ | 低（全品类）/ 中（单品牌） | USB HID、SMBus、SuperIO、厂商 SDK、Windows Dynamic Lighting | 长尾极长，是项目主要风险 |
| 统一 UI / 场景 / 稳定性 | ★★★☆☆ | 中 | Windows 服务 + 用户态 UI + IPC；WebView2 / Direct2D 效果引擎 | 工作量可控，但是差异化关键 |
| 内核驱动 | ★★★★☆ | 不建议 | 复用 PawnIO；自研驱动需 EV 证书 + Microsoft 签名 + 零售 WHQL | 不是技术壁垒，是合规负担 |

### 0.2 三个建议

1. **不要把目标定义成「重写 SignalRGB + Armoury Crate」。** SignalRGB 本体闭源，且早已不止灯效——Pro 档位包含系统监控和风扇/泵控制，还有 Linux Beta。正面拼设备数量不现实。
2. **先做「编排层 / 聚合层」MVP**：复用 `LibreHardwareMonitor` + OpenRGB + PawnIO + Windows Dynamic Lighting + 厂商 SDK，自己做统一 UI、场景联动、稳定性和中文体验。这是用约 1/10 成本拿到 80% 体验的路线。
3. **先花 2 周做 spike**：验证「你自己的硬件」能否被读到、被控到、崩溃后能否恢复。再决定是否投入 3-6 个月。

### 0.3 三种目标，三种结论

| 你的真实目标 | 建议 | 量级 |
| --- | --- | --- |
| 只解决自己机器的痛点 / 学习练手 | **值得做**，按 Phase 0 → 1 走 | 业余 3-6 个月 |
| 小圈子开源工具（2-3 个硬件家族、几十个用户） | **可以做**，但要接受维护负担 | +2-4 个月 |
| 商业产品，替代 SignalRGB / Armoury Crate | **不建议 solo 全品类**；要么垂直聚焦，要么只做编排层 | 3-5 人 × 12-24 个月，且需要硬件实验室 |

---

## 1. 先纠正一个事实：SignalRGB 并不开源

这是评估中最重要的事实修正，直接影响策略：

- **SignalRGB（Whirlwind FX / Whirlwind Virtual Realities Inc.）是闭源商业软件**，其 ToS 明确禁止逆向工程、反编译或尝试提取源码；付费形式是 **Pro 自动续费订阅**（另有 Light Points 虚拟内容）。来源：[signalrgb.com/terms](https://signalrgb.com/terms/)。
- 官方公开的只有**周边仓库**：JavaScript 设备插件（[GitLab signalrgb/signal-plugins](https://gitlab.com/signalrgb/signal-plugins)）、翻译、按厂商拆分的插件仓库。**没有官方 `signalrgb-pro` 应用源码仓库**；GitHub / GitLab 上同名的都是第三方插件、桥接或非官方项目。如果你看到的是「SignalRGB Pro」，那是**付费档位**，不是开源项目。
- **真正的开源同类是 OpenRGB（GPL-2.0）**：[GitLab](https://gitlab.com/CalcProgrammer1/OpenRGB) / [GitHub 镜像](https://github.com/CalcProgrammer1/OpenRGB)。
- 即使拿到 SignalRGB 插件 JS，也**没有明确开源许可**（插件仓库无 license 字段），且 ToS 禁止逆向，不能直接抄，只能作为协议研究参考。
- **警惕假冒仓库**：网上能搜到 `SignalRGB-Setup/SignalRGB-Pro` 这类「看起来像官方源码」的仓库，经核实是**假冒**——内容实为 OpenRGB + Artemis 代码重贴标签，README 是 SEO 关键词堆砌，下载链接指向站外 `github.io`（详见附录 C）。这恰好说明「SignalRGB 开源」的信息在搜索引擎里被污染了。
- **另一个反直觉的事实**：SignalRGB 今天已经同时具备**灯效 + 系统传感器监控 + 风扇/泵控制**（校准、曲线、Cooling profiles），见 [Pro Features](https://docs.signalrgb.com/guides/account-billing/about-pro-features/) 与 [Cooling 文档](https://docs.signalrgb.com/guides/advanced-features/fan-control/)。所以「SignalRGB + Armoury Crate 二合一」并不是市场空白，差距主要在**设备覆盖 + 内容生态（效果市场、50+ 游戏集成、订阅体系）**。

> **判断**：如果你的动机是「SignalRGB 不稳定，所以自己写一个更稳的」，这个动机成立；如果动机是「SignalRGB 是开源的，我可以直接改」，这个前提不成立。

---

## 2. 你的三个假设，逐条校准

### 2.1 「传感器读取应该不难」——基本成立 ✅

- 已经有成熟轮子：**LibreHardwareMonitor（LHM，MPL-2.0）**，C#/.NET，NuGet 包 `LibreHardwareMonitorLib`（0.9.6，累计下载约 94 万），覆盖 CPU（MSR/DTS）、GPU（NVAPI/ADL/IGCL）、主板 SuperIO/EC、内存、存储、电池等。来源：[GitHub](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)、[NuGet](https://www.nuget.org/packages/LibreHardwareMonitorLib)。
- LHM 已在 2025-09 完成 **WinRing0 → PawnIO** 的迁移（[PR #1857](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/pull/1857)），最新 0.9.6 已带 PawnIO 模块。
- **前提条件**：必须安装 PawnIO（管理员/服务）、进程需要足够权限；不同主板传感器定义不同，LHM 靠持续 PR 维护（见 §3 规模数据）。

### 2.2 「风扇控制应该不难」——写不难，保证不出事难 ⚠️

- 桌面主板：SuperIO（Nuvoton / ITE / ENE / Fintek）的 PWM 寄存器写入，代码量不大；LHM 已封装大部分控制逻辑。
- 难点在**安全与状态机**：
  - 风扇启动/停转阈值（stall speed）、最低占空比、曲线迟滞、斜坡限制；
  - 温度源失效时怎么办（默认全速）；
  - 软件崩溃/退出后如何**恢复 BIOS 默认控制**；
  - 笔记本 EC 写入风险高，FanControl 官方 FAQ 也承认大多数笔记本不支持（见 [FanControl.Releases](https://github.com/Rem0o/FanControl.Releases)）。
- 一个真实案例：HWiNFO 读 ASUS 老主板 EC 导致机箱风扇停转，用户只能重启进 BIOS 重置 Q-Fan（[HWiNFO 论坛 2015](https://hwinfo.com/forum/threads/fans-stop-spinning-after-running-hwinfo.1669/)）。**读都能出问题，写更要有兜底。**

### 2.3 「灯效控制应该不难」——这是最难的一块，而且难在非技术 ❌

- 设备访问路径高度碎片化：

| 设备类型 | 实际访问路径 | 是否需要内核态 |
| --- | --- | --- |
| 主板板载 ARGB / 老平台板载灯 | SMBus（I2C）、SuperIO、部分 USB HID 控制器 | 多数需要（SMBus/SuperIO） |
| 内存 RGB（DDR4/DDR5） | SMBus（与 SPD 同总线，存在地址冲突） | 需要 |
| 显卡 RGB | 厂商私有 USB HID / 部分走 SMBus | 多数用户态 |
| 键鼠 / 耳机 / 水冷 / 风扇控制器 | USB HID 私有协议、串口 | 用户态 |
| Windows Dynamic Lighting 设备 | HID LampArray（Windows 11 原生） | 用户态 |

- **没有统一标准**：Windows Dynamic Lighting 只覆盖实现 HID LampArray 的设备，且仅 Windows 11（[Microsoft Learn](https://learn.microsoft.com/en-us/windows-hardware/design/component-guidelines/dynamic-lighting-devices)），不覆盖主板 ARGB header、内存、绝大多数老设备。
- **覆盖规模的真实量级**：OpenRGB 做了约 7 年，支持 **2,500+ 设备**（[官方设备列表](https://openrgb.org/devices_1.1.html)、[ArchWiki](https://wiki.archlinux.org/title/OpenRGB)）；SignalRGB 号称数千设备、40 个厂商插件目录。每款设备都需要 **VID/PID + 初始化报文 + LED 拓扑映射 + 真机测试**。
- **结论**：灯效的「难度」是**长尾适配 + 商务合作**问题，不是算法问题。你能很快控制自己那一条灯带，但「支持市面上大部分设备」是另一个人年量级的工作。

### 2.4 「底层控制应该不是很复杂」——2026 年的 Windows 已经不是 🚫

- **WinRing0 路线已死**：Microsoft Defender 将其列为 `VulnerableDriver:WinNT/Winring0`（CVE-2020-14979），官方点名 OpenRGB、LibreHardwareMonitor、HWiNFO、MSI Afterburner 等受影响（[Microsoft Support](https://support.microsoft.com/en-us/windows/security/threat-malware-protection/microsoft-defender-antivirus-alert-vulnerabledriver-winnt-winring0)）；Windows 11 2022 update 起 **vulnerable driver blocklist 默认启用**，且我们实际解包 blocklist 确认其中已有 WinRing0 的 **SHA1/SHA256 hash 拒绝规则**（[Microsoft Learn](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/design/microsoft-recommended-driver-block-rules)）。重新打包同一个驱动也没有用。
- **自研内核驱动的门槛**：需要 EV 代码签名证书 + Microsoft Partner Center；attestation 签名**不能用于零售 Windows Update、不算 Windows Certified**，零售需 WHCP/HLK；test signing 明确禁止交付（[签名选项](https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/driver-signing-offerings)、[test signing](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/introduction-to-test-signing)）。
- **PawnIO 是当前事实标准**：签名驱动 + Pawn 脚本模块，LHM、FanControl、OpenRGB 均已迁移（[pawnio.eu](https://pawnio.eu/)）。但有两条硬约束：
  1. 官方签名版会**校验模块签名**（驱动内做 SHA-256 + RSA 校验），目前模块签名由作者掌握；无签名版（Unrestricted edition）才关闭校验；
  2. 许可证是 **GPL-2.0 + 特殊例外**，要求所有加载进 PawnIO 的模块 **GPL 兼容**（建议 LGPL-2.1）。
- **对产品的含义**：想写自己的内核逻辑，只有三条路——模块开源并上游化由作者签名、购买 PawnIO 商业授权、自研驱动走 EV + attestation +（零售）WHQL。**「自研驱动」不是技术壁垒，是合规与维护负担。**

---

## 3. 竞品现状：为什么它们不稳

### 3.1 关键事实

| 项目 | 许可 | 能力 | 规模信号（2026-11） | 主要痛点 |
| --- | --- | --- | --- | --- |
| **SignalRGB** | 闭源商业（Pro 订阅） | 灯效 + 风扇/泵 + 传感器监控 + 游戏集成 + Linux Beta | 官方称数千设备、40 厂商插件目录 | 需联网 + 管理员；官方列出 12+ 冲突程序；自身有崩溃/内存/卡顿故障页 |
| **Armoury Crate** | 闭源 | 灯效、风扇、性能模式、更新 | G-Helper 替代品 15.9k star | 模块化多服务、卸载困难、残留/重装/占用问题 |
| **OpenRGB** | GPL-2.0 | 灯效为主（风扇靠 Fan Sync 插件） | 2,500+ 设备；1.1 发布约 3 周 Windows 下载 20 万+ | 协议逆向，README 明确警告可能损坏/变砖；新设备覆盖靠社区 |
| **LibreHardwareMonitor** | MPL-2.0 | 传感器 + 风扇控制，不做灯效 | 9.3k star、1,054 fork、540 open issues | 主板适配靠持续 PR；WinRing0 迁移后部分 EC 功能回退 |
| **FanControl** | 闭源免费（许可禁止逆向） | 风扇曲线/校准/混合曲线 | 免费、用户多 | 不做灯效；笔记本支持有限 |
| **G-Helper** | GPL-3.0 | ASUS 笔记本的性能/风扇/灯效/MUX | **15.9k star、350K+ 活跃用户、1,110 万+ 下载（官方口径）** | 依赖 ASUS 自带 System Control Interface 驱动/WMI；仅 ASUS |
| **HWiNFO / AIDA64** | 闭源商业 | 传感器/诊断 | HWiNFO Pro 订阅 $29/年起 | 不做灯效；SDK 商用付费 |

> 数据来源：SignalRGB 文档与 ToS、G-Helper 官网/仓库、OpenRGB README/Codeberg Release、LHM GitHub API、FanControl Releases、HWiNFO 许可证页。详见附录。

### 3.2 不稳定的四个结构性原因

1. **单写者冲突**：同一设备只能有一个程序写。厂商软件、Armoury Crate、iCUE、Windows Dynamic Lighting、你的软件互相抢设备。SignalRGB 官方冲突清单列了 ASUS、ASRock、Corsair、Gigabyte、Logitech、MSI、NZXT、Razer、SteelSeries、Windows DL、Avast/AVG 等（[官方文档](https://docs.signalrgb.com/troubleshooting/software-conflicts/conflicting-programs/)）。
2. **内核驱动被拦**：驱动加载失败、被 Defender 隔离、被 blocklist 拒绝；SignalRGB 也有 "Blocked Internal Drivers" 专门排障页（[文档](https://docs.signalrgb.com/troubleshooting/software-conflicts/blocked-drivers/)）。
3. **长尾设备协议**：VID/PID 相同但固件版本不同、初始化序列不同；写错就是灯错、闪烁、无响应，甚至变砖。
4. **常驻服务 + 高频渲染开销**：效果引擎 + 高频写设备会带来 CPU/内存/卡顿问题；SignalRGB 论坛有高负载下进程退出、用户改 DX11 渲染才恢复的案例。

> **关键判断**：这四条根因不会因为「我们自己重写」而消失，只会转移到我们身上。产品能否稳定，取决于**设备矩阵测试 + 冲突治理设计 + 优雅降级**，而不是代码写得多漂亮。

---

## 4. 建议架构

### 4.1 总体分层

```
┌──────────────────────────── Atlas.App（用户态 UI） ────────────────────────────┐
│  WPF/WinUI3 主界面     WebView2 效果画布/社区效果     曲线编辑/场景/布局       │
└───────────────────────────────▲───────────────────────────────────────────────┘
                     Named Pipe / gRPC（ACL 限制为当前用户 + 管理员）
┌───────────────────────────────┴───────────────────────────────────────────────┐
│ Atlas.Service（Windows Service，LocalSystem，无 UI 依赖）                      │
│  SensorHub → FanEngine → LightingEngine → ProfileStore → Watchdog / 日志       │
└───────┬────────────────────────┬────────────────────────┬─────────────────────┘
        │                        │                        │
 LibreHardwareMonitor      OpenRGB SDK（TCP）/           PawnIO（已签名驱动）
 （MPL-2.0，NuGet）          Windows Dynamic Lighting      官方模块 / 自研模块
        │                  / 厂商 SDK / HID 插件          │
   CPU/GPU/主板/存储        RGB 设备/灯带/外设            SuperIO/EC/SMBus/MSR
```

**设计原则**：

- **UI 不碰硬件**：所有硬件 I/O 在服务进程；UI 崩溃不影响风扇/灯效。
- **单写者声明**：每个设备有明确的 owner 和接管策略；检测到厂商软件运行时降级为只读并提示，而不是硬抢。
- **最小权限**：服务 LocalSystem；UI 普通用户；IPC 用 named pipe + ACL。
- **故障隔离**：设备 Provider 可独立禁用；单个设备异常不拖垮全局。
- **可回放**：记录每次寄存器/报文写入，便于复现和恢复。

### 4.2 传感器层

- 首选 `LibreHardwareMonitorLib`（MPL-2.0，可闭源链接，保留版权声明，修改过的 MPL 文件需公开）。
- 不要试图自研全量传感器：CPU 微架构/主板 SuperIO/GPU 厂商 API 的组合是另一个 LHM 级项目。
- 备选：HWiNFO SDK（商用需 Pro 授权）、GPU 专用 API（NVAPI/ADLX/IGCL）。

### 4.3 风扇层（安全设计清单）

1. 写前读：只改 PWM 使能位和占空比，不动其他寄存器；
2. 保存原始值，退出 / 崩溃 / 服务停止时恢复；
3. 校准 start/stall 转速，设置最低占空比；
4. 曲线带迟滞和斜坡限制，避免转速抖动；
5. 温度源失效 → 全速（fail-safe）；
6. 独立 watchdog 线程：服务心跳丢失 / 温度超阈值 → 强制全速；
7. 设备黑名单 + 机型白名单：未验证机型默认只读；
8. 笔记本 EC：**首版不做**。

### 4.4 灯效层：四条路径，推荐组合

| 路径 | 覆盖 | 工作量 | 推荐度 |
| --- | --- | --- | --- |
| Windows Dynamic Lighting（HID LampArray） | Win11 新设备（键鼠等），不含主板 ARGB | 低 | ★★★★ 作为起点 |
| OpenRGB SDK（独立进程 / TCP） | 2,500+ 设备 | 低（协议客户端） | ★★★★★ 作为主力后端 |
| 厂商 SDK（Corsair iCUE、ASUS Aura、MSI Mystic Light 等） | 对应品牌，需装厂商软件/合作 | 中 | ★★★ 按需接 |
| 自研 HID 协议插件 | 自己的设备 | 每款 3-10 天 | ★★ 第二阶段 |

- **效果引擎**：核心效果用 Direct2D / Skia 在服务侧或独立渲染进程做；社区/用户自定义效果用 WebView2（HTML5 + Canvas），这也是 SignalRGB 的做法（其效果本质是网页，[官方文档](https://docs.signalrgb.com/developer/lightscripts/it-s-a-webpage/)）。
- **帧率**：30-60 fps 上限，按设备合并写入；HID 延迟高的设备降到 10-20 fps。
- **冲突治理**：检测厂商软件进程（Armoury Crate / LightingService / iCUE / Synapse / MSI Center / RGB Fusion 等），提供「让出 / 接管 / 共存」三档策略。
- **插件治理**：SignalRGB 的插件 API 可直连 HID / Raw USB / 串口 / TCP/UDP / DTLS，且加载不需要签名或沙箱（[官方文档](https://docs.signalrgb.com/developer/plugins/advanced-communication/)）——扩展快，但供应链风险由用户承担。Atlas 若做插件体系，应默认沙箱化 + 权限声明 + 可选签名。

### 4.5 驱动策略：三选一

| 方案 | 成本 | 风险 | 建议 |
| --- | --- | --- | --- |
| A. 复用 PawnIO（官方签名版 + 官方模块） | 低 | 模块受 GPL 约束；功能受模块覆盖限制 | **首版首选** |
| B. 自研 PawnIO 模块并上游化，由作者签名 | 中 | 需作者接受、GPL 兼容 | 长期可选 |
| C. 自研内核驱动（EV + attestation + WHQL） | 高（证书/时间/维护） | Defender/blocklist/AV、蓝屏责任 | **不建议首版** |

> 注：若使用 PawnIO 官方版，第三方模块需作者签名；若自研模块，必须 GPL 兼容（建议 LGPL-2.1）。商业闭源产品如需私有内核逻辑，需联系作者购买商业授权（[pawnio.eu](https://pawnio.eu/)）。

### 4.6 技术栈建议

- **语言**：C# / .NET 8+（服务 + UI 生态最成熟，LHM 是 C#，G-Helper 也证明可行）；仅在必须写 PawnIO 模块或原生 HID 封装时用 C/C++/Pawn。
- **UI**：WPF（成熟、稳定、Windows 原生）或 WinUI 3（现代但坑多）；跨平台诉求出现再考虑 Avalonia。
- **效果渲染**：WebView2 + Canvas（生态快）/ Direct2D（性能好）。
- **IPC**：Named Pipe + JSON/MessagePack；设备多、数据量大时可上 gRPC。
- **配置**：JSON 文件 + 场景/配置文件热加载；大型设备拓扑可 SQLite。
- **安装与更新**：MSIX 或 WiX；自动更新用 Velopack/Squirrel；代码签名可用 SignPath（G-Helper 这类开源项目即由 SignPath 免费签名）。
- **可观测性**：结构化日志、崩溃转储、匿名遥测（可选，默认关闭）。

### 4.7 稳定性设计的优先级

1. 服务崩溃不导致风扇失控（watchdog + 恢复默认）
2. 设备热插拔/休眠唤醒不崩溃
3. 与厂商软件共存不打架
4. GPU 驱动更新/系统更新后不失效
5. UI 卡顿不影响效果与风扇

---

## 5. 许可证与合规：能抄什么、不能抄什么

| 组件 | 许可证 | 能否用于闭源商业产品 | 注意事项 |
| --- | --- | --- | --- |
| LibreHardwareMonitor | **MPL-2.0** | 可以链接 | 修改的 MPL 文件需公开；保留声明 |
| LibreHardwareMonitorLib（NuGet） | MPL-2.0 | 可以 | 同上；自身依赖 PawnIO |
| OpenRGB | **GPL-2.0** | **不能链接/复制** | 可走独立进程 + SDK 协议；分发其二进制需遵守 GPL |
| PawnIO 驱动 | **GPL-2.0 + 例外** | 驱动可随产品分发；**模块必须 GPL 兼容** | 私有模块需商业授权 |
| PawnIO 官方模块 | 同仓库许可 | 可用 | 需作者签名版本 |
| FanControl | 闭源专有（禁止逆向） | 不能 | 仅参考交互设计 |
| SignalRGB 插件 | 无 license + ToS 禁逆向 | 不能 | 仅可作协议研究参考 |
| G-Helper | **GPL-3.0** | 不能闭源复用 | 仅作架构参考；ASUS WMI 用法可自行研究 |
| Artemis | **PolyForm Noncommercial** | 不能商用 | 非 OSI 开源 |
| WLED | EUPL-1.2 | 弱 copyleft | 通过网络协议集成 |
| HWiNFO SDK | 商业授权 | 需付费 | 非 Pro 共享内存仅 12h |

**法律风险提示（非法律意见）**：厂商 EULA 常限制逆向工程；直接驱动硬件存在合同风险；部分场景可能涉及 DMCA §1201 反规避；产品化前应请律师审阅 EULA、SDK 条款和隐私政策。

---

## 6. 工作量与成本估算（判断，非事实）

> 假设：1 名熟悉 C#/Windows 的开发者，有 AI 辅助编码；不计算学习硬件协议的前期时间；复用 LHM + OpenRGB + PawnIO，不自研驱动。

### 6.1 分阶段

| 阶段 | 范围 | 全职估算 | 业余估算 |
| --- | --- | --- | --- |
| **Phase 0 Spike** | 只读传感器 + 单路风扇控制并恢复 + 单条灯带控制 | 1-2 周 | 3-4 周 |
| **Phase 1 自用 MVP** | 服务 + UI + 传感器面板 + 风扇曲线 + 灯效桥接 + 场景 | 8-12 人周 | 4-6 个月 |
| **Phase 2 小圈子版本** | 2-3 个硬件家族适配、安装器、自动更新、崩溃上报、文档 | +3-5 人月 | +6-10 个月 |
| **Phase 3 产品化** | 50-100 款设备、插件体系、签名/合规、支持体系 | 3-5 人 × 12-24 个月 | 不建议单人 |

### 6.2 成本项

| 项目 | 估算 | 说明 |
| --- | --- | --- |
| EV 代码签名证书 | 约 $300-600 / 年 | 仅自研驱动需要 |
| Microsoft 驱动签名流程 | 时间成本为主 | attestation 免费但不能零售 WU；零售需 WHQL |
| PawnIO 商业授权 | 需联系作者 | 私有模块场景 |
| 测试硬件 | ¥1-5 万起 | 主板、显卡、内存、键鼠、灯带、AIO；设备越多越贵 |
| 代码签名（应用） | 开源可免费（SignPath） | 商业需证书 |
| 维护成本 | 持续 | 新设备、Windows 更新、驱动更新 |

### 6.3 为什么「设备覆盖」是主要成本

- 每款设备：抓包 → 识别初始化序列 → LED 映射 → 写入测试 → 固件差异 → 回归。顺利 3-5 天，不顺利 2-4 周。
- 100 款设备 ≈ 1-2 人年；SignalRGB 的「数千设备」是多年 + 社区插件 + 厂商合作的结果。
- 因此：**首版只覆盖「你自己的设备 + 1-2 个主流品牌」是唯一理性的范围。**

---

## 7. 风险清单（Top 10）

| # | 风险 | 概率 | 影响 | 缓解 |
| --- | --- | --- | --- | --- |
| 1 | 驱动/合规：WinRing0 已死、PawnIO 模块受 GPL/签名限制、自研驱动需 EV+WHQL | 高 | 高 | 首版只用 PawnIO 官方模块；不自研驱动 |
| 2 | 设备覆盖长尾，用户设备不支持 → 口碑差 | 高 | 高 | 只承诺白名单；接入 OpenRGB SDK 扩大覆盖 |
| 3 | 与厂商软件/Windows DL 抢设备导致冲突 | 高 | 中高 | 单写者策略、检测冲突进程、降级提示 |
| 4 | 写 EC/SuperIO 导致风扇失控/设备异常 | 中 | 高 | 只读默认、白名单、watchdog、恢复默认、日志 |
| 5 | 产品责任/保修/法律（EULA、逆向、DMCA） | 中 | 中高 | 免责声明、避开逆向高风险设备、律师审阅 |
| 6 | 杀软/反作弊误报（带驱动安装包） | 中 | 中高 | 不捆绑驱动；用 PawnIO；签名 |
| 7 | 上游依赖（PawnIO 单一作者、LHM 维护、OpenRGB GPL） | 中 | 中 | 锁定版本、准备降级路径、参与社区 |
| 8 | Windows 更新/驱动更新破坏 | 中 | 中 | 版本兼容测试、自动回滚、发布前验证 |
| 9 | 支持成本与用户期望失控 | 高 | 中 | 明确支持范围、内置诊断包导出 |
| 10 | 个人项目时间/兴趣衰减 | 高 | 高 | 先做 Phase 0/1，小步交付，别一上来做全品类 |

---

## 8. 推荐路线（Go / No-Go）

### 8.1 方案对比

| 方案 | 内容 | 成本 | 建议 |
| --- | --- | --- | --- |
| **A. 编排层 MVP（推荐）** | 复用 LHM + OpenRGB + PawnIO + Windows DL，自己做统一 UI/场景/稳定性 | 低 | 先做这个 |
| **B. 垂直聚焦** | 只做 ASUS/MSI 某代主板 + NVIDIA GPU + 少数外设，深度适配 | 中 | 若要做产品，选这个 |
| **C. 全品类对标 SignalRGB** | 自研协议 + 全设备覆盖 | 极高 | 不建议 solo |

### 8.2 Phase 0（1-2 周）验收标准

- [ ] 读到本机 CPU/GPU/主板温度、风扇转速、电压
- [ ] 手动控制一路机箱风扇，并能恢复到 BIOS 默认
- [ ] 控制一条 ARGB 灯带（OpenRGB 或 Windows DL）
- [ ] 杀掉服务进程后，风扇回到安全状态
- [ ] 确认本机是否需要 PawnIO、需要哪些模块

> 如果这一圈跑不通，先别写代码，先解决环境/权限/驱动问题。

### 8.3 Phase 1（自用 MVP，4-6 个月业余）

1. Windows 服务 + UI 骨架 + IPC + 日志
2. 传感器面板 + 历史曲线
3. 风扇曲线引擎 + 校准 + 安全兜底
4. 灯效：OpenRGB SDK 客户端 + 布局/效果
5. 场景/配置/开机自启
6. 稳定性测试（休眠、热插拔、崩溃恢复）

### 8.4 Phase 2（可选产品化）

- 设备白名单扩展 + 插件体系
- 安装器/自动更新/崩溃上报
- 代码签名与许可证合规
- 文档/社区/支持流程

### 8.5 Go / No-Go 判定

- **Phase 0 通过** → 继续 Phase 1。
- **Phase 1 完成后自用稳定 1 个月** → 考虑开源或小范围发布。
- **有 100+ 真实用户且设备需求可控** → 再考虑产品化；否则保持自用/小圈子。
- **如果 3 个月内没有开始写 Phase 0** → 大概率不会做了，及时止损。

### 8.6 首版明确不做的事（控制范围）

- 不做自研内核驱动（用 PawnIO）；
- 不做笔记本 EC 控制；
- 不做全品牌灯效协议（只做白名单 + OpenRGB 覆盖）；
- 不做在线账号/订阅/内容市场；
- 不做超频、电压、风扇曲线以外的高级调校（那是 Afterburner / 厂商工具的领域）；
- 不做多平台（Windows 优先）。

---

## 9. 如果只是想让自己的机器好用

在写代码之前，先确认现有工具是否已经够用：

1. **ASUS 笔记本**：G-Helper（GPL-3.0，单 exe，350K+ 用户）基本覆盖 Armoury Crate 的核心功能。
2. **风扇曲线**：FanControl（免费，闭源，曲线能力强）。
3. **灯效**：OpenRGB（GPL-2.0，2,500+ 设备）+ Windows Dynamic Lighting。
4. **监控**：LibreHardwareMonitor / HWiNFO。

如果这些组合已经满足 90%，**自研的价值就只剩「统一入口 + 更稳 + 中文 + 无订阅」**——那更应该做的是「编排层」，而不是重写底层。

---

## 10. 需要你确认的 5 个问题

1. **目标硬件清单**：你的主板型号、显卡、内存、键鼠、灯带/风扇控制器分别是什么？（决定可行性）
2. **目标性质**：自用 / 开源小工具 / 商业产品？（决定投入量级）
3. **可接受的依赖**：是否接受安装 PawnIO、管理员权限、以及把 OpenRGB 作为灯效后端？
4. **技术栈偏好**：C# / WPF 是否 OK？是否愿意写一点 C/C++/Pawn？
5. **时间预算**：每周能投入多少小时？能否接受 3-6 个月才出可用版本？

---

## 附录 A：主要来源

**SignalRGB**
- ToS（闭源、禁逆向、订阅）：https://signalrgb.com/terms/
- 插件机制：https://docs.signalrgb.com/developer/plugins/
- 插件加载与覆盖：https://docs.signalrgb.com/developer/plugins/how-is-a-plugin-loaded-/
- Pro 权益（含监控/风扇）：https://docs.signalrgb.com/guides/account-billing/about-pro-features/
- Cooling（风扇/泵控制）：https://docs.signalrgb.com/guides/advanced-features/fan-control/
- 冲突程序清单：https://docs.signalrgb.com/troubleshooting/software-conflicts/conflicting-programs/
- 内部驱动被阻止：https://docs.signalrgb.com/troubleshooting/software-conflicts/blocked-drivers/
- 系统需求（联网/管理员）：https://docs.signalrgb.com/quick-start/about-system-requirements/
- Dynamic Lighting 优先级：https://docs.signalrgb.com/troubleshooting/software-conflicts/dynamic-lighting/
- 插件仓库：https://gitlab.com/signalrgb/signal-plugins

**OpenRGB**
- 仓库/许可证（GPL-2.0）：https://gitlab.com/CalcProgrammer1/OpenRGB ｜ https://github.com/CalcProgrammer1/OpenRGB
- README（变砖警告）：https://gitlab.com/CalcProgrammer1/OpenRGB/-/raw/master/README.md
- SMBus/PawnIO：https://github.com/CalcProgrammer1/OpenRGB/blob/master/Documentation/SMBusAccess.md
- 设备列表：https://openrgb.org/devices_1.1.html
- 1.1 Release：https://codeberg.org/OpenRGB/OpenRGB/releases/tag/release_1.1

**LibreHardwareMonitor / FanControl**
- 仓库：https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
- 许可证（MPL-2.0）：https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LICENSE
- WinRing0→PawnIO PR：https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/pull/1857
- NuGet：https://www.nuget.org/packages/LibreHardwareMonitorLib
- FanControl Releases（闭源/PawnIO 迁移）：https://github.com/Rem0o/FanControl.Releases

**驱动与安全**
- Microsoft vulnerable driver blocklist：https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/design/microsoft-recommended-driver-block-rules
- Defender 对 WinRing0 的检出：https://support.microsoft.com/en-us/windows/security/threat-malware-protection/microsoft-defender-antivirus-alert-vulnerabledriver-winnt-winring0
- CVE-2020-14979：https://www.cve.org/CVERecord?id=CVE-2020-14979
- PawnIO：https://pawnio.eu/ ｜ 许可：https://github.com/namazso/PawnIO ｜ 模块：https://github.com/namazso/PawnIO.Modules
- 驱动签名选项：https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/driver-signing-offerings
- Test signing：https://learn.microsoft.com/en-us/windows-hardware/drivers/install/introduction-to-test-signing
- 内核模式签名策略：https://learn.microsoft.com/en-us/windows-hardware/drivers/install/kernel-mode-code-signing-policy--windows-vista-and-later-

**硬件 API / 标准**
- Windows Dynamic Lighting：https://learn.microsoft.com/en-us/windows-hardware/design/component-guidelines/dynamic-lighting-devices
- NVAPI cooler：https://docs.nvidia.com/nvapi/group__gpucooler.html
- AMD ADLX manual fan tuning：https://gpuopen.com/manuals/adlx/adlx-sdk-references/adlx-interfaces/gpu-tuning/iadlxmanualfantuning/
- Intel IGCL：https://github.com/intel/drivers.gpu.control-library
- Corsair iCUE SDK：https://github.com/CorsairOfficial/cue-sdk
- Razer Chroma SDK：https://doc.wyvrn.com/docs/chroma-sdk/
- ASUS Aura SDK：https://www.asus.com/campaign/aura/us/SDK.php
- MSI Mystic Light SDK：https://storage-asset.msi.com/files/pdf/Mystic_Light_Software_Development_Kit.pdf

**其他**
- G-Helper：https://github.com/seerge/g-helper ｜ https://g-helper.com/
- Armoury Crate 官方卸载说明：https://rog.asus.com/us/articles/guides/how-to-cleanly-uninstall-and-reinstall-armoury-crate-2/
- ASUS 服务列表（G-Helper 源码）：https://github.com/seerge/g-helper/blob/main/app/Helpers/AsusService.cs
- HWiNFO 许可证：https://www.hwinfo.com/licenses/
- Artemis（PolyForm Noncommercial）：https://github.com/Artemis-RGB/Artemis
- WLED（EUPL-1.2）：https://github.com/wled/WLED
- HWiNFO 读 EC 致风扇停转案例：https://hwinfo.com/forum/threads/fans-stop-spinning-after-running-hwinfo.1669/

## 附录 B：调研笔记与事实核查

- `research/rgb-landscape.md`：SignalRGB / Armoury Crate / OpenRGB 生态调研（56 个来源，标注已核实/推测/无法核实）
- `research/windows-hardware-access.md`：Windows 驱动签名、WinRing0 封禁、PawnIO、厂商 SDK 调研（含 blocklist 实际解包核验）

> 无法核实项（已在调研笔记中标注）：SignalRGB Pro 具体价格、SignalRGB Internal Driver 驱动名/签名方、Armoury Crate 是否 .NET/WPF、OpenRGB 精确贡献者人数、PawnIO 商业授权价格、EC 误写变砖的一手案例。

## 附录 C：一次真实的假冒仓库取证（2026-10，供避坑参考）

> 背景：用户提供了 `https://github.com/SignalRGB-Setup/SignalRGB-Pro`，认为这是 SignalRGB 的开源项目。核实结论：**不是**。该仓库是典型的「冒名 + 拼接开源代码 + SEO 引流 + 站外下载」仓库。

**仓库层面的红旗**

| 观察项 | 事实 | 正常项目应是什么样 |
| --- | --- | --- |
| 账号年龄 | 组织 `SignalRGB-Setup` 创建于 2026-10-16，与仓库同一天；仅 1 个仓库、0 关注者 | 官方组织应有历史、多个项目、关注者 |
| 仓库活跃度 | 1 star、1 fork、3 个 commit（全部网页上传，3 分钟内完成） | 有持续开发历史、issue/PR |
| 提交者 | 个人账号 `somilase1500` 创建于 2026-10-10、0 仓库 | 官方维护者账号 |
| Release | 无 release（`has_downloads=false`，releases 为空） | 开源软件通常有可校验的 release |
| 下载入口 | README 的「GET SignalRGB」按钮指向 `https://karlpolczynski635.github.io/.github/SignalRGB`（随机用户 GitHub Pages，访问返回 404），**不是 signalrgb.com** | 官方下载应指向官方域名 |
| README 文风 | 大量 SEO 关键词堆砌（"signalrgb devices"、"signalrgb supported devices"、Related Search Terms），内容自相矛盾 | 正常技术文档 |

**代码层面的证据（最致命）**

- `startup.cpp`：文件头写着 **"Startup for the OpenRGB application / This file is part of the OpenRGB project / SPDX-License-Identifier: GPL-2.0-or-later"**，内容含 `OpenRGBDialog`、`ResourceManager`、`NetworkServer` —— 是 OpenRGB 源码。
- `fan/FanBusController.cpp`：版权头为 **"Adam Honse (calcprogrammer1@gmail.com) 12 Jan 2021 / This file is part of the OpenRGB project"** —— OpenRGB 作者本人的代码。
- `device/DeviceProvider.cs`：命名空间 **`Artemis.Core.DeviceProviders`**、引用 `RGB.NET.Core` —— 是 **Artemis** 项目代码；而 Artemis 的许可证是 **PolyForm Noncommercial 1.0.0（禁止商用）**，与该仓库自称的 GPL-2.0 直接冲突。
- 目录里还混有 `Directory.Build.props`、`package.json`、`graphql.config.yml`、`azure-pipelines-pr.yml` 等互不相干的文件，以及 `fan/logo.jpg`、`fan/page1.png` 等占位图 —— 典型的拼接/生成式填充，不是可编译的完整工程。

**结论**：该仓库里**没有任何 SignalRGB 专有代码**（因为 SignalRGB 闭源，公开也不可能有），它是把 OpenRGB（GPL-2.0）和 Artemis（PolyForm Noncommercial）代码重新贴牌成 "SignalRGB Pro"，再用 SEO 页面把流量导向站外下载。这类仓库的常见用途是分发捆绑恶意软件/破解器的安装包，下载链接会轮换域名。

**给用户的建议**：

1. **不要**从该 README 的链接下载/运行任何东西；如果已经下载，不要运行，先交给 Defender 扫描。
2. 需要 SignalRGB 请只用官网 `signalrgb.com`；需要开源灯效代码请用 `codeberg.org/OpenRGB/OpenRGB`（GPL-2.0）或 `gitlab.com/signalrgb/signal-plugins`（插件）。
3. 可向 GitHub「Report repository」举报（Impersonation / Malware），并可通知 SignalRGB 官方。
4. **不要把这个仓库当作技术参考或代码来源**：它本身违反许可证（混入 Artemis 非商用代码却声称 GPL-2.0），且来源不可信。
