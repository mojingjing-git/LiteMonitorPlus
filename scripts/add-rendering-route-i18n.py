# -*- coding: utf-8 -*-
# 【步3 / R3 §4.4】向 9 个语言文件的 Menu 段插入「渲染路线」页所需 key（文本级插入，不动既有行）
import json, re, io, os

LANG_DIR = r"C:\Users\wrz20\.codex\worktrees\review-remediation-20261023\LiteMonitor-fork\resources\lang"

T = {
  "zh": {
    "nav": "渲染路线",
    "legacy": "当前主题走 GDI+ 旧路线（未声明 GPU 材质）。",
    "running": "GPU 材质运行中。",
    "liveOn": "实时桌面背景运行中（系统截图/录屏会排除整个窗口）。",
    "liveOff": "GPU 运行中，使用固定背景（实时背景未开启）。",
    "liveUnavail": "实时背景已开启但暂不可用",
    "notStarted": "GPU 呈现未运行",
    "layered": "当前窗口为分层窗口（透明度<100%）；2026-10-24 实测不影响实时背景。",
    "sufGpu": "（GPU 材质）",
    "sufGdi": "（GDI+）",
  },
  "zh-tw": {
    "nav": "渲染路線",
    "legacy": "目前主題走 GDI+ 舊路線（未宣告 GPU 材質）。",
    "running": "GPU 材質執行中。",
    "liveOn": "即時桌面背景執行中（系統截圖/錄影會排除整個視窗）。",
    "liveOff": "GPU 執行中，使用固定背景（即時背景未開啟）。",
    "liveUnavail": "即時背景已開啟但暫不可用",
    "notStarted": "GPU 呈現未執行",
    "layered": "目前視窗為分層視窗（透明度<100%）；2026-10-24 實測不影響即時背景。",
    "sufGpu": "（GPU 材質）",
    "sufGdi": "（GDI+）",
  },
  "en": {
    "nav": "Rendering route",
    "legacy": "This theme renders via the GDI+ legacy route (no GPU material declared).",
    "running": "GPU material is running.",
    "liveOn": "Live desktop background is active; supported screenshots/recordings exclude the whole window.",
    "liveOff": "GPU is running with a fixed background (live background is off).",
    "liveUnavail": "Live background is on but currently unavailable",
    "notStarted": "GPU presentation is not running",
    "layered": "Window is layered (opacity<100%); verified 2026-10-24 that this does not block the live background.",
    "sufGpu": " (GPU material)",
    "sufGdi": " (GDI+)",
  },
  "ja": {
    "nav": "描画ルート",
    "legacy": "このテーマは GDI+ 旧ルートで描画されます（GPU マテリアル未宣言）。",
    "running": "GPU マテリアル実行中。",
    "liveOn": "ライブデスクトップ背景が実行中（対応するスクリーンショット/録画からはウィンドウ全体が除外されます）。",
    "liveOff": "GPU 実行中、固定背景を使用中（ライブ背景はオフ）。",
    "liveUnavail": "ライブ背景はオンですが現在利用できません",
    "notStarted": "GPU 描画が実行されていません",
    "layered": "ウィンドウはレイヤード表示（不透明度<100%）です。2026-10-24 の実測ではライブ背景に影響しません。",
    "sufGpu": "（GPUマテリアル）",
    "sufGdi": "（GDI+）",
  },
  "ko": {
    "nav": "렌더링 경로",
    "legacy": "이 테마는 GDI+ 레거시 경로로 렌더링됩니다(GPU 재질 미선언).",
    "running": "GPU 재질 실행 중.",
    "liveOn": "실시간 데스크톱 배경 실행 중(지원되는 스크린샷/녹화에서 창 전체가 제외됨).",
    "liveOff": "GPU 실행 중, 고정 배경 사용(실시간 배경 꺼짐).",
    "liveUnavail": "실시간 배경이 켜져 있지만 현재 사용 불가",
    "notStarted": "GPU 프레젠테이션이 실행되지 않음",
    "layered": "창이 계층화되어 있습니다(불투명도<100%). 2026-10-24 실측에서 실시간 배경에 영향 없음 확인.",
    "sufGpu": " (GPU 재질)",
    "sufGdi": " (GDI+)",
  },
  "de": {
    "nav": "Render-Route",
    "legacy": "Dieses Theme rendert über die GDI+-Legacy-Route (kein GPU-Material deklariert).",
    "running": "GPU-Material läuft.",
    "liveOn": "Live-Desktop-Hintergrund aktiv; unterstützte Screenshots/Aufnahmen schließen das ganze Fenster aus.",
    "liveOff": "GPU läuft mit festem Hintergrund (Live-Hintergrund aus).",
    "liveUnavail": "Live-Hintergrund ist eingeschaltet, aber derzeit nicht verfügbar",
    "notStarted": "GPU-Darstellung läuft nicht",
    "layered": "Fenster ist geschichtet (Deckkraft<100%); am 24.10.2026 verifiziert: blockiert den Live-Hintergrund nicht.",
    "sufGpu": " (GPU-Material)",
    "sufGdi": " (GDI+)",
  },
  "es": {
    "nav": "Ruta de renderizado",
    "legacy": "Este tema renderiza por la ruta GDI+ heredada (sin material GPU declarado).",
    "running": "Material GPU en ejecución.",
    "liveOn": "Fondo de escritorio en vivo activo; las capturas/grabaciones compatibles excluyen toda la ventana.",
    "liveOff": "GPU en ejecución con fondo fijo (fondo en vivo desactivado).",
    "liveUnavail": "Fondo en vivo activado pero no disponible actualmente",
    "notStarted": "La presentación GPU no está en ejecución",
    "layered": "La ventana está en capas (opacidad<100%); verificado el 24-10-2026 que no bloquea el fondo en vivo.",
    "sufGpu": " (material GPU)",
    "sufGdi": " (GDI+)",
  },
  "fr": {
    "nav": "Route de rendu",
    "legacy": "Ce thème effectue le rendu via la route GDI+ héritée (aucun matériau GPU déclaré).",
    "running": "Matériau GPU en cours d'exécution.",
    "liveOn": "Arrière-plan de bureau en direct actif ; les captures/enregistrements pris en charge excluent toute la fenêtre.",
    "liveOff": "GPU en cours avec arrière-plan fixe (arrière-plan en direct désactivé).",
    "liveUnavail": "Arrière-plan en direct activé mais actuellement indisponible",
    "notStarted": "Le rendu GPU n'est pas en cours",
    "layered": "La fenêtre est superposée (opacité<100 %) ; vérifié le 24-10-2026 : ne bloque pas l'arrière-plan en direct.",
    "sufGpu": " (matériau GPU)",
    "sufGdi": " (GDI+)",
  },
  "ru": {
    "nav": "Маршрут рендеринга",
    "legacy": "Эта тема рендерится через унаследованный маршрут GDI+ (GPU-материал не объявлен).",
    "running": "GPU-материал работает.",
    "liveOn": "Живой фон рабочего стола активен; поддерживаемые скриншоты/записи исключают всё окно.",
    "liveOff": "GPU работает с фиксированным фоном (живой фон выключен).",
    "liveUnavail": "Живой фон включён, но сейчас недоступен",
    "notStarted": "GPU-рендеринг не запущен",
    "layered": "Окно многослойное (непрозрачность<100%); проверено 24.10.2026 — живому фону не мешает.",
    "sufGpu": " (GPU-материал)",
    "sufGdi": " (GDI+)",
  },
}

def jstr(s):  # JSON string literal with minimal escaping
    return json.dumps(s, ensure_ascii=False)

def build_block(lang, child_indent):
    t = T[lang]
    i2 = child_indent
    lines = [
        f'{i2}"RenderingRoute": {jstr(t["nav"])},',
        f'{i2}"RouteSuffixGpu": {jstr(t["sufGpu"])},',
        f'{i2}"RouteSuffixGdi": {jstr(t["sufGdi"])},',
        f'{i2}"RouteStatus": {{',
        f'{i2}  "LegacyTheme": {jstr(t["legacy"])},',
        f'{i2}  "GpuRunning": {jstr(t["running"])},',
        f'{i2}  "LiveBackgroundOn": {jstr(t["liveOn"])},',
        f'{i2}  "LiveBackgroundOff": {jstr(t["liveOff"])},',
        f'{i2}  "LiveBackgroundUnavailable": {jstr(t["liveUnavail"])},',
        f'{i2}  "NotStarted": {jstr(t["notStarted"])},',
        f'{i2}  "LayeredNote": {jstr(t["layered"])}',
        f'{i2}}},',
    ]
    return lines

ok = True
for lang in T:
    path = os.path.join(LANG_DIR, lang + ".json")
    if not os.path.exists(path):
        print("MISSING FILE:", path); ok = False; continue
    with io.open(path, "r", encoding="utf-8") as f:
        text = f.read()
    if '"RenderingRoute"' in text:
        print("SKIP (already inserted):", lang); continue
    m = re.search(r'^([ \t]*)"Menu"\s*:\s*\{\s*$', text, re.M)
    if not m:
        # 可能是 "Menu": { ... } 单行风格，退化为宽松匹配
        m = re.search(r'^([ \t]*)"Menu"\s*:\s*\{.*$', text, re.M)
        if not m:
            print("NO Menu anchor:", lang); ok = False; continue
    menu_line = m.group(0)
    # 子级缩进 = Menu 行缩进 + 2 空格（与仓库文件风格一致）
    child_indent = m.group(1) + "  "
    insert_at = m.end()
    block = "\n" + "\n".join(build_block(lang, child_indent))
    new_text = text[:insert_at] + block + text[insert_at:]
    # 校验：仍是合法 JSON 且 key 可解析
    try:
        d = json.loads(new_text)
        menu = d["Menu"]
        assert menu["RenderingRoute"] == T[lang]["nav"]
        assert menu["RouteStatus"]["LayeredNote"] == T[lang]["layered"]
        assert menu["RouteSuffixGpu"] == T[lang]["sufGpu"]
    except Exception as e:
        print("VALIDATION FAILED:", lang, e); ok = False; continue
    with io.open(path, "w", encoding="utf-8", newline="") as f:
        f.write(new_text)
    print("OK:", lang)

print("ALL OK" if ok else "HAS FAILURES")
