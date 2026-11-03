#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""DeskBox AI 创建格子：本地化门禁 + 一次性资源清理。

用法：
  python scripts/check_ai_widget_localization.py                 # 检查并输出报告
  python scripts/check_ai_widget_localization.py --apply-cleanup # 执行 P0 资源清理（幂等）

检查项：
  1. 13 个 Strings/*.json 的 key 集合是否完全一致；
  2. 面向用户的重点前缀在非中文语种中是否仍回退为 en-US 原文；
  3. 已删除的旧引导词条 / 已改名的 key 是否残留。
"""
from __future__ import annotations

import argparse
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
STRINGS = ROOT / "src" / "DeskBox" / "Strings"

DEAD_PREFIXES = (
    "CustomWidget.Guide.",
    "CustomWidget.AiWizard.Step",
    "CustomWidget.InfoTip.Overview.",
    "CustomWidget.InfoTip.Modes.",
    "CustomWidget.InfoTip.Capsule.",
)
# 仍被 xaml.cs 引用的词条必须保留
KEEP_KEYS = {"CustomWidget.AiWizard.Step3.CopyRepairButton"}

RENAMES = {
    "CustomWidget.Workshop.AgentMcp.GuideButton": "CustomWidget.Workshop.GuideButton",
}

STAGE_KEYS = (
    "CustomWidget.Workshop.AgentStage.Notice",
    "CustomWidget.Workshop.AgentStage.ViewButton",
    "CustomWidget.Workshop.AgentStage.DiscardButton",
    "CustomWidget.Workshop.AgentStage.ReviewHint",
    "CustomWidget.Workshop.AgentStage.CommitFailed",
    "CustomWidget.Workshop.AgentStage.HistoryLabel",
)

# 失败提示按场景分类：标题 + 一句「下一步做什么」+ 折叠技术详情。
# 这四类是原来直接吐 ex.Message 的六处调用点，缺一条就会退回英文异常原文。
FAILURE_KEYS = (
    "CustomWidget.Workshop.Failure.SaveTitle",
    "CustomWidget.Workshop.Failure.SaveBody",
    "CustomWidget.Workshop.Failure.OpenToolTitle",
    "CustomWidget.Workshop.Failure.OpenToolBody",
    "CustomWidget.Workshop.Failure.ClipboardTitle",
    "CustomWidget.Workshop.Failure.ClipboardBody",
    "CustomWidget.Workshop.Failure.I18nTitle",
    "CustomWidget.Workshop.Failure.I18nBody",
)
STAGE_COPY = {
    "zh-CN": {"CustomWidget.Workshop.AgentStage.Notice": "有 {0} 处来自外部工具的更改待确认，桌面尚未改变。", "CustomWidget.Workshop.AgentStage.ViewButton": "查看预览"},
    "zh-TW": {"CustomWidget.Workshop.AgentStage.Notice": "有 {0} 處來自外部工具的變更待確認，桌面尚未改變。", "CustomWidget.Workshop.AgentStage.ViewButton": "查看預覽"},
    "en-US": {"CustomWidget.Workshop.AgentStage.Notice": "{0} pending change(s) from an external tool; the desktop is unchanged.", "CustomWidget.Workshop.AgentStage.ViewButton": "Preview"},
    "ja-JP": {"CustomWidget.Workshop.AgentStage.Notice": "外部ツールからの保留中の変更が {0} 件あります。デスクトップは変更されていません。", "CustomWidget.Workshop.AgentStage.ViewButton": "プレビュー"},
    "ko-KR": {"CustomWidget.Workshop.AgentStage.Notice": "외부 도구의 대기 중인 변경 {0}건이 있습니다. 바탕 화면은 변경되지 않았습니다.", "CustomWidget.Workshop.AgentStage.ViewButton": "미리 보기"},
    "de-DE": {"CustomWidget.Workshop.AgentStage.Notice": "{0} ausstehende Änderung(en) von einem externen Tool; der Desktop ist unverändert.", "CustomWidget.Workshop.AgentStage.ViewButton": "Vorschau"},
    "fr-FR": {"CustomWidget.Workshop.AgentStage.Notice": "{0} modification(s) en attente d'un outil externe ; le bureau est inchangé.", "CustomWidget.Workshop.AgentStage.ViewButton": "Aperçu"},
    "es-ES": {"CustomWidget.Workshop.AgentStage.Notice": "{0} cambio(s) pendiente(s) de una herramienta externa; el escritorio no ha cambiado.", "CustomWidget.Workshop.AgentStage.ViewButton": "Vista previa"},
    "ru-RU": {"CustomWidget.Workshop.AgentStage.Notice": "Ожидают подтверждения {0} изменени(й) от внешнего инструмента; рабочий стол не изменён.", "CustomWidget.Workshop.AgentStage.ViewButton": "Предпросмотр"},
    "pt-BR": {"CustomWidget.Workshop.AgentStage.Notice": "{0} alteração(ões) pendente(s) de uma ferramenta externa; a área de trabalho não foi alterada.", "CustomWidget.Workshop.AgentStage.ViewButton": "Pré-visualizar"},
    "hi-IN": {"CustomWidget.Workshop.AgentStage.Notice": "बाह्य टूल से {0} परिवर्तन पुष्टि के लिए लंबित हैं; डेस्कटॉप अपरिवर्तित है।", "CustomWidget.Workshop.AgentStage.ViewButton": "पूर्वावलोकन"},
    "bn-BD": {"CustomWidget.Workshop.AgentStage.Notice": "বাহ্যিক টুল থেকে {0} টি পরিবর্তন নিশ্চিতকরণের অপেক্ষায় আছে; ডেস্কটপ অপরিবর্তিত।", "CustomWidget.Workshop.AgentStage.ViewButton": "পূর্বরূপ"},
    "ar-SA": {"CustomWidget.Workshop.AgentStage.Notice": "هناك {0} تغيير(ات) بانتظار التأكيد من أداة خارجية؛ سطح المكتب لم يتغير.", "CustomWidget.Workshop.AgentStage.ViewButton": "معاينة"},
}

# P2 GUI 闭环：待确认提示条的「忽略」、预览态说明、提交失败兜底、修订来源标签。
STAGE_EXTRA = {
    "zh-CN": ("忽略", "这是外部工具暂存的待确认版本，桌面尚未改变。点「放到桌面」后才会生效。", "提交失败，暂存内容仍保留，可稍后重试。", "外部工具暂存"),
    "zh-TW": ("忽略", "這是外部工具暫存的待確認版本，桌面尚未改變。點「放到桌面」後才會生效。", "提交失敗，暫存內容仍保留，可稍後重試。", "外部工具暫存"),
    "en-US": ("Ignore", "This is a pending change from an external tool; the desktop is unchanged. It takes effect only after you choose Place on desktop.", "Commit failed; the pending change is still kept and can be retried.", "External tool (pending)"),
    "ja-JP": ("無視", "外部ツールからの保留中の変更です。デスクトップは変更されていません。「デスクトップに配置」を選ぶと反映されます。", "コミットに失敗しました。保留中の変更は保持され、再試行できます。", "外部ツール（保留中）"),
    "ko-KR": ("무시", "외부 도구가 대기시킨 변경입니다. 바탕 화면은 변경되지 않았으며 '바탕 화면에 놓기'를 선택해야 반영됩니다.", "커밋에 실패했습니다. 대기 중인 변경은 유지되며 다시 시도할 수 있습니다.", "외부 도구 (대기 중)"),
    "de-DE": ("Ignorieren", "Dies ist eine ausstehende Änderung eines externen Tools; der Desktop ist unverändert. Sie wird erst nach „Auf Desktop platzieren“ wirksam.", "Übernahme fehlgeschlagen; die ausstehende Änderung bleibt erhalten und kann erneut versucht werden.", "Externes Tool (ausstehend)"),
    "fr-FR": ("Ignorer", "Modification en attente d'un outil externe ; le bureau est inchangé. Elle ne sera appliquée qu'après « Placer sur le bureau ».", "Échec de la validation ; la modification en attente est conservée et peut être relancée.", "Outil externe (en attente)"),
    "es-ES": ("Ignorar", "Cambio pendiente de una herramienta externa; el escritorio no ha cambiado. Solo se aplicará al elegir «Colocar en el escritorio».", "Error al confirmar; el cambio pendiente se conserva y se puede reintentar.", "Herramienta externa (pendiente)"),
    "ru-RU": ("Игнорировать", "Ожидающее изменение от внешнего инструмента; рабочий стол не изменён. Оно вступит в силу только после «Поместить на рабочий стол».", "Не удалось зафиксировать; ожидающее изменение сохранено, можно повторить.", "Внешний инструмент (ожидает)"),
    "pt-BR": ("Ignorar", "Alteração pendente de uma ferramenta externa; a área de trabalho não foi alterada. Só será aplicada após escolher «Colocar na área de trabalho».", "Falha ao confirmar; a alteração pendente foi mantida e pode ser repetida.", "Ferramenta externa (pendente)"),
    "hi-IN": ("अनदेखा करें", "यह बाह्य टूल का लंबित परिवर्तन है; डेस्कटॉप अपरिवर्तित है। «डेस्कटॉप पर रखें» चुनने पर ही लागू होगा।", "कमिट विफल; लंबित परिवर्तन सुरक्षित है और दोबारा प्रयास किया जा सकता है।", "बाह्य टूल (लंबित)"),
    "bn-BD": ("উপেক্ষা করুন", "এটি বাহ্যিক টুলের অপেক্ষমাণ পরিবর্তন; ডেস্কটপ অপরিবর্তিত। «ডেস্কটপে রাখুন» বেছে নিলেই কার্যকর হবে।", "কমিট ব্যর্থ; অপেক্ষমাণ পরিবর্তন সংরক্ষিত আছে, আবার চেষ্টা করা যাবে।", "বাহ্যিক টুল (অপেক্ষমাণ)"),
    "ar-SA": ("تجاهل", "هذا تغيير معلق من أداة خارجية؛ سطح المكتب لم يتغير. لن يُطبَّق إلا بعد اختيار «وضع على سطح المكتب».", "فشل التأكيد؛ لا يزال التغيير المعلق محفوظًا ويمكن إعادة المحاولة.", "أداة خارجية (معلق)"),
}
STAGE_EXTRA_COPY = {
    lang: {
        "CustomWidget.Workshop.AgentStage.DiscardButton": values[0],
        "CustomWidget.Workshop.AgentStage.ReviewHint": values[1],
        "CustomWidget.Workshop.AgentStage.CommitFailed": values[2],
        "CustomWidget.Workshop.AgentStage.HistoryLabel": values[3],
    }
    for lang, values in STAGE_EXTRA.items()
}

NEW_KEY = "CustomWidget.Workshop.Diag.TechDetailsHeader"
INSERT_AFTER = "CustomWidget.Workshop.Diag.ErrorBody"
TECH_HEADER = {
    "zh-CN": "查看技术详情", "zh-TW": "查看技術詳情", "en-US": "Technical details",
    "ja-JP": "技術的な詳細", "ko-KR": "기술 세부 정보", "de-DE": "Technische Details",
    "fr-FR": "Détails techniques", "es-ES": "Detalles técnicos", "ru-RU": "Технические подробности",
    "pt-BR": "Detalhes técnicos", "hi-IN": "तकनीकी विवरण", "bn-BD": "প্রযুক্তিগত বিবরণ",
    "ar-SA": "التفاصيل التقنية",
}

EXTRA_COPY = {
    lang: {
        NEW_KEY: TECH_HEADER.get(lang, TECH_HEADER["en-US"]),
        **STAGE_COPY.get(lang, {}),
        **STAGE_EXTRA_COPY.get(lang, {}),
    }
    for lang in TECH_HEADER
}

# P0 文案修正：按钮名实相符、术语统一
VALUE_FIXES = {
    "zh-CN": {
        "CustomWidget.Workshop.Upsell.Button": "配置模型服务",
        "CustomWidget.Workshop.Upsell.Message": "想更快？配置一个模型服务，下次输入想法就能一键生成。",
    },
    "zh-TW": {
        "CustomWidget.Workshop.Upsell.Button": "設定模型服務",
        "CustomWidget.Workshop.Upsell.Message": "想更快？設定一個模型服務，下次輸入想法就能一鍵生成。",
    },
    "en-US": {
        "CustomWidget.Workshop.Upsell.Button": "Configure model service",
        "CustomWidget.Workshop.Upsell.Message": "Want it faster? Configure a model service and generate in one click next time.",
    },
    "ja-JP": {"CustomWidget.Workshop.Upsell.Button": "モデルサービスを設定"},
    "ko-KR": {"CustomWidget.Workshop.Upsell.Button": "모델 서비스 설정"},
    "de-DE": {"CustomWidget.Workshop.Upsell.Button": "Modelldienst konfigurieren"},
    "fr-FR": {"CustomWidget.Workshop.Upsell.Button": "Configurer le service de modèles"},
    "es-ES": {"CustomWidget.Workshop.Upsell.Button": "Configurar el servicio de modelos"},
    "ru-RU": {"CustomWidget.Workshop.Upsell.Button": "Настроить службу моделей"},
    "pt-BR": {"CustomWidget.Workshop.Upsell.Button": "Configurar serviço de modelos"},
    "hi-IN": {"CustomWidget.Workshop.Upsell.Button": "मॉडल सेवा कॉन्फ़िगर करें"},
    "bn-BD": {"CustomWidget.Workshop.Upsell.Button": "মডেল পরিষেবা কনফিগার করুন"},
    "ar-SA": {"CustomWidget.Workshop.Upsell.Button": "إعداد خدمة النماذج"},
}

# P2：工具说明必须与「默认暂存、确认后提交」的新行为一致
UPSERT_COPY = {
    "zh-CN": "校验并暂存自定义格子（默认不改动桌面）；经用户确认后提交到桌面并保留数据",
    "zh-TW": "校驗並暫存自訂格子（預設不變更桌面）；經確認後提交到桌面並保留資料",
    "en-US": "Validate and stage a Custom Widget (desktop unchanged by default); commit after user confirmation while preserving data",
    "ja-JP": "カスタム格子を検証してステージングします（既定ではデスクトップを変更しません）。確認後にコミットし、データを保持します",
    "ko-KR": "사용자 지정 격자를 검증하고 스테이징합니다(기본: 바탕 화면 변경 없음). 확인 후 커밋하며 데이터를 보존합니다",
    "de-DE": "Custom Widget prüfen und bereitstellen (Desktop standardmäßig unverändert); nach Bestätigung übernehmen und Daten behalten",
    "fr-FR": "Valider et mettre en attente un widget personnalisé (bureau inchangé par défaut) ; valider après confirmation en conservant les données",
    "es-ES": "Validar y preparar un widget personalizado (escritorio sin cambios por defecto); confirmar tras aprobación conservando los datos",
    "ru-RU": "Проверить и подготовить виджет (рабочий стол по умолчанию не меняется); после подтверждения зафиксировать, сохранив данные",
    "pt-BR": "Validar e preparar um widget personalizado (área de trabalho inalterada por padrão); confirmar após aprovação preservando os dados",
    "hi-IN": "कस्टम विजेट को जाँचें और स्टेज करें (डेस्कटॉप अपरिवर्तित); पुष्टि के बाद कमिट करें",
    "bn-BD": "কাস্টম উইজেট যাচাই ও স্টেজ করুন (ডেস্কটপ অপরিবর্তিত); নিশ্চিত হলে কমিট করুন",
    "ar-SA": "التحقق من الأداة المخصصة وإعدادها مؤقتًا (سطح المكتب دون تغيير افتراضيًا)؛ التأكيد بعد موافقة المستخدم مع الحفاظ على البيانات",
}

# 首次选择弹窗：把实现层命名改为用户语言（P1 渐进披露的第一步）
USER_FRIENDLY = {
    "zh-CN": {
        "CustomWidget.Workshop.Onboarding.ModeDirectTitle": "我的模型服务",
        "CustomWidget.Workshop.Onboarding.ModeAgentTitle": "本地编程工具",
        "CustomWidget.Workshop.Onboarding.ModeWebTitle": "免费网页 AI",
    },
    "zh-TW": {
        "CustomWidget.Workshop.Onboarding.ModeDirectTitle": "我的模型服務",
        "CustomWidget.Workshop.Onboarding.ModeAgentTitle": "本機程式設計工具",
        "CustomWidget.Workshop.Onboarding.ModeWebTitle": "免費網頁 AI",
    },
    "en-US": {
        "CustomWidget.Workshop.Onboarding.ModeDirectTitle": "My model service",
        "CustomWidget.Workshop.Onboarding.ModeAgentTitle": "Local coding tool",
        "CustomWidget.Workshop.Onboarding.ModeWebTitle": "Free web AI",
    },
    "ja-JP": {"CustomWidget.Workshop.Onboarding.ModeDirectTitle": "自分のモデルサービス", "CustomWidget.Workshop.Onboarding.ModeAgentTitle": "ローカル開発ツール", "CustomWidget.Workshop.Onboarding.ModeWebTitle": "無料の Web AI"},
    "ko-KR": {"CustomWidget.Workshop.Onboarding.ModeDirectTitle": "내 모델 서비스", "CustomWidget.Workshop.Onboarding.ModeAgentTitle": "로컬 개발 도구", "CustomWidget.Workshop.Onboarding.ModeWebTitle": "무료 웹 AI"},
    "de-DE": {"CustomWidget.Workshop.Onboarding.ModeDirectTitle": "Mein Modelldienst", "CustomWidget.Workshop.Onboarding.ModeAgentTitle": "Lokales Entwicklerwerkzeug", "CustomWidget.Workshop.Onboarding.ModeWebTitle": "Kostenlose Web-KI"},
    "fr-FR": {"CustomWidget.Workshop.Onboarding.ModeDirectTitle": "Mon service de modèles", "CustomWidget.Workshop.Onboarding.ModeAgentTitle": "Outil de développement local", "CustomWidget.Workshop.Onboarding.ModeWebTitle": "IA web gratuite"},
    "es-ES": {"CustomWidget.Workshop.Onboarding.ModeDirectTitle": "Mi servicio de modelos", "CustomWidget.Workshop.Onboarding.ModeAgentTitle": "Herramienta de desarrollo local", "CustomWidget.Workshop.Onboarding.ModeWebTitle": "IA web gratuita"},
    "ru-RU": {"CustomWidget.Workshop.Onboarding.ModeDirectTitle": "Моя служба моделей", "CustomWidget.Workshop.Onboarding.ModeAgentTitle": "Локальный инструмент разработчика", "CustomWidget.Workshop.Onboarding.ModeWebTitle": "Бесплатный веб-ИИ"},
    "pt-BR": {"CustomWidget.Workshop.Onboarding.ModeDirectTitle": "Meu serviço de modelos", "CustomWidget.Workshop.Onboarding.ModeAgentTitle": "Ferramenta de desenvolvimento local", "CustomWidget.Workshop.Onboarding.ModeWebTitle": "IA web gratuita"},
    "hi-IN": {"CustomWidget.Workshop.Onboarding.ModeDirectTitle": "मेरा मॉडल सेवा", "CustomWidget.Workshop.Onboarding.ModeAgentTitle": "स्थानीय कोडिंग टूल", "CustomWidget.Workshop.Onboarding.ModeWebTitle": "मुफ़्त वेब AI"},
    "bn-BD": {"CustomWidget.Workshop.Onboarding.ModeDirectTitle": "আমার মডেল পরিষেবা", "CustomWidget.Workshop.Onboarding.ModeAgentTitle": "স্থানীয় কোডিং টুল", "CustomWidget.Workshop.Onboarding.ModeWebTitle": "বিনামূল্যে ওয়েব AI"},
    "ar-SA": {"CustomWidget.Workshop.Onboarding.ModeDirectTitle": "خدمة النماذج الخاصة بي", "CustomWidget.Workshop.Onboarding.ModeAgentTitle": "أداة برمجة محلية", "CustomWidget.Workshop.Onboarding.ModeWebTitle": "ذكاء اصطناعي عبر الويب مجانًا"},
}

KEY_LINE = re.compile(r'^\s*"([^"]+)"\s*:')
CHECK_PREFIXES = ("CustomWidget.Workshop.Onboarding.", "CustomWidget.Workshop.Diag.", "CustomWidget.Workshop.Upsell.")
BRAND_ONLY_KEYS = frozenset({
    "CustomWidget.Workshop.Onboarding.ModeDirectDesc",
    "CustomWidget.Workshop.Onboarding.ModeAgentDesc",
    "CustomWidget.Workshop.Onboarding.ModeWebDesc",
})


def lang_of(path: pathlib.Path) -> str:
    return path.stem


def load(path: pathlib.Path) -> dict:
    with path.open("r", encoding="utf-8-sig") as f:
        return json.load(f)


def cleanup_file(path: pathlib.Path) -> tuple[int, int, int]:
    lang = lang_of(path)
    fixes = dict(VALUE_FIXES.get(lang, {}))
    fixes.update(USER_FRIENDLY.get(lang, {}))
    if lang in UPSERT_COPY:
        fixes["CustomWidget.AgentHub.Tool.Upsert"] = UPSERT_COPY[lang]
    with path.open("r", encoding="utf-8", newline="") as f:
        lines = f.readlines()

    existing_keys = {match.group(1) for line in lines if (match := KEY_LINE.match(line))}
    out: list[str] = []
    removed = renamed = changed = inserted = 0
    inserted_extra = False
    for line in lines:
        m = KEY_LINE.match(line)
        key = m.group(1) if m else None
        if key:
            if key.startswith(DEAD_PREFIXES) and key not in KEEP_KEYS:
                removed += 1
                continue
            new_key = RENAMES.get(key, key)
            if new_key != key:
                line = line.replace(f'"{key}"', f'"{new_key}"', 1)
                renamed += 1
                key = new_key
            if key in fixes:
                eol = "\r\n" if line.endswith("\r\n") else "\n"
                comma = "," if line.rstrip("\r\n").rstrip().endswith(",") else ""
                line = f'  "{key}": {json.dumps(fixes[key], ensure_ascii=False)}{comma}{eol}'
                changed += 1
        out.append(line)
        if key == INSERT_AFTER and not inserted_extra:
            eol = "\r\n" if line.endswith("\r\n") else "\n"
            for extra_key, extra_value in EXTRA_COPY.get(lang, {}).items():
                if extra_key in existing_keys:
                    continue
                out.append(f'  "{extra_key}": {json.dumps(extra_value, ensure_ascii=False)},{eol}')
                inserted += 1
            inserted_extra = True

    with path.open("w", encoding="utf-8", newline="") as f:
        f.writelines(out)
    return removed, renamed + changed + inserted, removed


def check(allow_fallback: int = 0) -> int:
    files = sorted(STRINGS.glob("*.json"))
    catalogs = {lang_of(p): load(p) for p in files}
    baseline = set(catalogs.get("zh-CN", {}))
    problems: list[str] = []
    for lang, data in catalogs.items():
        missing = baseline - set(data)
        extra = set(data) - baseline
        if missing or extra:
            problems.append(f"{lang}: 缺 {len(missing)} / 多 {len(extra)} 个 key")
        for k in DEAD_PREFIXES:
            stale = [x for x in data if x.startswith(k) and x not in KEEP_KEYS]
            if stale:
                problems.append(f"{lang}: 仍残留 {len(stale)} 条旧引导词条")
        for k in RENAMES:
            if k in data:
                problems.append(f"{lang}: 仍残留旧 key {k}")
        for k in STAGE_KEYS:
            if not data.get(k):
                problems.append(f"{lang}: 缺少 P2 暂存提示条文案 {k}")
        for k in FAILURE_KEYS:
            if not data.get(k):
                problems.append(f"{lang}: 缺少失败提示文案 {k}")
        if "{0}" not in data.get("CustomWidget.Workshop.AgentStage.Notice", "{0}"):
            problems.append(f"{lang}: AgentStage.Notice 缺少批次数量占位符 {{0}}")

    en = catalogs.get("en-US", {})
    fallback = 0
    total = 0
    for lang, data in catalogs.items():
        if lang in ("en-US", "zh-CN", "zh-TW"):
            continue
        for k, v in en.items():
            if k in BRAND_ONLY_KEYS:
                continue
            if k.startswith(CHECK_PREFIXES):
                total += 1
                if data.get(k) == v:
                    fallback += 1
    print(f"语言文件: {len(catalogs)}；key 基线: {len(baseline)}")
    print(f"重点前缀非中英语种英文回退: {fallback}/{total} ({fallback / total * 100:.1f}%)" if total else "无检查项")
    if fallback > allow_fallback:
        problems.append(
            f"重点前缀英文回退 {fallback}/{total} 超过上限 {allow_fallback}："
            "新增面向用户的 key 时必须同时补齐 10 个非中英语种；仅紧急情况可用 --allow-fallback 放宽"
        )
    for p in problems:
        print("  [FAIL]", p)
    if not problems:
        print("  [OK] key 集合一致、旧词条与旧 key 已清理")
    return 1 if problems else 0


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    if hasattr(sys.stderr, "reconfigure"):
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply-cleanup", action="store_true", help="执行 P0 资源清理（幂等）")
    ap.add_argument("--allow-fallback", type=int, default=0,
                    help="允许的重点前缀英文回退条数（默认 0；紧急放行时显式指定）")
    args = ap.parse_args()
    if args.apply_cleanup:
        for p in sorted(STRINGS.glob("*.json")):
            removed, changed, _ = cleanup_file(p)
            print(f"{p.name}: 删除 {removed} 条死键，改名/修正/新增 {changed} 处")
    return check(allow_fallback=args.allow_fallback)


if __name__ == "__main__":
    sys.exit(main())
