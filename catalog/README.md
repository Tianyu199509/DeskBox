# DeskBox Remote Catalog (`catalog/`)

本目录存放 DeskBox 自定义格子的远程 OTA 数据源（GitHub 镜像通道，同时作为官网 `https://deskbox.fun/widgets/` 的发布源）：

- `widgets.v1.json`：官方格子远程目录（官网部署目标 URL 为 `https://deskbox.fun/widgets/catalog.v1.json`）
- `prompts.v1.json`：AI 生成提示词与设计规则热更新包（官网部署目标 URL 为 `https://deskbox.fun/widgets/prompts.v1.json`）

## Agent / 开发者操作入口

**任何模板上架、版本热推送、下架（`deprecate`）或官网部署操作，请先完整阅读：**

- [自定义格子远程模板库与 AI 提示词 OTA 运维手册](../docs/architecture/custom-widget-remote-catalog-workflow.md)

### 常用一键命令速查

```powershell
# 1. 查看当前远程目录条目与状态
python scripts/publish_widget_catalog.py list

# 2. 推送内置模板热更新（版本号自动 +0.0.1），并同步导出官网 catalog.v1.json
python scripts/publish_widget_catalog.py --website-out ./artifacts/website-widgets/catalog.v1.json upsert --builtin pomodoro --bump patch

# 3. 远程上架全新模板文件夹
python scripts/publish_widget_catalog.py --website-out ./artifacts/website-widgets/catalog.v1.json upsert --dir ./path/to/widget --id my-widget --version 1.0.0

# 4. 远程下架模板（含内置模板隐藏，必须用 deprecate 而非 remove）
python scripts/publish_widget_catalog.py --website-out ./artifacts/website-widgets/catalog.v1.json deprecate --id my-widget

# 5. 静态校验远程目录规范与体积上限
python scripts/publish_widget_catalog.py validate
```
