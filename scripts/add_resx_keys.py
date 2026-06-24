#!/usr/bin/env python3
"""Insert (and translate) resource keys across all Strings.*.resx files.

Idempotent: a key already present in a file is left untouched. New keys are
inserted just before the closing </root> tag, preserving the file's existing
content, encoding and newline style so diffs stay minimal.

Usage:
    python scripts/add_resx_keys.py            # apply
    python scripts/add_resx_keys.py --check     # report missing keys, exit 1 if any

Add new keys by extending TRANSLATIONS below: each key maps culture -> value.
"en" is the neutral/base culture used by Strings.resx.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path
from xml.sax.saxutils import escape

RESOURCES_DIR = (
    Path(__file__).resolve().parent.parent
    / "src"
    / "TunnelAgent.Avalonia"
    / "Resources"
)

# key -> { culture -> value }. {0}, {1} placeholders are preserved verbatim.
TRANSLATIONS: dict[str, dict[str, str]] = {
    "Toast_EngineError_PortInUse": {
        "en": "Port {0} is already in use by another process. Stop it and start {1} again.",
        "es": "El puerto {0} ya está en uso por otro proceso. Deténlo e inicia {1} de nuevo.",
        "de": "Port {0} wird bereits von einem anderen Prozess verwendet. Beenden Sie ihn und starten Sie {1} erneut.",
        "fr": "Le port {0} est déjà utilisé par un autre processus. Arrêtez-le et relancez {1}.",
        "it": "La porta {0} è già in uso da un altro processo. Interrompilo e riavvia {1}.",
        "pt": "A porta {0} já está a ser utilizada por outro processo. Pare-o e inicie o {1} novamente.",
        "ru": "Порт {0} уже используется другим процессом. Остановите его и снова запустите {1}.",
        "uk": "Порт {0} вже використовується іншим процесом. Зупиніть його та запустіть {1} знову.",
        "tr": "{0} bağlantı noktası başka bir işlem tarafından kullanılıyor. Onu durdurup {1} öğesini yeniden başlatın.",
        "ja": "ポート {0} は別のプロセスによって既に使用されています。それを停止してから {1} を再度起動してください。",
        "ko": "포트 {0}은(는) 다른 프로세스에서 이미 사용 중입니다. 해당 프로세스를 중지한 후 {1}을(를) 다시 시작하세요.",
        "zh": "端口 {0} 已被其他进程占用。请停止该进程后重新启动 {1}。",
        "hi": "पोर्ट {0} पहले से ही किसी अन्य प्रक्रिया द्वारा उपयोग में है। उसे रोकें और {1} को फिर से शुरू करें।",
        "ar": "المنفذ {0} مُستخدَم بالفعل بواسطة عملية أخرى. أوقفها ثم ابدأ تشغيل {1} مرة أخرى.",
    },
    "Toast_EngineError_Timeout": {
        "en": "{0} did not respond in time. Please try again.",
        "es": "{0} no respondió a tiempo. Inténtalo de nuevo.",
        "de": "{0} hat nicht rechtzeitig geantwortet. Bitte versuchen Sie es erneut.",
        "fr": "{0} n'a pas répondu à temps. Veuillez réessayer.",
        "it": "{0} non ha risposto in tempo. Riprova.",
        "pt": "O {0} não respondeu a tempo. Tente novamente.",
        "ru": "{0} не ответил вовремя. Повторите попытку.",
        "uk": "{0} не відповів вчасно. Спробуйте ще раз.",
        "tr": "{0} zamanında yanıt vermedi. Lütfen tekrar deneyin.",
        "ja": "{0} が時間内に応答しませんでした。もう一度お試しください。",
        "ko": "{0}이(가) 제때 응답하지 않았습니다. 다시 시도하세요.",
        "zh": "{0} 未及时响应。请重试。",
        "hi": "{0} ने समय पर प्रतिक्रिया नहीं दी। कृपया पुनः प्रयास करें।",
        "ar": "لم يستجب {0} في الوقت المناسب. يرجى المحاولة مرة أخرى.",
    },
    "Toast_EngineError_LaunchFailed": {
        "en": "Failed to launch {0}. Check the installation and try again.",
        "es": "No se pudo iniciar {0}. Comprueba la instalación e inténtalo de nuevo.",
        "de": "{0} konnte nicht gestartet werden. Überprüfen Sie die Installation und versuchen Sie es erneut.",
        "fr": "Impossible de lancer {0}. Vérifiez l'installation et réessayez.",
        "it": "Impossibile avviare {0}. Controlla l'installazione e riprova.",
        "pt": "Falha ao iniciar o {0}. Verifique a instalação e tente novamente.",
        "ru": "Не удалось запустить {0}. Проверьте установку и повторите попытку.",
        "uk": "Не вдалося запустити {0}. Перевірте встановлення та спробуйте ще раз.",
        "tr": "{0} başlatılamadı. Kurulumu kontrol edip tekrar deneyin.",
        "ja": "{0} を起動できませんでした。インストールを確認してもう一度お試しください。",
        "ko": "{0}을(를) 시작하지 못했습니다. 설치를 확인하고 다시 시도하세요.",
        "zh": "无法启动 {0}。请检查安装后重试。",
        "hi": "{0} शुरू करने में विफल। इंस्टॉलेशन जांचें और पुनः प्रयास करें।",
        "ar": "تعذّر تشغيل {0}. تحقق من التثبيت وحاول مرة أخرى.",
    },
    "Toast_EngineError_Crashed": {
        "en": "{0} stopped unexpectedly. Check the logs and try again.",
        "es": "{0} se detuvo inesperadamente. Revisa los registros e inténtalo de nuevo.",
        "de": "{0} wurde unerwartet beendet. Überprüfen Sie die Protokolle und versuchen Sie es erneut.",
        "fr": "{0} s'est arrêté de manière inattendue. Consultez les journaux et réessayez.",
        "it": "{0} si è arrestato in modo imprevisto. Controlla i log e riprova.",
        "pt": "O {0} parou inesperadamente. Verifique os registos e tente novamente.",
        "ru": "{0} неожиданно остановился. Проверьте журналы и повторите попытку.",
        "uk": "{0} несподівано зупинився. Перегляньте журнали та спробуйте ще раз.",
        "tr": "{0} beklenmedik şekilde durdu. Günlükleri kontrol edip tekrar deneyin.",
        "ja": "{0} が予期せず停止しました。ログを確認してもう一度お試しください。",
        "ko": "{0}이(가) 예기치 않게 중지되었습니다. 로그를 확인하고 다시 시도하세요.",
        "zh": "{0} 意外停止。请查看日志后重试。",
        "hi": "{0} अप्रत्याशित रूप से रुक गया। लॉग जांचें और पुनः प्रयास करें।",
        "ar": "توقف {0} بشكل غير متوقع. تحقق من السجلات وحاول مرة أخرى.",
    },
}


def culture_of(path: Path) -> str:
    """Strings.resx -> 'en'; Strings.es-ES.resx -> 'es'."""
    m = re.fullmatch(r"Strings(?:\.([a-z]{2})-[A-Z]{2})?", path.stem)
    if not m:
        return ""
    return m.group(1) or "en"


def data_block(key: str, value: str, indent: str, newline: str) -> str:
    body = escape(value)
    return (
        f'{indent}<data name="{key}" xml:space="preserve">{newline}'
        f"{indent}  <value>{body}</value>{newline}"
        f"{indent}</data>{newline}"
    )


def process(path: Path, apply: bool) -> list[str]:
    culture = culture_of(path)
    if not culture:
        return []
    text = path.read_text(encoding="utf-8")
    newline = "\r\n" if "\r\n" in text else "\n"
    missing: list[str] = []

    insertions = ""
    for key, by_culture in TRANSLATIONS.items():
        if re.search(rf'<data name="{re.escape(key)}"', text):
            continue
        value = by_culture.get(culture) or by_culture["en"]
        missing.append(key)
        insertions += data_block(key, value, "  ", newline)

    if not insertions:
        return []

    if apply:
        idx = text.rfind("</root>")
        if idx == -1:
            raise SystemExit(f"{path.name}: no </root> found")
        text = text[:idx] + insertions + text[idx:]
        path.write_text(text, encoding="utf-8", newline="")
    return missing


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument(
        "--check", action="store_true", help="report missing keys without writing"
    )
    args = ap.parse_args()

    files = sorted(RESOURCES_DIR.glob("Strings*.resx"))
    if not files:
        raise SystemExit(f"No resx files under {RESOURCES_DIR}")

    any_missing = False
    for path in files:
        missing = process(path, apply=not args.check)
        if missing:
            any_missing = True
            verb = "missing" if args.check else "added"
            print(f"{path.name}: {verb} {', '.join(missing)}")

    if not any_missing:
        print("All resx files already contain every key.")
    return 1 if (args.check and any_missing) else 0


if __name__ == "__main__":
    sys.exit(main())
