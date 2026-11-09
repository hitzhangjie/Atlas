# Atlas 个人版开发计划与当前状态

> 目标：**只服务这一台机器，自用，不发布、不收费、不做全品类兼容。**
> 最后更新：2026-11-08

## 1. 目标硬件（已实测确认）

| 部件 | 型号 | 控制/读取路径 |
| --- | --- | --- |
| 主板 | ASUS ROG STRIX Z790-H GAMING WIFI（BIOS 3001） | Nuvoton **NCT6798D** SuperIO → PawnIO |
| CPU | Intel Core i9-13900K | MSR（PawnIO IntelMSR 模块） |
| GPU | NVIDIA GeForce RTX 4090（ASUS，SUBSYS 889A1043） | NVAPI（用户态） |
| 内存 | G.Skill F5-6000J3636F16G ×4（DDR5-6000） | SMBus（PawnIO）→ 温度；RGB 待接入 |
| 水冷 | ASUS ROG RYUO III（USB 0B05:1A51） | 灯效待接入；泵/风扇走主板 |
| 主板灯效 | AURA LED Controller（USB 0B05:19AF） | 待接入（OpenRGB / 自研 HID） |
| 键盘 | ASUS ROG Falchion Ace HFX（0B05:1B7E） | 待接入 |
| 其他 | Logitech 键鼠、Philips 329P1R 显示器 | 暂不接入 |
| OS | Windows 11 Pro 10.0.26300，VBS/HVCI 开启 | — |

## 2. Phase 0 实测结果（2026-11-08 已通过）

- ✅ 传感器：CPU 温度/频率/电压/功耗、GPU 核心/热点/显存结温、4 条 DIMM 温度、主板温度/电压、7 路风扇转速
- ✅ 风扇控制：NCT6798D 7 个 PWM 通道写入成功（0–100%），测试后均恢复 BIOS 默认
- ✅ GPU 风扇：2 个通道可写（范围 30–100%，0 RPM 为零噪音模式，正常）
- ✅ PawnIO 2.2.0 已安装并运行（`C:\Program Files\PawnIO`，服务 RUNNING）
- ⚠️ 实测映射：Fan #1 / #2 = 机箱风扇（有测速，可调）；Fan #7 ≈3090–3190 RPM、PWM 100%（**可调**，实测解除锁定后固定 PWM 有效；疑似水泵或 AIO 风扇组，待接线确认）；Fan #3–#6 无测速（空接口，BIOS 默认 70% 占空比）
  - 注：ASUS 官网标称 RYUO III 水泵 800–2600 ±300 RPM、AIO 风扇 2200 ±300 RPM，实测 ≈3190 略高于标称上限；若 #7 是水泵，建议保持高转速（如固定 80–100% 或曲线下限 ≥60%），不要按机箱风扇的低转速曲线跑。
- ⚠️ 系统启用了 Defender ASR 规则「阻止不受信任的可执行文件」，需把程序目录加入排除（已把 `D:\Github\Atlas` 加入）

## 3. 当前已实现（v0.2）

```
src/Atlas.Core/    LibreHardwareMonitor 封装 + 风扇控制引擎 + JSON 配置
src/Atlas.Cli/     命令行冒烟测试（list / smoke）
src/Atlas.App/     WPF 界面（要求管理员权限）
app/               当前部署目录（构建输出复制到这里）
run-atlas.cmd      启动脚本
```

功能（v0.1 → v0.2）：

- 1 秒刷新：CPU Package / GPU Core / GPU Hotspot / 内存最高温
- 每个风扇通道三种模式：**BIOS 默认 / 固定 PWM / 温度曲线**
- 固定 PWM：0–100% 滑杆，带斜坡限制
- **曲线编辑器（图形拖拽）**：温度/PWM 坐标图，拖动圆点调整；双击空白加点、右键删点；支持恢复默认曲线
- **锁定**：勾选后 Atlas 永不写该通道（用于水泵/不确定的通道）
- 启动时先恢复 BIOS 默认，再按保存的配置接管；退出/看门狗触发时恢复默认
- 看门狗：UI 卡住超过 8 秒自动 `SetDefault()`，避免风扇停在异常 PWM
- 自动保存配置；配置在 `%LOCALAPPDATA%\Atlas\config.json`
- **开机自启**：按钮创建/删除计划任务 `AtlasFan`（登录时以最高权限启动，不弹 UAC）
- 温度源失效 5 秒后全速兜底
- 曲线/固定模式每秒最多变化 8%，避免转速突变

运行：

```powershell
run-atlas.cmd
```

## 4. 安全设计（已实现 / 待实现）

已实现：

- 默认所有通道不接管（只读监控）
- 只在勾选「启用曲线」后才写 PWM
- 温度源失效 → 全速
- 退出/恢复按钮 → `SetDefault()` 交还 BIOS
- 写入前 clamp 到通道允许范围，斜坡限制避免转速突变

已实现（v0.2 追加）：

- 启动时先 `RestoreAll()`，清理上次异常退出可能留下的软件控制状态
- 独立看门狗（UI 卡住 8 秒自动恢复 BIOS 默认）
- 退出时自动保存配置
- **锁定**通道（永不写入，用于水泵等）
- 曲线编辑器（图形拖拽 + 预设）、复制曲线、通道备注名、隐藏 0 RPM

待实现：

- 风扇启动/停转校准（避免低占空比停转）
- 水泵专用模式（曲线下限保护）

### 4.1 启动性能（2026-11-08 优化）

实测（dist Release 版，进程启动 → 主窗口可见）：

| 阶段 | 优化前 | 优化后 |
| --- | --- | --- |
| 窗口出现 | 6743 ms | **368 ms** |
| 风扇控制就绪 | 6727 ms | **约 2000 ms** |
| DIMM 温度就绪 | 同上（阻塞） | 后台加载，约 7 s 后出现 |

原因与处理：

- 实测 `LibreHardwareMonitor.Computer.Open()` 中 **DDR5 SPD/DIMM 初始化占约 4.5 秒**（其他硬件合计约 0.6 秒）。
- 处理：主初始化只开 CPU/GPU/主板（关掉内存与 controller），窗口先显示；DIMM 温度交给独立 `MemoryService` 在后台打开并每 2 秒刷新，期间温度卡显示“加载中…”。
- 启动耗时记录在 `%LOCALAPPDATA%\Atlas\startup.log`，便于以后回归。
- 如需进一步压缩，可减少首帧 `Update()` 或延后部分传感器；当前 2 秒内即可控制风扇。

## 5. 后续版本

> **范围决定（2026-11-08）**：灯效交给 Armoury Crate，Atlas **只负责风扇 + 传感器监控**。原 v0.3 灯效计划取消（OpenRGB 对 RYUO III / G.Skill DDR5 也不支持，强行自研不划算）。

| 版本 | 内容 |
| --- | --- |
| v0.2（已完成） | 固定 PWM 模式、曲线编辑器、锁定通道（防止误控水泵）、自动保存、开机自启（计划任务最高权限）、启动恢复默认、看门狗 |
| v0.3（分支 `fan-profiles` 原型） | 场景 / 配置切换：统一下拉（内置场景静音 / 均衡 / 性能只读 + 用户命名的「我的配置」），选择即切换；内置预设只读，编辑时提示先复制为「我的配置」；「我的配置」支持另存为 / 保存 / 重命名 / 删除；单扇只读曲线模板；设计见 [风扇预设 / 场景 / 配置设计](fan-profiles-design.md) |
| 后续 | 传感器日志、托盘、配置导入导出、按程序自动切换场景 |
| 不做 | 灯效控制（Armoury Crate 负责） |


## 6. 环境注意事项

- **PawnIO**：已装 2.2.0；卸载或升级后 LHM 会退回只能读 GPU。
- **管理员权限**：风扇写入必须提权；`Atlas.exe` 的 manifest 已设为 `requireAdministrator`。
- **Defender ASR**：当前把 `D:\Github\Atlas` 加了排除；若以后把程序移到别处，需要把新目录加入排除，或用计划任务（最高权限）启动以绕过启动拦截。
- **不要同时开 Armoury Crate / Fan Xpert / FanControl**：同一 SuperIO 只能有一个写者。
- **风险**：SuperIO 直接写属于底层操作；当前只对自己的 NCT6798D 做了验证，不要拿别的机器试。

## 7. 已知限制（v0.1）

- 曲线只能在 JSON 里改，UI 还不能编辑控制点
- 没有「固定 PWM」模式（只能 BIOS 默认或曲线）
- 没有托盘/最小化到托盘、没有开机自启
- 没有灯效
- Fan #3–#6 无测速，无法确认是否接了风扇
- GPU 风扇默认 0 RPM 是零噪音模式，不代表故障
