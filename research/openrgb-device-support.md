# OpenRGB 1.1 对本机灯效设备的支持核查

> 核查日期：2026-11-08（Asia/Shanghai）  
> 目标机：ASUS ROG STRIX Z790-H GAMING WIFI（BIOS 3001）+ AURA LED Controller `0B05:19AF` + ROG RYUO III `0B05:1A51` + ROG Falchion Ace HFX `0B05:1B7E` + ASUS RTX 4090（`1043:889A`）+ G.Skill `F5-6000J3636F16G` ×4  
> OpenRGB 版本：**1.1**，commit `8e16de27858cf2be6af54a33ce003df74cb9fd3e`，2026-10-16（本机日志直接确认）
>
> 标记约定：**【已核实】**＝直接读到官方页面/1.1 源码/本机 OpenRGB 1.1 日志或配置；**【推测】**＝基于已核实事实的推断，未在本机做写入验证；**【无法确认】**＝缺少可复现证据，需实测或厂商资料。

## 0. 一句话结论

- **主板 19AF：仅部分支持。** 1.1 能检测并建区，但本机 AURA 固件正是 `AULA3-AR32-0309`——公开 issue 中 1.1 对该固件“能识别、发命令无效”的版本；ARGB header 默认还是 0 灯，需要手动设数量。**【已核实（检测/固件版本）】【推测（本机写入效果）】**
- **RYUO III 1A51：不支持。** 1.1 只注册 Ryuo `1887` / Ryujin `18AE`；本机日志只有 HID 连接，没有 controller。OpenRGB 只做灯，不做泵/风扇。**【已核实】**
- **Falchion Ace HFX 1B7E：支持，但不是 ASUS 专用协议，而是通用 HID LampArray。** 本机 1.1 已注册为 `HID LampArray Device`，84 个 LED，Direct/Autonomous 两种模式。**【已核实】**
- **G.Skill `F5-6000J3636F16G`：不支持。** 1.1 没有 G.Skill DDR5 控制器；ENE DRAM 代码只列了 DDR4 的 `DIMM_LED-0102`（Trident Z RGB）等，未知版本会被丢弃。本机日志也没有 DRAM。**【已核实（源码/列表）】【无法确认（PawnIO 就绪后未复测）】**
- **ASUS RTX 4090 `1043:889A`：支持。** 精确对应 `ASUS TUF GeForce RTX 4090 Gaming OC`，走 GPU 的 NvAPI I2C、ENE SMBus 地址 `0x67`；本机 1.1 已注册，4 个 LED。**不需要 PawnIO。**【已核实】

---

## 1. 官方 1.1 支持列表核对

### 1.1 核查口径

`https://openrgb.org/devices_1.1.html` 的表格由 `data/supported_devices_1.1.csv` 驱动，按**控制器族**列出，不是按每块主板/每根内存的型号逐条列；1.1 CSV 共 312 行（273 个唯一 `RGBController|Name` 组合），VID/PID 列全为空。因此：

- “官方列表里有没有这个**控制器族**”看 1.1 CSV；
- “这个**具体 VID/PID/型号**是否真的被认”必须看 1.1 源码的 detector 表和本机日志。

1.1 源码 tag：`release_1.1`；本机实际运行的是同一 commit 的 1.1。

### 1.2 逐设备核对表

| 设备 | 关键 ID | 1.1 官方列表对应项 | 1.1 源码/本机核对 | 结论 |
|---|---|---|---|---|
| ROG STRIX Z790-H + AURA LED Controller | `0B05:19AF` | `Asus Aura USB Mainboard`（注释：适用于 x570 起的多数 AMD/Intel 主板） | 源码 `AURA_MOTHERBOARD_3_PID 0x19AF`；本机日志注册 `ASUS ROG STRIX Z790-H GAMING WIFI` | **仅部分支持**（检测/建区已核实，控制受固件限制） |
| ROG RYUO III | `0B05:1A51` | `Asus Aura Ryuo AIO` | 源码只有 `AURA_RYUO_AIO_PID 0x1887`、`AURA_RYUJIN_AIO_PID 0x18AE`；无 `1A51`；本机日志无注册 | **不支持** |
| ROG Falchion Ace HFX | `0B05:1B7E` | `HID LampArray Controllers`（通用项） | 无 ASUS 专用 `1B7E` 驱动；本机日志通过 `MI_04` 注册 `ROG FALCHION ACE HFX`，profile 84 LED | **支持（通用 LampArray）** |
| G.Skill `F5-6000J3636F16G` | 内存条型号（无 USB PID） | 无 G.Skill 条目；只有 `ENE SMBus Device` 等通用项 | `ENESMBusController.cpp` 只列 DDR4 `DIMM_LED-0102` 等；未知版本 `led_count=0`；本机日志无 DRAM | **不支持**（高置信；未在 PawnIO 就绪后复测） |
| ASUS RTX 4090 | PCI `10DE:2684` / `1043:889A` | `ENE SMBus Device` | `pci_ids.h`：`ASUS_TUF_RTX_4090_O24G_OC_GAMING 0x889A`；`ENESMBusControllerDetect.cpp` 地址 `0x67`；本机日志注册 `ASUS TUF GeForce RTX 4090 Gaming OC` | **支持** |

**统计：支持 2（Falchion Ace HFX、RTX 4090）；仅部分支持 1（Z790-H 19AF）；不支持 2（RYUO III、G.Skill DDR5）。**

---

## 2. 主板：ASUS AURA LED Controller `0B05:19AF`（Z790-H）

### 2.1 结论

**仅部分支持。** 1.1 对本机的 19AF 能做到“HID 枚举 + 读配置表 + 建 controller/zone”，但**不能保证灯效写入生效**：本机固件 `AULA3-AR32-0309` 正是公开 issue 里 1.1 失效的版本；此外 3 个 Addressable RGB Header 默认 0 灯，必须手动设置数量。**【已核实（检测/固件/建区）】【推测（写入效果，需实测）】**

### 2.2 控制器与传输方式

- **USB HID，不是 SMBus，也不是 WinUSB。** OpenRGB 通过 `hidapi` 打开 `HID\VID_0B05&PID_19AF&MI_02`，不是设备管理器里 `MI_00` 的 “AURA LED Controller”。本机日志/配置的 location 就是 `HID#VID_0B05&PID_19AF&MI_02`。**【已核实（本机配置）】**
- 控制器类：`AuraMainboardController` / `RGBController_AuraMainboard`；检测入口：
  - `REGISTER_HID_DETECTOR_PU("ASUS Aura Motherboard", ..., 0x19AF, 0xFF72, 0x00A1)`（`AsusAuraUSBControllerDetect.cpp:452`）
  - `PU` = 按 HID **usage page + usage** 匹配；0x19AF 匹配 `page 0xFF72 / usage 0x00A1` 的厂商自定义集合（即 `MI_02`），不是普通 “VID/PID 任意接口” detector。
- 协议要点（源码）：
  - `0xEC 0x82`：读固件版本；
  - `0xEC 0xB0`：读 60 字节 config table；
  - `0xEC 0x35`：effect/mode；
  - `0xEC 0x40`：direct 每 LED 颜色；
  - `0xEC 0x3F`：commit/save；
  - 65 字节 HID report（64 字节 payload + report id）。**【已核实（源码）】**

### 2.3 本机 1.1 实际枚举结果

来自 `C:\Program Files\OpenRGB\service_config\Configuration.json` 和 `profiles\Default.json`（2026-11-04 的 OpenRGB v1.1 运行）：

```text
ASUS Aura USB Mainboard Device
version: AULA3-AR32-0309
location: HID#VID_0B05&PID_19AF&MI_02

zones:
  Aura Mainboard                 4 LEDs  (LED 1 + RGB Header 1/2/3)
  Addressable RGB Header 1       0 LEDs  (min 0, max 500, manually configurable)
  Addressable RGB Header 2       0 LEDs  (min 0, max 500, manually configurable)
  Addressable RGB Header 3       0 LEDs  (min 0, max 500, manually configurable)
```

这解释了“只能控制部分 header”的直观现象：

1. **Addressable header 默认 0 灯。** 源码 `RGBController_AsusAuraUSB.cpp:236,255` 把 addressable zone 的 `leds_max` 设为 `AURA_ADDRESSABLE_MAX_LEDS`（500），首次运行 `leds_count=0`，并加 `ZONE_FLAG_MANUALLY_CONFIGURABLE_SIZE`。必须手动把每个 header 设成实际灯数（例如 3 把风扇 × 8 灯 = 24），否则不会写入。**【已核实（源码/本机配置）】**
2. **固定区还把 1 个板载 LED + 3 个名为 “RGB Header” 的 LED 放在一起。** `AuraMainboardController.cpp:17-40` 用 `config_table[0x1B]`（总 LED）、`[0x1D]`（RGB header 数）、`[0x02]`（addressable header 数）建区；固定区不是每个 12V/ARGB header 独立可控。**【已核实（源码/本机配置）】**
3. **本机是 3×Addressable Gen2 + 1×Aura RGB header**（ASUS 官方规格）；按配置，3 个 addressable zone 应对应 3 个 ARGB header，4-pin RGB header 走固定区。物理对应关系仍建议逐 header 实测确认。**【已核实（ASUS 规格/本机配置）】【推测（物理对应）】**

### 2.4 已知问题（按对本机的相关度排序）

#### A. 固件 `AULA3-AR32-0309` 的 1.1 失效（最高风险）

本机 19AF 的固件版本是 `AULA3-AR32-0309`。这与两个公开 issue 的固件完全一致：

- [#6038](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/6038)：TUF GAMING B650E-E WIFI，`0b05:19af` / `AULA3-AR32-0309`；OpenRGB 1.1 能检测、能读 config table，但 ARGB header 完全不响应；**同一台机器换 1.0 就正常**。
- [#5910](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/5910)：ROG STRIX X870-A GAMING WIFI，同固件；Direct/Static 等全部无效；USBPcap 抓包显示 Gen2 协议需要：
  - 先发 `EC 38 01 01 00 00` / `EC 38 00 01 00 00` 使能；
  - ARGB header 用 effect channel `0x10`、`0x11`、`0x12`（不是 1/2/3）；
  - direct 包的 LED count 字节要带 `0x20` 标志（如 `0x34` = `0x20 | 20`）。

1.1 的 `AuraMainboardController`/`AuraUSBController` 仍是 Gen1 写法（effect channel 从 1 开始、fixed 占 0；direct channel 0/1/2；无 0x38 使能、无 0x20 标志），所以**本机很可能出现“能识别但写不亮”**。这不是配置问题，是协议代际问题。**【已核实（issue 报告 + 固件版本）】【推测（本机实际写入）】**

> 注意：issue #6038 说 1.1 坏了、1.0 正常；#5910 说 1.0 也不亮。两者板型/BIOS 不同，结论不完全一致。对本机最稳妥的判断是：**1.1 检测没问题，控制需要实测；若 1.1 不亮，先用 1.0 做对照，再考虑 Gen2 协议补丁或厂商软件。**

#### B. Z790-H 的 BIOS 更新已知问题

- [#4224](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/4224)（closed）：Z790-H 升级 BIOS 2301（含 Aura 固件更新）后，主板 RGB 区不可控；内存仍可控。issue 已关闭（最后更新 2024-07-12），但 1.0 → 1.1 的 `AsusAuraMainboardController.cpp` / `RGBController_AsusAuraMainboard.cpp` 逐行 diff **完全相同**，没有看到针对该问题的代码修复。本机 BIOS 3001 比 2301 更新，风险不能被“issue 已关闭”排除。**【已核实（issue + 源码 diff）】【推测（3001 仍可能受影响）】**

#### C. Addressable header 数量/灯数必须手动配置

- 3 个 header 默认 0 灯，不设数量就不写；设错数量会导致颜色映射错位或部分灯不亮。**【已核实（源码/本机配置）】**
- 每个 header 上限 500 灯；本机 3 个 header 的 `flags=4158`（`ZONE_FLAG_MANUALLY_CONFIGURABLE_*`），表示名称/大小/类型/矩阵/分段都可手动改。**【已核实（本机配置）】**

#### D. Armoury Crate / Aura 服务冲突

- 本机**没有安装完整的 Armoury Crate**（仅有 ASUS Update Helper / AsusFCNotification，以及一个停止的 ArmouryCrateDownloadTool 残留），OpenRGB 1.1 仍能检测 19AF，说明**检测不依赖 Armoury Crate**。**【已核实（本机已安装程序 + 日志）】**
- 但 [#4114](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/4114)（Z790 Hero）报告：不装 Armoury Crate 时，OpenRGB 不能控制 VRM 的 LED 显示屏；装了之后可以。说明某些灯区需要 Armoury Crate 先做固件/初始化。Z790-H 是否有同类灯区，未验证。**【已核实（issue 报告）】【推测（Z790-H 可能类似）】**
- 同时运行 Armoury Crate/Aura 与 OpenRGB 会争抢同一个 HID 设备；建议单写者：要么 OpenRGB，要么 Armoury Crate。**【推测（通用 HID 独占/初始化冲突）】**

#### E. 1.1 回归与重开机问题

- [#6038](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/6038) 是明确的 1.1 vs 1.0 回归报告（同固件）。
- [#4793](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/4793)：ASUS Aura 集成控制器在一次重启后不可控；属于同一类“固件状态/初始化”问题。**【已核实（issue 存在）】**

### 2.5 建议

1. 以管理员/服务方式跑 OpenRGB 1.1，先把 3 个 Addressable RGB Header 的 LED 数量设成实际值，再对每个 zone 单独试 Direct。
2. 若不亮：用 OpenRGB 1.0 做对照（#6038 的路径）；若 1.0 亮、1.1 不亮，优先等 1.1.x/上游修复，或暂时用 1.0。
3. 若 1.0/1.1 都不亮：用 Armoury Crate 或 Windows Dynamic Lighting（Z790 在 ASUS 官方 Dynamic Lighting 支持列表内）作为主板灯效兜底。
4. 不要同时开 Armoury Crate 和 OpenRGB。
5. 若自研：需要补 Gen2 协议（`EC 38` 使能、effect channel `0x10-0x12`、direct count `0x20`），工作量高，且要按主板/固件版本做兼容表。

---

## 3. ROG RYUO III `0B05:1A51`

### 3.1 结论

**不支持。** 1.1 的 ASUS 水冷 detector 只注册：

- `AURA_RYUO_AIO_PID 0x1887`（ROG Ryuo）
- `AURA_RYUJIN_AIO_PID 0x18AE`（ROG Ryujin）

没有 `0x1A51`。本机 1.1 日志只出现 `HID device connected: [0b05:1a51 - ...MI_01]`，**没有** “Registering RGB controller ROG RYUO III”；`Default.json` 也没有它。**【已核实（源码/本机日志）】**

### 3.2 它暴露的是灯效还是泵/风扇？

- OpenRGB 是**灯效控制器**，不是风扇/泵控制器。即使是被支持的 Ryuo `0x1887`，`AsusAuraRyuoAIOController` 也只发 LED 包（`SetLedsDirect`、`SetMode`、`GetStatus` 读当前颜色），没有泵/风扇转速或 PWM 接口。**【已核实（源码）】**
- RYUO III 的泵/风扇控制需要 Armoury Crate（或其它支持该型号的厂商/第三方工具；liquidctl 是否支持需另查，本文未核实）。OpenRGB 即使以后加 1A51，按现有架构也只做灯。**【已核实（OpenRGB 架构）】【无法确认（liquidctl 支持情况）】**

### 3.3 可选路径

- **保留 Armoury Crate**：泵/风扇/灯效都由厂商软件管，最稳。
- **把冷头 ARGB 线接到主板 ARGB header**：由 19AF 控制；但本机 19AF 本身有固件风险，且 header 默认 0 灯，需要配置。只解决灯，不解决泵/风扇。
- **自研 HID**：需要抓 1A51 的灯效协议，工作量大；issue [#3851](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/3851) 从 2024-01-10 开到现在仍未支持，说明不是简单加 PID。

---

## 4. ROG Falchion Ace HFX `0B05:1B7E`

### 4.1 结论

**支持，但走的是通用 HID LampArray，不是 ASUS 专用驱动。** 1.1 源码里没有 `1B7E` 的 ASUS 专用 detector；Falchion 的专用 PID 只有 `193C`（Wired）和 `193E`（Wireless）。但 1.1 的 `HIDLampArrayController` 会按 HID Usage Page `0x59` / Usage `0x01` 枚举任何 LampArray 设备，本机键盘正好暴露了 `MI_04` 的 LampArray 接口。**【已核实（源码/本机日志/profile）】**

本机 1.1 日志：

```text
HID device connected: [0b05:1b7e - ...HID#VID_0B05&PID_1B7E&MI_04...]
Registering RGB controller ROG FALCHION ACE HFX
```

本机 profile：

```text
HID LampArray Device
vendor: ASUSTeK
location: HID#VID_0B05&PID_1B7E&MI_04
zone: Keyboard, 84 LEDs
modes: Direct, Autonomous
```

### 4.2 限制

- **只有 Direct / Autonomous 两种模式。** 不能用 ASUS Aura 的 Static/Breathing/Rainbow 等固件效果；Direct 是逐键写颜色，Autonomous 交回键盘固件。**【已核实（profile/源码）】**
- **84 个 LED 是 LampArray 描述符给的映射**，包含按键和额外灯区；和 ASUS 官方布局不一定 1:1，矩阵映射需要实测。**【已核实（profile 84 LED）】【推测（映射差异）】**
- **依赖 LampArray 接口存在。** 本机有线 HFX 的 `MI_04` 暴露了它；无线/不同固件/不同模式是否还暴露，未验证。**【已核实（本机有线）】【无法确认（无线/其它固件）】**
- **与 Windows Dynamic Lighting / Armoury Crate 争控制权。** ASUS 官方 FAQ 明确 Falchion Ace HFX 支持 Windows Dynamic Lighting；如果 Dynamic Lighting 或 Armoury Crate 优先级更高，OpenRGB 可能写不进去。建议只保留一个写者，或在 Windows 设置里调整“背景光控制”优先级。**【已核实（ASUS FAQ）】【推测（争抢）】**
- **没有 ASUS 专用 PID 协议**，所以不能做灯效同步/板载效果编程；但作为“逐键灯效”已经够用。**【已核实（源码）】**

### 4.3 建议

- 首选 OpenRGB 1.1 的 `HID LampArray Device`，低工作量。
- 如果 OpenRGB 不亮：用 Windows Dynamic Lighting（ASUS 官方支持）或 Armoury Crate。
- 不需要 PawnIO。

---

## 5. G.Skill `F5-6000J3636F16G`（DDR5 RGB）

### 5.1 先确认具体系列

`F5-6000J3636F16G` 是**单条 16GB 模块料号**，不是完整 kit 料号。同一模块料号至少出现在两套 DDR5-6000 CL36 16GB RGB 产品上：

- **Trident Z5 RGB**：`F5-6000J3636F16GX2-TZ5RK`（2×16GB）
- **Ripjaws M5 Neo RGB**：`F5-6000J3636F16GX2-RM5NRK`（2×16GB）

所以“很可能是 Trident Z5 RGB”不能直接成立；请看内存条标签/包装的 **`-TZ5RK` 还是 `-RM5NRK`** 后缀。两者都是 DDR5-6000 CL36-36-36-96 1.35V、16GB、带 RGB。**【已核实（G.Skill 产品页）】**

### 5.2 1.1 支持结论

**不支持。** 依据：

1. 1.1 官方控制器列表（`supported_devices_1.1.csv`）没有 G.Skill 条目；只有 `ENE SMBus Device`、`Kingston Fury DDR4/5 DRAM`、`Corsair DRAM` 等通用/厂商项。**【已核实（官方 CSV）】**
2. `Controllers/ENESMBusController/ENESMBusController.cpp` 的已知设备版本表只列到 DDR4 世代：
   - `DIMM_LED-0102` → 注释明确写 “First generation DRAM controller (Trident Z RGB)”；
   - `DIMM_LED-0103` → T-Force Delta RGB；
   - 其余是 `AUDA0-*` / `AUMA0-*` 等主板/旧控制器。
   没有 G.Skill DDR5 的版本字符串。**【已核实（源码）】**
3. 未知版本字符串会走 `else`：`led_count = 0`，controller 不注册（源码 `ENESMBusController.cpp:311-318`）。**【已核实（源码）】**
4. 本机 1.1 日志（2026-11-04）在 “Detecting I2C DRAM modules” 之后没有任何 DRAM controller；`Default.json` 也没有内存。**【已核实（本机日志）】**

> 补充：如果个别 DDR5 模块的固件字符串恰好报告 `DIMM_LED-0102`，理论上可能被 ENE DRAM 代码“误识别”；但 1.1 没有针对 DDR5 的灯数/通道配置，不能据此认为官方支持。以 1.1 列表 + issue 跟踪为准。

> 注意：这次运行的时候 PawnIO 还没装（日志里 `Could not open PawnIO`），所以“本机没检测到”不能单独证明“永远不支持”。但源码/列表已经足够说明 1.1 没有 G.Skill DDR5 支持；2026-11-08 本机已安装 PawnIO 2.2.0.0，若要做最终确认，应在管理员/服务模式下重扫一次。

### 5.3 如果将来支持，通道/权限/风险

- **通道：SMBus（Intel Z790 是 i801）**，不是 USB。DDR5 SPD 在 `0x50/0x51`，ENE DRAM 的 RGB 控制器通过 `0x77` 重映射到 `0x70-0x77` 等地址。**【已核实（OpenRGB SMBusAccess/源码）】**
- **Windows 需要 PawnIO + 管理员**（或 OpenRGB 后台服务）。`Documentation/SMBusAccess.md` 明确写：先装 PawnIO；必须管理员运行，或安装为以管理员权限运行的后台服务。**【已核实（官方文档）】**
- **已知风险：**
  - SMBus 总线锁死/系统崩溃：[#5537](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/5537)（ASUS ROG Strix B850-A，启动 OpenRGB 时 SMBus lockup/system crash）。
  - ENE DRAM 重映射在休眠恢复后卡住：[#3948](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/3948)。
  - PawnIO 迁移后内存不再被检测：[#5106](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/5106)。
  - G.Skill DDR5 检测回归/不稳定：[#5655](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/5655)、[#5404](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/5404)、[#3831](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/3831)。
  - Linux 下 `spd5118` 驱动会占用 DDR5 SPD 地址（Kingston Fury 已知），需要 `rmmod spd5118`；Windows 下对应的是 SPD 地址冲突/厂商驱动占用。**【已核实（官方文档）】**
- **不要**用自研代码直接写 SPD 或未知 SMBus 地址；写错可能影响内存识别/稳定性。OpenRGB 本身也只写 RGB 控制器寄存器，不写 SPD EEPROM。**【推测/通用风险】**

### 5.4 建议

- 内存灯效先用 **G.Skill Trident Z Lighting Control**（或主板 Aura Sync / Armoury Crate）控制。
- 不要把 OpenRGB 当作这套 DDR5 的可用方案；如果一定要做，工作量/风险都是“高”。
- 如果将来 OpenRGB 上游加了 G.Skill DDR5，再评估 PawnIO + 管理员路径。

---

## 6. ASUS RTX 4090（`1043:889A`）

### 6.1 型号确认

`SUBSYS_889A1043` 里的 `889A` 在 OpenRGB `pci_ids/pci_ids.h:343` 精确对应：

```c
#define ASUS_TUF_RTX_4090_O24G_OC_GAMING  0x889A
```

即 **ASUS TUF GeForce RTX 4090 Gaming OC**。本机 1.1 日志也直接打出：

```text
Registering I2C interface: Nvidia NvAPI I2C on GPU 0 Device 10DE:2684 Subsystem: 1043:889A
Registering RGB controller ASUS TUF GeForce RTX 4090 Gaming OC
```

**【已核实（源码/本机日志）】**

### 6.2 支持结论

**支持。** `ENESMBusControllerDetect.cpp:403` 注册：

```c
REGISTER_I2C_PCI_DETECTOR("ASUS TUF GeForce RTX 4090 Gaming OC",
    DetectENESMBusGPUControllers, NVIDIA_VEN, NVIDIA_RTX4090_DEV,
    ASUS_SUB_VEN, ASUS_TUF_RTX_4090_O24G_OC_GAMING, 0x67);
```

- 控制器：`ENE SMBus Device`（不是 `Asus Aura GPU`，40 系走 ENE）。
- 地址：`0x67`。
- 总线：本机日志是 **Nvidia NvAPI I2C**，不是 i801 SMBus；因此**不需要 PawnIO**（日志里 PawnIO 打开失败，GPU 仍然注册成功）。
- 本机 profile：`ENE SMBus Device`，location `I2C: Nvidia NvAPI I2C on GPU 0, address 0x67`，4 个 LED，模式 Direct/Off/Static/Breathing/Flashing/Spectrum Cycle/Rainbow/Chase Fade/Chase/Random Flicker。**【已核实（本机 profile）】**

### 6.3 已知问题

- [#4702](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/4702)：ROG STRIX RTX 4090 Gaming White 颜色不对（Direct 变浅蓝/绿，Static 只有蓝色）。这是 **White 变体**，和本机 TUF `889A` 不同；但说明 40 系 ENE 控制器存在颜色映射/变体差异。**【已核实（issue 报告）】**
- [#5871](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/5871)：ASUS TUF RTX 4090 子系统 `1043:89C7` 的新设备请求；本机 `889A` 已在表内。**【已核实（issue 存在）】**
- [#5054](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/5054)：ROG Strix 4090 变体 SPID `8933` 的支持请求（closed）；说明 ASUS 4090 有多个 SPID，必须逐个子系统 ID 核对。**【已核实（issue 存在）】**

### 6.4 如果型号未知，怎么确认

1. PowerShell：`Get-PnpDeviceProperty -InstanceId <GPU InstanceId> -KeyName DEVPKEY_Device_HardwareIds`，或 `wmic path Win32_VideoController get name,PNPDeviceID`；看 `SUBSYS_xxxx`。
2. GPU-Z / HWiNFO 的 “Subsystem ID”。
3. 在 OpenRGB 源码 `pci_ids/pci_ids.h` 搜 `ASUS_TUF_RTX_4090_*` / `ASUS_ROG_STRIX_RTX_4090_*`。
4. 看 OpenRGB 日志的 `Registering RGB controller ...` 行；它会打出实际匹配的型号名。
5. 不要用 USB VID/PID 找 4090 灯效——它不是 USB 设备，是 PCI 设备 + GPU I2C。

---

## 7. 控制路径建议、开发工作量与风险

> “开发工作量”指接入/产品化工作量，不是用户安装难度：  
> **低**＝直接用 OpenRGB 原生/SDK 或系统 API；**中**＝需要 wrapper/自动化/兼容层；**高**＝需要逆向协议、内核访问或自研驱动。

| 设备 | 推荐控制路径 | 备选 | 开发工作量 | 风险 | 结论 |
|---|---|---|---|---|---|
| Z790-H 19AF 主板 | **OpenRGB 原生 USB HID**（1.1；先手动设 header 灯数） | OpenRGB 1.0 对照 / Windows Dynamic Lighting / Armoury Crate / 自研 HID（Gen2） | **低**（原生）/ **高**（自研 Gen2） | **高**：本机固件 `AULA3-AR32-0309` 是 1.1 已知失效版本；header 默认 0 灯；BIOS 3001 仍有 #4224 类风险 | 仅部分支持 |
| ROG RYUO III | **Armoury Crate**（泵/风扇/灯效） | 冷头 ARGB 接主板 header / 自研 HID | **低**（厂商）/ **高**（自研） | 中：灯效可用厂商软件；接主板 header 受 19AF 固件风险影响 | 不支持（OpenRGB 原生） |
| Falchion Ace HFX | **OpenRGB 原生 HID LampArray** | Windows Dynamic Lighting / Armoury Crate / 自研 HID（不必要） | **低**（原生）/ **高**（自研） | 低：只有 Direct/Autonomous，无 ASUS 固件效果；需避免与 Dynamic Lighting 争控制权 | 支持 |
| G.Skill `F5-6000J3636F16G` | **G.Skill Trident Z Lighting Control / 主板 Aura Sync** | 自研 SMBus（不推荐） | **低**（厂商）/ **高**（自研） | **高**：SMBus 锁死、SPD 地址冲突、PawnIO 权限、G.Skill DDR5 检测不稳定 | 不支持（OpenRGB 原生） |
| ASUS RTX 4090 `889A` | **OpenRGB 原生 ENE SMBus（NvAPI I2C 0x67）** | Armoury Crate / GPU Tweak III / 自研 SMBus | **低**（原生）/ **高**（自研） | 低：本机已注册/建区；注意 40 系变体颜色映射差异 | 支持 |

**补充：**

- 如果目标是“统一控制面板”，OpenRGB 原生路径覆盖 **GPU + 键盘 + 主板（部分）**；RYUO III 和 G.Skill DDR5 必须保留厂商软件或接主板 header。
- 如果目标是“闭源产品集成”，OpenRGB 是 GPL-2.0，不能直接链接；只能参考协议或用其网络 SDK 解耦。**【已核实（GPL-2.0）】**
- 自研 HID/SMBus 的最大成本不是“发一个包”，而是**按型号/固件维护兼容表**；本机 19AF 的 `AULA3-AR32-0309` 就是典型例子。

---

## 8. PawnIO 与管理员权限

### 8.1 官方要求

- OpenRGB 1.1 release notes：Windows 上访问 **RAM、旧主板、以及某些其它设备** 需要安装 [PawnIO](https://pawnio.eu/)，并且必须 **以管理员运行**，或安装为后台服务。**【已核实（官方 release notes）】**
- `Documentation/SMBusAccess.md`：Windows 用 PawnIO 访问 SMBus；**必须先安装 PawnIO**；**必须以管理员运行**，或安装为管理员权限的后台服务；WinRing0/InpOut32 已弃用。**【已核实（官方文档）】**
- `Documentation/USBAccess.md`：Windows 的 USB 设备**不需要特殊驱动/设置**；如果检测不到，才尝试管理员。**【已核实（官方文档）】**

### 8.2 本机实际状态

- OpenRGB 1.1 日志（2026-11-04）显示：`Start PawnIO: SmbusI801.bin` → `Could not open PawnIO`；但同一次运行仍然注册了：
  - `ASUS TUF GeForce RTX 4090 Gaming OC`（NvAPI I2C）
  - `ASUS ROG STRIX Z790-H GAMING WIFI`（USB HID 19AF）
  - `ROG FALCHION ACE HFX`（HID LampArray）
  
  说明这三个设备的检测**不依赖 PawnIO**。**【已核实（本机日志）】**
- 本机当前（2026-11-08 核查时）已安装 **PawnIO 2.2.0.0**，`PawnIO` 内核服务状态 **RUNNING**（`sc.exe query PawnIO`）。这是 2026-11-04 日志之后的状态；如果现在重跑 OpenRGB，SMBus 路径可能已可用。**【已核实（本机安装记录/服务状态）】**
- 但 **G.Skill DDR5 本身不在 1.1 支持列表**，所以“PawnIO 已装”不等于“内存能控制”。

### 8.3 逐设备结论

| 设备 | 是否需要 PawnIO | 是否需要管理员 | 说明 |
|---|---|---|---|
| 19AF 主板 | **不需要** | 通常不需要（USB HID） | OpenRGB 走 `MI_02` HID；本机 PawnIO 失败时仍注册 |
| RYUO III | 不需要（但也不支持） | — | USB HID；1.1 无 1A51 controller |
| Falchion Ace HFX | **不需要** | 通常不需要 | HID LampArray `MI_04` |
| ASUS RTX 4090 | **不需要** | 通常不需要（NvAPI I2C；未单独验证普通用户） | 本机 PawnIO 失败时仍注册；不是 i801 SMBus |
| G.Skill DDR5 | **需要**（如果将来支持） | **需要**（或后台服务） | 走 i801 SMBus；当前 1.1 不支持该内存 |

**结论：**

- 只为 **主板/键盘/显卡** 灯效，**不需要额外安装 PawnIO**；本机已装的 PawnIO 不会影响这三类 USB/NvAPI 设备。
- 若要尝试 **内存 RGB**，需要 PawnIO + 管理员/服务；本机已具备 PawnIO，但 1.1 仍不支持 G.Skill DDR5，重扫预计也不会出现。
- 日志里那条 “One or more I2C/SMBus interfaces failed to initialize” 的警告是**通用警告**；如果只用 USB/NvAPI 设备，可以忽略。

---

## 9. 建议的下一步实测（按优先级）

1. **主板 19AF**：以管理员运行 OpenRGB 1.1 → 把 3 个 `Addressable RGB Header` 的 LED 数量设成实际值 → 逐个 header 试 Direct（例如红/绿/蓝）→ 记录哪个 header 有反应。  
   - 若全无反应：换 OpenRGB 1.0 做对照（#6038 路径）。  
   - 若 1.0 有反应：1.1 回归，等上游/用 1.0。  
   - 若 1.0 也无反应：本机固件 `AULA3-AR32-0309` 的 Gen2 协议问题，转 Armoury Crate/Windows Dynamic Lighting。
2. **键盘**：在 OpenRGB 里确认 `HID LampArray Device`（84 LED）能逐键改色；若被 Dynamic Lighting 抢控制，关闭 “Use Dynamic Lighting on my devices” 或调整 Armoury Crate 优先级。
3. **显卡**：确认 `ENE SMBus Device` 的 4 个 LED 能改色；不需要 PawnIO。
4. **内存**：在 PawnIO 已装 + 管理员/服务模式下重扫；若仍无 G.Skill，按“不支持”处理，不要继续投入逆向。
5. **记录**：把 OpenRGB 版本、19AF 固件版本（`AULA3-AR32-0309`）、GPU 子系统 ID、键盘 HID 接口（`MI_04`）写入设备指纹，后续升级 BIOS/固件后重新核对。

---

## 10. 来源

### 10.1 OpenRGB 官方/源码

- 1.1 支持设备页：<https://openrgb.org/devices_1.1.html>
- 1.1 控制器级 CSV：<https://openrgb.org/data/supported_devices_1.1.csv>
- Pipeline/逐设备 CSV：<https://openrgb.org/data/supported_devices_pipeline.csv>
- 1.1 release（Codeberg）：<https://codeberg.org/OpenRGB/OpenRGB/releases/tag/release_1.1>
- 1.1 release（GitLab）：<https://gitlab.com/CalcProgrammer1/OpenRGB/-/releases/release_1.1>
- ASUS 19AF detector：<https://gitlab.com/CalcProgrammer1/OpenRGB/-/blob/release_1.1/Controllers/AsusAuraUSBController/AsusAuraUSBControllerDetect.cpp#L452>
- ASUS 主板 controller：<https://gitlab.com/CalcProgrammer1/OpenRGB/-/blob/release_1.1/Controllers/AsusAuraUSBController/AsusAuraUSBController/AsusAuraMainboardController.cpp#L17>
- ASUS addressable zone：<https://gitlab.com/CalcProgrammer1/OpenRGB/-/blob/release_1.1/Controllers/AsusAuraUSBController/AsusAuraUSBController/RGBController_AsusAuraUSB.cpp#L236>
- Ryuo/Ryujin detector：<https://gitlab.com/CalcProgrammer1/OpenRGB/-/blob/release_1.1/Controllers/AsusAuraUSBController/AsusAuraUSBControllerDetect.cpp#L559>
- ENE GPU detector（889A）：<https://gitlab.com/CalcProgrammer1/OpenRGB/-/blob/release_1.1/Controllers/ENESMBusController/ENESMBusControllerDetect.cpp#L403>
- ENE DRAM 支持表：<https://gitlab.com/CalcProgrammer1/OpenRGB/-/blob/release_1.1/Controllers/ENESMBusController/ENESMBusController.cpp#L107>
- PCI ID（889A）：<https://gitlab.com/CalcProgrammer1/OpenRGB/-/blob/release_1.1/pci_ids/pci_ids.h#L343>
- HID LampArray detector：<https://gitlab.com/CalcProgrammer1/OpenRGB/-/blob/release_1.1/Controllers/HIDLampArrayController/HIDLampArrayControllerDetect.cpp#L50>
- HID detector 宏定义（PU = page+usage）：<https://gitlab.com/CalcProgrammer1/OpenRGB/-/blob/release_1.1/DetectionManager.h#L526>
- SMBusAccess 文档：<https://gitlab.com/CalcProgrammer1/OpenRGB/-/blob/release_1.1/Documentation/SMBusAccess.md>
- USBAccess 文档：<https://gitlab.com/CalcProgrammer1/OpenRGB/-/blob/release_1.1/Documentation/USBAccess.md>

### 10.2 OpenRGB issue（用户报告/上游跟踪）

- [#4224](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/4224) Z790-H BIOS 更新后主板区不可控
- [#3929](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/3929) Z790-F ARGB Gen2 抓包/新设备
- [#6038](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/6038) 19AF `AULA3-AR32-0309` 在 1.1 失效、1.0 正常
- [#5910](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/5910) 19AF 同固件 Gen2 协议差异（0x38/0x10-0x12/0x20）
- [#5529](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/5529) 19AF 在 B650E-I 不检测
- [#4114](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/4114) Z790 Hero VRM LED 依赖 Armoury Crate
- [#4793](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/4793) ASUS Aura 控制器重启后不可控
- [#3851](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/3851) RYUO III 360 ARGB White 新设备请求（open）
- [#3582](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/3582) Falchion Ace 新设备请求（open）
- [#4203](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/4203) Falchion RX Low Profile 新设备请求（open）
- [#4702](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/4702) ROG Strix 4090 White 颜色异常
- [#5871](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/5871) ASUS TUF 4090 `1043:89C7` 变体
- [#5054](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/5054) ROG Strix 4090 SPID `8933`
- [#5655](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/5655) G.Skill Trident Z5 RGB DDR5 不被检测（Linux）
- [#5404](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/5404) G.Skill Trident Z5 在 1.0rc1/rc2 停止检测
- [#5636](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/5636) G.Skill Ripjaws M5 Neo RGB `F5-6000J3636F16GX2-RM5NRW` 新设备请求
- [#3831](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/3831) G-Skill DDR5-6400 灯效冻结/锁插件
- [#5537](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/5537) ASUS B850-A SMBus lockup/系统崩溃
- [#3948](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/3948) ENE DRAM 休眠恢复后卡住
- [#5106](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/5106) PawnIO 迁移后内存不再检测

### 10.3 ASUS / G.Skill / 第三方

- Z790-H 规格（3×Addressable Gen2 + 1×Aura RGB）：<https://rog.asus.com/motherboards/rog-strix/rog-strix-z790-h-gaming-wifi-model/spec/>
- Z790-H BIOS 页面（3001 发布日期 2025-05-19；最新 3301）：<https://www.asus.com/uk/supportonly/rog%20strix%20z790-h%20gaming%20wifi/helpdesk_bios/>
- ASUS Windows Dynamic Lighting 兼容列表（含 Z790、Falchion Ace HFX）：<https://rog.asus.com/dk/support/faq/1051416/>
- G.Skill Trident Z5 RGB `F5-6000J3636F16GX2-TZ5RK`：<https://www.gskill.com/tw/product/203/375/1640229752/F5-6000J3636F16GX2-TZ5RK>
- G.Skill Ripjaws M5 Neo RGB `F5-6000J3636F16GX2-RM5NRK`：<https://www.gskill.com/cn/product/204/430/1727073263/F5-6000J3636F16GX2-RM5NRK>
- liquidctl 的 ASUS Aura USB 说明（19AF/固件/通道同步限制，第三方参考）：<https://github.com/liquidctl/liquidctl/blob/292dc561361cfbc0cdee155c26662131fe160989/docs/asus-aura-led-guide.md>

### 10.4 本机证据

- OpenRGB v1.1 日志：`C:\Program Files\OpenRGB\service_config\logs\OpenRGB_20261104_081606.log`（2026-11-04，commit `8e16de27`）
- OpenRGB 设备配置：`C:\Program Files\OpenRGB\service_config\Configuration.json`（19AF `version: AULA3-AR32-0309`；3 个 addressable zone 0 LED）
- OpenRGB profile：`C:\Program Files\OpenRGB\service_config\profiles\Default.json`（3 个 controller：GPU ENE 0x67 / 19AF / HID LampArray 84 LED）
- PawnIO：`C:\Program Files\PawnIO`，版本 `2.2.0.0`；`sc.exe query PawnIO` → `RUNNING`（2026-11-08）

---

## 11. 未确认/待核实

- 本机 19AF 在 1.1 下**实际写入**是否完全无效：需要逐 header 实测；目前只有“同固件公开失败报告”和“检测/建区成功”两类证据。
- 本机 19AF 的 3 个 “RGB Header” 固定区 LED 与 3 个 Addressable zone 的物理对应关系：需要看主板走线/逐 header 测试。
- Z790-H BIOS 3001 是否一定带有与 2301 相同的 Aura 行为：没有 ASUS 的 Aura 固件变更日志。
- G.Skill 具体是 Trident Z5 RGB 还是 Ripjaws M5 Neo RGB：需要看标签后缀；两者当前都不在 1.1 支持列表。
- G.Skill 在 PawnIO 2.2.0.0 就绪后重扫是否会出现（源码判断不会；未实测）。
- Falchion Ace HFX 在无线/其它固件模式下是否仍暴露 `MI_04` LampArray：未验证。
- RYUO III 的灯效协议是否与 Ryuo `0x1887` 相同：未验证；issue #3851 仍 open。
