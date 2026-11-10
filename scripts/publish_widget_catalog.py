#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""DeskBox 自定义格子远程目录（OTA）一键发布与校验工具。

远程拉取双通道架构（客户端内置）：
  1. 主通道（官网 CDN）：https://deskbox.fun/widgets/catalog.v1.json
  2. 兜底镜像（GitHub）：https://raw.githubusercontent.com/Tianyu199509/DeskBox/main/catalog/widgets.v1.json

用法示例：
  # 1) 查看当前远程目录状态
  python scripts/publish_widget_catalog.py list

  # 2) 将内置模板 pomodoro 打补丁版本推送给全量用户（支持 --bump patch/minor/major 或 --version x.y.z）
  python scripts/publish_widget_catalog.py upsert --builtin pomodoro --bump patch

  # 3) 上架一个全新的本地格子目录到远程模板库（免发客户端新版本）
  python scripts/publish_widget_catalog.py upsert --dir ./my-new-widget --id habit-ring --version 1.0.0

  # 4) 远程下架某个模板（工坊不再展示；用户已放桌面的实例保留不删数据）
  python scripts/publish_widget_catalog.py deprecate --id habit-ring

  # 5) 恢复上架被下架的模板
  python scripts/publish_widget_catalog.py undeprecate --id habit-ring

  # 6) 从远程目录彻底移除某条远程条目，并同时导出一份供官网部署的 catalog.v1.json
  python scripts/publish_widget_catalog.py validate --website-out ./artifacts/catalog.v1.json
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import pathlib
import re
import shutil
import sys
from typing import Any

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

ROOT = pathlib.Path(__file__).resolve().parents[1]
DEFAULT_CATALOG_PATH = ROOT / "catalog" / "widgets.v1.json"
BUILTIN_TEMPLATES_ROOT = ROOT / "src" / "DeskBox" / "Assets" / "CustomWidgets" / "Templates"

MAX_MANIFEST_BYTES = 512 * 1024
MAX_LOGIC_JS_BYTES = 128 * 1024
MAX_LOCALE_TOTAL_BYTES = 1024 * 1024
SEMVER_RE = re.compile(r"^(\d+)\.(\d+)\.(\d+)$")
TEMPLATE_ID_RE = re.compile(r"^[a-z0-9][a-z0-9._-]{1,63}$", re.IGNORECASE)


def utc_now_iso() -> str:
    return dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def normalize_short_id(raw_id: str) -> str:
    val = raw_id.strip()
    for prefix in ("deskbox.", "starter."):
        if val.lower().startswith(prefix):
            val = val[len(prefix):]
            break
    return val.strip().lower()


def bump_semver(ver: str, part: str) -> str:
    m = SEMVER_RE.match(ver.strip())
    if not m:
        raise ValueError(f"无法在非标准版本号 '{ver}' 上执行 --bump {part}，请显式传入 --version x.y.z")
    major, minor, patch = int(m.group(1)), int(m.group(2)), int(m.group(3))
    if part == "major":
        return f"{major + 1}.0.0"
    if part == "minor":
        return f"{major}.{minor + 1}.0"
    return f"{major}.{minor}.{patch + 1}"


def load_catalog(path: pathlib.Path) -> dict[str, Any]:
    if not path.exists():
        return {"schemaVersion": 2, "updatedAtUtc": utc_now_iso(), "widgets": []}
    with path.open("r", encoding="utf-8-sig") as f:
        doc = json.load(f)
    if not isinstance(doc, dict):
        raise ValueError(f"{path} 根节点必须是 JSON 对象")
    doc.setdefault("schemaVersion", 2)
    doc.setdefault("updatedAtUtc", utc_now_iso())
    doc.setdefault("widgets", [])
    return doc


def save_catalog(doc: dict[str, Any], path: pathlib.Path, website_out: pathlib.Path | None = None) -> None:
    doc["schemaVersion"] = 2
    doc["updatedAtUtc"] = utc_now_iso()
    path.parent.mkdir(parents=True, exist_ok=True)
    text = json.dumps(doc, ensure_ascii=False, indent=2) + "\n"
    path.write_text(text, encoding="utf-8")
    print(f"[OK] 已写入远程目录: {path}（共 {len(doc.get('widgets', []))} 条记录）")
    if website_out is not None:
        website_out.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(path, website_out)
        print(f"[OK] 已同步导出官网部署副本: {website_out}")


def load_workspace_bundle(folder: pathlib.Path) -> tuple[dict[str, Any], str | None, str | None, dict[str, dict[str, str]]]:
    manifest_path = folder / "manifest.json"
    if not manifest_path.is_file():
        raise FileNotFoundError(f"找不到 manifest.json: {manifest_path}")

    raw_manifest_bytes = manifest_path.read_bytes()
    if len(raw_manifest_bytes) > MAX_MANIFEST_BYTES:
        raise ValueError(f"manifest.json 超过 {MAX_MANIFEST_BYTES} 字节上限 ({len(raw_manifest_bytes)} bytes)")
    manifest = json.loads(raw_manifest_bytes.decode("utf-8-sig"))
    if not isinstance(manifest, dict):
        raise ValueError("manifest.json 根节点必须是对象")

    logic_js: str | None = None
    logic_path = folder / "logic.js"
    if logic_path.is_file():
        logic_bytes = logic_path.read_bytes()
        if len(logic_bytes) > MAX_LOGIC_JS_BYTES:
            raise ValueError(f"logic.js 超过 {MAX_LOGIC_JS_BYTES} 字节上限 ({len(logic_bytes)} bytes)")
        logic_js = logic_bytes.decode("utf-8-sig")

    base_locale: str | None = None
    locales: dict[str, dict[str, str]] = {}
    locales_dir = folder / "locales"
    if locales_dir.is_dir():
        total_locale_bytes = 0
        meta_path = locales_dir / "_meta.json"
        if meta_path.is_file():
            meta = json.loads(meta_path.read_text(encoding="utf-8-sig"))
            if isinstance(meta, dict) and isinstance(meta.get("baseLocale"), str):
                base_locale = meta["baseLocale"]
        for loc_file in sorted(locales_dir.glob("*.json")):
            if loc_file.name.startswith("_"):
                continue
            b = loc_file.read_bytes()
            total_locale_bytes += len(b)
            if total_locale_bytes > MAX_LOCALE_TOTAL_BYTES:
                raise ValueError(f"locales 总大小超过 {MAX_LOCALE_TOTAL_BYTES} 字节上限")
            table = json.loads(b.decode("utf-8-sig"))
            if isinstance(table, dict):
                locales[loc_file.stem] = {str(k): str(v) for k, v in table.items() if isinstance(v, (str, int, float))}

    return manifest, logic_js, base_locale, locales


def validate_catalog_doc(doc: dict[str, Any]) -> list[str]:
    problems: list[str] = []
    if doc.get("schemaVersion") != 2:
        problems.append(f"schemaVersion 必须为 2，当前为 {doc.get('schemaVersion')}")
    widgets = doc.get("widgets")
    if not isinstance(widgets, list):
        problems.append("widgets 字段必须是数组")
        return problems

    seen_ids: set[str] = set()
    for idx, item in enumerate(widgets):
        if not isinstance(item, dict):
            problems.append(f"widgets[{idx}] 必须是对象")
            continue
        raw_id = str(item.get("id", "")).strip()
        norm_id = normalize_short_id(raw_id)
        if not norm_id or not TEMPLATE_ID_RE.match(norm_id):
            problems.append(f"widgets[{idx}] id 非法: '{raw_id}'")
        elif norm_id in seen_ids:
            problems.append(f"widgets[{idx}] 存在重复 id: '{norm_id}'")
        seen_ids.add(norm_id)

        if item.get("deprecated") is True:
            continue

        ver = str(item.get("version", "")).strip()
        if not SEMVER_RE.match(ver):
            problems.append(f"模板 '{norm_id}' 的 version 必须是 x.y.z 三段语义化版本号，当前为 '{ver}'")

        min_app = item.get("minAppVersion")
        if min_app is not None and not SEMVER_RE.match(str(min_app).strip()):
            problems.append(f"模板 '{norm_id}' 的 minAppVersion 必须是 x.y.z 格式，当前为 '{min_app}'")

        has_manifest = (
            isinstance(item.get("manifest"), dict)
            or bool(str(item.get("manifestJson") or "").strip())
            or bool(str(item.get("manifestUrl") or "").strip())
            or bool(str(item.get("bundleUrl") or "").strip())
        )
        if not has_manifest:
            problems.append(f"非下架模板 '{norm_id}' 缺少 manifest / manifestJson / manifestUrl / bundleUrl")

        manifest_obj = item.get("manifest")
        if isinstance(manifest_obj, dict):
            if "view" not in manifest_obj:
                problems.append(f"模板 '{norm_id}' 的内联 manifest 缺少 view 根节点")
            encoded = json.dumps(manifest_obj, ensure_ascii=False).encode("utf-8")
            if len(encoded) > MAX_MANIFEST_BYTES:
                problems.append(f"模板 '{norm_id}' 的 manifest 超过 512KB 上限")

        logic_js = item.get("logicJs")
        if isinstance(logic_js, str) and len(logic_js.encode("utf-8")) > MAX_LOGIC_JS_BYTES:
            problems.append(f"模板 '{norm_id}' 的 logicJs 超过 128KB 上限")

    return problems


def cmd_list(args: argparse.Namespace) -> int:
    doc = load_catalog(args.catalog)
    widgets = doc.get("widgets", [])
    print(f"远程目录文件: {args.catalog}")
    print(f"更新时间 (UTC): {doc.get('updatedAtUtc', '-')}")
    print(f"条目总数: {len(widgets)}")
    for item in widgets:
        status = "DEPRECATED (已下架)" if item.get("deprecated") else "ACTIVE (在线)"
        locales_count = len(item.get("locales") or {})
        has_js = "yes" if item.get("logicJs") or item.get("logicUrl") else "no"
        min_app = item.get("minAppVersion") or "-"
        print(
            f"  - {item.get('id'):<18} v{item.get('version', '-'):<8} [{status}] "
            f"minApp={min_app:<7} locales={locales_count:<2} js={has_js} name={item.get('name', '')}"
        )
    return 0


def cmd_upsert(args: argparse.Namespace) -> int:
    if bool(args.builtin) == bool(args.dir):
        print("[ERROR] 请二选一指定 --builtin <内置模板名> 或 --dir <本地格子目录>", file=sys.stderr)
        return 1

    folder = (BUILTIN_TEMPLATES_ROOT / args.builtin) if args.builtin else pathlib.Path(args.dir).resolve()
    if not folder.is_dir():
        print(f"[ERROR] 目录不存在: {folder}", file=sys.stderr)
        return 1

    manifest, logic_js, base_locale, locales = load_workspace_bundle(folder)
    doc = load_catalog(args.catalog)
    widgets: list[dict[str, Any]] = doc["widgets"]

    short_id = normalize_short_id(args.id or (args.builtin or str(manifest.get("id") or folder.name)))
    manifest_id = str(manifest.get("id") or f"deskbox.{short_id}").strip()
    if not manifest_id.lower().startswith(("deskbox.", "starter.")):
        manifest_id = f"deskbox.{short_id}"
    manifest["id"] = manifest_id

    existing_idx = next((i for i, w in enumerate(widgets) if normalize_short_id(str(w.get("id", ""))) == short_id), -1)
    baseline_version = (
        str(widgets[existing_idx].get("version"))
        if existing_idx >= 0 and widgets[existing_idx].get("version")
        else str(manifest.get("version") or "1.0.0")
    )

    if args.version:
        target_version = args.version.strip()
    elif args.bump:
        target_version = bump_semver(baseline_version, args.bump)
    else:
        target_version = baseline_version

    if not SEMVER_RE.match(target_version):
        print(f"[ERROR] 版本号 '{target_version}' 必须是 x.y.z 格式", file=sys.stderr)
        return 1

    manifest["version"] = target_version
    if args.min_app_version:
        manifest["minAppVersion"] = args.min_app_version.strip()
    manifest.pop("isCustomized", None)
    manifest["origin"] = "official"

    default_size = manifest.get("defaultSize") if isinstance(manifest.get("defaultSize"), dict) else {}
    width = int(default_size.get("width") or 324)
    height = int(default_size.get("height") or 360)

    names_map: dict[str, str] = {}
    descriptions_map: dict[str, str] = {}
    for lang, table in locales.items():
        for nk in ("meta.name", "widgetName", "title"):
            if table.get(nk):
                names_map[lang] = table[nk]
                break
        for dk in ("meta.description", "widgetDesc", "description"):
            if table.get(dk):
                descriptions_map[lang] = table[dk]
                break

    zh_name = names_map.get("zh-CN") or str(manifest.get("name") or short_id)
    en_name = names_map.get("en-US")
    zh_desc = descriptions_map.get("zh-CN") or str(manifest.get("description") or "")
    en_desc = descriptions_map.get("en-US")

    entry: dict[str, Any] = {
        "id": short_id,
        "manifestId": manifest_id,
        "name": zh_name,
        "description": zh_desc,
        "iconGlyph": str(manifest.get("icon") or "\uE71D"),
        "version": target_version,
        "deprecated": False,
        "defaultWidth": width,
        "defaultHeight": height,
        "manifest": manifest,
    }
    if en_name:
        entry["nameEn"] = en_name
    if names_map:
        entry["names"] = names_map
    if en_desc:
        entry["descriptionEn"] = en_desc
    if descriptions_map:
        entry["descriptions"] = descriptions_map
    if args.feature_tag:
        entry["featureTag"] = args.feature_tag
    min_app_val = args.min_app_version or manifest.get("minAppVersion")
    if min_app_val:
        entry["minAppVersion"] = str(min_app_val)
    if logic_js:
        entry["logicJs"] = logic_js
    if base_locale:
        entry["baseLocale"] = base_locale
    if locales:
        entry["locales"] = locales

    if existing_idx >= 0:
        widgets[existing_idx] = entry
    else:
        widgets.append(entry)

    problems = validate_catalog_doc(doc)
    if problems:
        for p in problems:
            print(f"[FAIL] {p}", file=sys.stderr)
        return 1

    save_catalog(doc, args.catalog, args.website_out)
    print(f"[OK] 已发布模板 '{short_id}' -> v{target_version}")
    return 0


def cmd_set_deprecated(args: argparse.Namespace, deprecated: bool) -> int:
    short_id = normalize_short_id(args.id)
    doc = load_catalog(args.catalog)
    widgets: list[dict[str, Any]] = doc["widgets"]
    existing = next((w for w in widgets if normalize_short_id(str(w.get("id", ""))) == short_id), None)

    if existing is None:
        if not deprecated:
            print(f"[ERROR] 远程目录中不存在 '{short_id}'，无需恢复上架", file=sys.stderr)
            return 1
        widgets.append({
            "id": short_id,
            "manifestId": f"deskbox.{short_id}",
            "name": short_id,
            "version": "999.0.0",
            "deprecated": True,
        })
    else:
        existing["deprecated"] = deprecated

    problems = validate_catalog_doc(doc)
    if problems:
        for p in problems:
            print(f"[FAIL] {p}", file=sys.stderr)
        return 1

    save_catalog(doc, args.catalog, args.website_out)
    action_text = "已标记远程下架 (deprecated=true)" if deprecated else "已恢复在线上架 (deprecated=false)"
    print(f"[OK] 模板 '{short_id}' {action_text}")
    return 0


def cmd_remove(args: argparse.Namespace) -> int:
    short_id = normalize_short_id(args.id)
    doc = load_catalog(args.catalog)
    before = len(doc["widgets"])
    doc["widgets"] = [w for w in doc["widgets"] if normalize_short_id(str(w.get("id", ""))) != short_id]
    if len(doc["widgets"]) == before:
        print(f"[WARN] 远程目录中未找到 '{short_id}'")
        return 0
    save_catalog(doc, args.catalog, args.website_out)
    print(f"[OK] 已从远程目录移除条目 '{short_id}'")
    return 0


def cmd_validate(args: argparse.Namespace) -> int:
    doc = load_catalog(args.catalog)
    problems = validate_catalog_doc(doc)
    if problems:
        for p in problems:
            print(f"  [FAIL] {p}", file=sys.stderr)
        return 1
    if args.website_out is not None:
        save_catalog(doc, args.catalog, args.website_out)
    print(f"[OK] 远程目录校验通过: {args.catalog}（共 {len(doc.get('widgets', []))} 个条目）")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="DeskBox 自定义格子远程目录 (OTA) 一键发布与下架管理工具")
    parser.add_argument("--catalog", type=pathlib.Path, default=DEFAULT_CATALOG_PATH, help="目录 JSON 路径（默认 catalog/widgets.v1.json）")
    parser.add_argument("--website-out", type=pathlib.Path, default=None, help="可选：同步导出一份官网部署文件（如 catalog.v1.json）")

    sub = parser.add_subparsers(dest="cmd", required=True)

    sub.add_parser("list", help="列出当前远程目录中的所有模板及状态")
    sub.add_parser("validate", help="静态校验远程目录格式、体积上限与版本号规范")

    p_upsert = sub.add_parser("upsert", help="发布新模板或向全量用户推送现有官方模板的新版本")
    p_upsert.add_argument("--builtin", help="从仓库内置模板目录读取（例如 pomodoro / countdown / sys-pulse）")
    p_upsert.add_argument("--dir", help="从任意本地格子文件夹读取（含 manifest.json / logic.js / locales）")
    p_upsert.add_argument("--id", help="覆盖模板短 ID（默认自动从内置名或 manifest.id 提取）")
    p_upsert.add_argument("--version", help="显式指定发布的 x.y.z 版本号（必须高于旧版本才会触发客户端自动热升级）")
    p_upsert.add_argument("--bump", choices=("patch", "minor", "major"), help="在现有版本基础上自动递增版本号")
    p_upsert.add_argument("--min-app-version", help="要求最低 DeskBox 客户端版本（如 1.6.0）")
    p_upsert.add_argument("--feature-tag", help="工坊卡片角标标签文案")

    p_dep = sub.add_parser("deprecate", help="远程下架指定模板（从工坊隐藏，不影响用户桌面已有实例与数据）")
    p_dep.add_argument("--id", required=True, help="要下架的模板 ID（支持内置模板或远程模板）")

    p_undep = sub.add_parser("undeprecate", help="恢复之前标记为 deprecated 的远程模板")
    p_undep.add_argument("--id", required=True, help="要恢复上架的模板 ID")

    p_rm = sub.add_parser("remove", help="从远程目录 JSON 中删除指定远程条目")
    p_rm.add_argument("--id", required=True, help="要删除的条目 ID")

    args = parser.parse_args()
    if args.cmd == "list":
        return cmd_list(args)
    if args.cmd == "validate":
        return cmd_validate(args)
    if args.cmd == "upsert":
        return cmd_upsert(args)
    if args.cmd == "deprecate":
        return cmd_set_deprecated(args, deprecated=True)
    if args.cmd == "undeprecate":
        return cmd_set_deprecated(args, deprecated=False)
    if args.cmd == "remove":
        return cmd_remove(args)
    return 1


if __name__ == "__main__":
    sys.exit(main())
