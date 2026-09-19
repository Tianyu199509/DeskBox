# AOT Release 内存曲线实验协议（2026-09-18）

## 目的

判别合并了五刀（#386/#388/#390/#391/#393）后的 main 在 **AOT Release** 下的长跑内存行为：

- **plateau**（可接受）：批量操作后的私有内存峰值不随批次单调爬升（涨幅 ≤15%），空闲段基线稳定 → 1.5.4 按现状发。
- **持续爬升**（需查 native 侧）：批间峰值单调递增且总涨幅 >15% → native/XAML 持有还有活，1.5.4 前继续排查。

前次实验被一次性 hang 打断；当时的判别已把 hang 归因为旧 cut-state 风暴（#388 已修）。若本次复现 hang：**不要强杀**，先按既有工具收 dump（cdb + 零售符号），两份旧 dump 仍在 %TEMP% 留证。

## 隔离（关键）

实验实例通过 `DESKBOX_AOT_PREVIEW_DATA_ROOT` 指向独立数据根（默认 `%LOCALAPPDATA%\DeskBox-AotCurve`）：

- 单实例锁按数据根派生独立 scope → **可与正在运行的安装版 1.5.3 共存**；
- settings / log / recovery journal 全部落在实验根 → **绝不触碰真实数据目录与 19.8MB 现场**。

## 工具

- 采样器：`scripts/measure-aot-memory-curve.ps1`（首阶段负责启动实验实例；后续阶段 `-Attach` 附加）
- 汇总判别：`scripts/summarize-aot-memory-curve.ps1`（按阶段输出峰值/末值、批间趋势 verdict、对照 [Memory] 托管堆行）
- 指标口径：**私有提交（PrivateMB）为准**，工作集仅参考（trim 会干扰 WS）

## 构建备注（已备好，重复实验时用）

NativeAOT 是 opt-in 审计档案，**直接用助手脚本**（本机 BuildTools 默认 VC 工具集 14.42 缺 x64 静态 CRT，脚本会 pin 到 14.44 并让 ILCompiler 用环境工具链）：

```powershell
# 从仓库根目录
cmd /c scripts\publish-aot-x64.cmd
```

等价的裸命令（在 `vcvarsall.bat amd64 -vcvars_ver=14.44` 环境内）：

```powershell
dotnet publish src/DeskBox/DeskBox.csproj -c Release `
    -p:Platform=x64 -p:RuntimeIdentifier=win-x64 `
    -p:DeskBoxAotAudit=true -p:DeskBoxRustNative=true `
    -p:IlcUseEnvironmentalTools=true
```

已踩的坑（2026-09-18 记录）：
1. 按 AGENTS.md 安装器流程传 `-p:SelfContained=false` 会**静默退化成 JIT 输出**——publish 里出现 10MB 的 DeskBox.dll、exe 只有几百 KB 就是没走 AOT；JIT 产物混进 publish 目录会污染判定，重建前先删 publish 目录。
2. `DeskBoxAotAudit=true` 单独不够：还需要 `DeskBoxRustNative=true`（否则 Validate target 报错，AOT 必须带 Rust 后端）。
3. 本机 BuildTools 默认工具集 `14.42.34433` 的 `lib/x64` **缺 LIBCMT.lib**（arm64 全、14.44.35207 全）→ `LNK1104` 链接失败。`-p:VCToolsVersion` 对 ILCompiler 的 vswhere 探测无效，必须走 `vcvarsall -vcvars_ver=14.44` + `IlcUseEnvironmentalTools=true`。
4. 验证特征：真 AOT 输出 **无 DeskBox.dll**，exe 约 46MB，`deskbox_native.dll` 在位。


## 阶段指令（Simon 手动操作，采样器全程跑着）

```powershell
# 准备（可选）：生成 2500 个测试文件到暂存目录（盘符自选）
$d = 'E:\DeskBox-AotCurve-Staging'; New-Item -ItemType Directory -Force $d | Out-Null
0..2499 | ForEach-Object { Set-Content -Path (Join-Path $d ("t{0:d4}.png" -f $_)) -Value 'x' }

# 阶段 1 —— baseline（5 分钟）：启动实验实例，走完首启，
# 建一个文件格子指向暂存目录所在盘的接收文件夹，然后什么都不做
./scripts/measure-aot-memory-curve.ps1 -Phase baseline -Minutes 5

# 阶段 2 —— batch1：把 2500 个测试文件一次性拖进格子（中途观察采样行）
./scripts/measure-aot-memory-curve.ps1 -Phase batch1 -Minutes 8 -Attach

# 阶段 3 —— idle1（5 分钟）：什么都不做（可最小化/收起格子）
./scripts/measure-aot-memory-curve.ps1 -Phase idle1 -Minutes 5 -Attach

# 阶段 4 —— batch2：全选格子内文件剪切到桌面（触发 move-out 路径，#391 场景），
#          再拖回格子一次（触发 import + cut 混合，#388/#386 场景）
./scripts/measure-aot-memory-curve.ps1 -Phase batch2 -Minutes 8 -Attach

# 阶段 5 —— idle2（5 分钟）
./scripts/measure-aot-memory-curve.ps1 -Phase idle2 -Minutes 5 -Attach

# 阶段 6（可选）—— batch3 + idle3：重复一次大批量，凑 3 个批数据点
./scripts/measure-aot-memory-curve.ps1 -Phase batch3 -Minutes 8 -Attach
./scripts/measure-aot-memory-curve.ps1 -Phase idle3 -Minutes 10 -Attach
```

操作中途想打时间标记：在采样器控制台按 **M** + 回车（插入 MARKER 行）。

## 判读

```powershell
./scripts/summarize-aot-memory-curve.ps1
```

- 看 `batch-phase peak trend` 的自动 verdict；
- 交叉验证：实验根 `DeskBox.log` 的 `[Memory]` 行（`managedHeapAfterMB` vs `privateAfterMB`）——
  托管堆稳定而私有爬升 = native 侧；两者都爬 = 托管还有放大器；
- 若 verdict 是 plateau：AOT 线结案，1.5.4 进入打包流程；
- 若持续爬升：留 dump + 曲线 CSV，转入 native 持有排查（cdb 路线）。

## 收尾

- 实验数据根与暂存文件可整目录删除（不含任何真实数据）；
- CSV / 协议 / 脚本留在仓库工作区（未提交，随 1.5.4 批次处置）。
