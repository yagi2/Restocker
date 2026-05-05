namespace Restocker.Localization;

public enum Language
{
    English = 0,
    Japanese = 1,
    German = 2,
    French = 3,
    Chinese = 4,
    Korean = 5
}

/// <summary>
/// 全 UI 文言の単一ソース。EN/JA/DE/FR/ZH/KO の 6 言語に必ず対応する。
/// 新規文字列を足す時は <see cref="T"/> ヘルパーを使い、6 言語ぶんを 1 行で書くこと。
/// </summary>
public static class Strings
{
    private static Language lang = Language.English;
    public static void SetLanguage(Language l) => lang = l;
    public static Language Current => lang;

    private static string T(string en, string ja, string de, string fr, string zh, string ko) => lang switch
    {
        Language.Japanese => ja,
        Language.German => de,
        Language.French => fr,
        Language.Chinese => zh,
        Language.Korean => ko,
        _ => en
    };

    public static string LanguageNameEnglish => "English";
    public static string LanguageNameJapanese => "日本語";
    public static string LanguageNameGerman => "Deutsch";
    public static string LanguageNameFrench => "Français";
    public static string LanguageNameChinese => "简体中文";
    public static string LanguageNameKorean => "한국어";

    public static string WindowTitle => "Restocker";

    // Tabs
    public static string TabReprice => T("Update prices", "価格更新", "Preise anpassen", "Mettre à jour les prix", "更新价格", "가격 갱신");
    public static string TabList    => T("List items", "新規出品", "Einstellen", "Mettre en vente", "上架", "등록");
    public static string TabSettings => T("Settings", "設定", "Einstellungen", "Paramètres", "设置", "설정");

    // Header
    public static string HeaderCachedRetainers => T("Cached:", "キャッシュ:", "Zwischenspeicher:", "Cache :", "缓存:", "캐시:");
    public static string HeaderRetainers => T("retainer(s)", "リテイナー", "Gehilfe(n)", "intendant(s)", "雇员", "리테이너");
    public static string HeaderLastUpdated => T("last update", "最終更新", "letzte Aktualisierung", "dernière mise à jour", "最后更新", "최종 갱신");
    public static string HeaderUnknown => T("never", "未取得", "nie", "jamais", "未获取", "미취득");
    public static string HeaderRefreshAll => T("Refresh all retainers", "全リテイナー更新", "Alle Gehilfen aktualisieren", "Actualiser tous les intendants", "刷新全部雇员", "전체 리테이너 새로고침");
    public static string HeaderRefreshing => T("Refreshing… {0}/{1}", "更新中… {0}/{1}", "Aktualisiere… {0}/{1}", "Actualisation… {0}/{1}", "刷新中… {0}/{1}", "새로고침 중… {0}/{1}");
    public static string HeaderStop => T("Stop", "中断", "Stopp", "Arrêter", "中断", "중단");
    public static string ToastBellNotOpen => T(
        "Open the retainer bell first.",
        "先にリテイナーベルを開いてください。",
        "Öffne zuerst die Gehilfenglocke.",
        "Ouvre d'abord la cloche des intendants.",
        "请先打开雇员铃。",
        "먼저 리테이너 벨을 여세요.");
    public static string ToastNoRetainersInBell => T(
        "No retainers detected in the bell list.",
        "ベルリストにリテイナーが見つかりません。",
        "Keine Gehilfen in der Glockenliste.",
        "Aucun intendant détecté.",
        "未在铃列表中检测到雇员。",
        "벨 목록에서 리테이너가 감지되지 않았습니다.");

    // Common
    public static string Apply => T("Apply", "適用", "Anwenden", "Appliquer", "应用", "적용");
    public static string Cancel => T("Cancel", "キャンセル", "Abbrechen", "Annuler", "取消", "취소");
    public static string NotImplemented => T("(not implemented)", "(未実装)", "(noch nicht)", "(à venir)", "（暂未）", "(미구현)");
    public static string Filter => T("Filter by item name", "アイテム名で絞り込み", "Nach Artikel filtern", "Filtrer par nom d'article", "按物品名筛选", "아이템 이름 필터");
    public static string HQNQNote => T("HQ/NQ are listed as separate rows", "HQ/NQ は別行扱い", "HQ/NQ als separate Zeilen", "HQ/NQ traités séparément", "HQ/NQ 视为独立行", "HQ/NQ는 별도 행");
    public static string NoTransfer => T("no items moved between retainers", "リテイナー間移動なし", "kein Transfer zwischen Gehilfen", "aucun transfert entre intendants", "雇员之间不转移", "리테이너 간 이동 없음");
    public static string EmptyHint => T(
        "Open the bell, summon a retainer and open the sell list — items will populate here.",
        "リテイナー召喚→マーケット出品リストを開けば、ここに集約表示されます。",
        "Glocke öffnen, Gehilfen rufen und die Verkaufsliste öffnen — die Artikel erscheinen hier.",
        "Ouvre la cloche, invoque un intendant et ouvre la liste de vente — les objets apparaîtront ici.",
        "打开雇员铃→召唤雇员→打开寄售列表，物品将出现在此处。",
        "벨을 열고 리테이너를 소환해 판매 목록을 열면 여기에 표시됩니다.");

    // Update prices tab
    public static string RepriceMatchLowest => T(
        "-1 gil to all rows",
        "全行に最安値 -1ギル",
        "-1 Gil auf alle Zeilen",
        "-1 gil à toutes les lignes",
        "全部行 -1 金币",
        "전체 행에 -1길");
    public static string RepriceMatchLowestThisRetainer => T(
        "-1 gil (this retainer)",
        "このリテイナーに -1ギル",
        "-1 Gil (nur dieser Gehilfe)",
        "-1 gil (cet intendant)",
        "本雇员 -1 金币",
        "이 리테이너에 -1길");
    public static string ApplyWithCount => T("{0} ({1})", "{0} ({1}件)", "{0} ({1})", "{0} ({1})", "{0} ({1})", "{0} ({1})");
    public static string MatchAppliedSummary => T(
        "Applied -1 gil pricing to {0}/{1} rows.",
        "{1} 行のうち {0} 行に -1ギル価格を適用しました。",
        "Auf {0}/{1} Zeilen -1 Gil angewandt.",
        "-1 gil appliqué à {0}/{1} lignes.",
        "已对 {1} 行中的 {0} 行应用 -1 金币价格。",
        "{1}행 중 {0}행에 -1길 가격을 적용했습니다.");
    public static string MatchAppliedSummaryWithMissing => T(
        "Applied to {0}/{1} rows. {2} item(s) have no market data — open the market for: {3}",
        "{1} 行中 {0} 行に適用。{2} 種類はマーケットデータ未取得 — 該当アイテムをマーケットで開いてください: {3}",
        "{0}/{1} Zeilen angewandt. {2} ohne Marktdaten: {3}",
        "{0}/{1} appliquées. {2} sans données de marché : {3}",
        "已应用 {0}/{1} 行。{2} 种缺少市场数据：{3}",
        "{1}행 중 {0}행 적용. {2}종 시장 데이터 없음: {3}");
    public static string ApplyProgress => T(
        "Applying… action {0}/{1}, retainer {2}/{3}",
        "適用中… アクション {0}/{1}、リテイナー {2}/{3}",
        "Anwenden… Aktion {0}/{1}, Gehilfe {2}/{3}",
        "Application… action {0}/{1}, intendant {2}/{3}",
        "应用中… 操作 {0}/{1}, 雇员 {2}/{3}",
        "적용 중… 작업 {0}/{1}, 리테이너 {2}/{3}");
    public static string RetainerHeader => T("{0}  ({1} listings, {2})", "{0}  ({1}件 / {2})", "{0}  ({1} Posten, {2})", "{0}  ({1} mises, {2})", "{0}  ({1} 项, {2})", "{0}  ({1}건, {2})");
    public static string RetainerHeaderInventory => T("{0}  ({1} items, {2})", "{0}  ({1}品 / {2})", "{0}  ({1} Artikel, {2})", "{0}  ({1} articles, {2})", "{0}  ({1} 物品, {2})", "{0}  ({1}품, {2})");
    public static string CharacterInventoryHeader => T("Character bag (entrust required)", "キャラ所持品（要 預け入れ）", "Charakter-Tasche (Übergabe nötig)", "Sac du personnage (transfert requis)", "角色背包（需先寄存）", "캐릭터 가방 (위탁 필요)");
    public static string SaddlebagHeader => T("Saddlebag", "サドルバッグ", "Satteltasche", "Fonte", "马鞍袋", "새들백");
    public static string CharBagTargetLabel => T("Target retainer", "出品先リテイナー", "Ziel-Gehilfe", "Intendant cible", "上架雇员", "등록 대상 리테이너");
    public static string CharBagPickTarget => T("(pick a target retainer)", "（出品先を選択）", "(Ziel wählen)", "(choisis un intendant)", "（请选择雇员）", "(대상 선택)");
    public static string CharBagNeedRetainerSnapshot => T(
        "No retainer snapshot yet — refresh retainers first to enable target selection.",
        "リテイナーの snapshot がまだありません。先に「全リテイナー更新」してください。",
        "Erst Gehilfen aktualisieren, dann Ziel wählen.",
        "Aucun snapshot — actualise d'abord les intendants.",
        "尚无雇员快照，请先刷新。",
        "리테이너 스냅샷이 없습니다. 먼저 새로고침하세요.");
    public static string FreeSlots => T("free slot(s)", "枠空き", "Slot(s)", "places", "空格", "빈 슬롯");
    public static string CollapseAll => T("Collapse all", "全て折りたたむ", "Alle einklappen", "Tout replier", "全部折叠", "모두 접기");
    public static string ExpandAll => T("Expand all", "全て展開", "Alle ausklappen", "Tout déplier", "全部展开", "모두 펼치기");
    public static string ColExpand => T("+/-", "展開", "+/-", "+/-", "展开", "확장");
    public static string ColItem => T("Item", "アイテム", "Artikel", "Article", "物品", "아이템");
    public static string ColTotalQty => T("Qty", "合計数", "Anz.", "Qté", "数量", "수량");
    public static string ColListings => T("Listings", "出品本数", "Posten", "Mises", "上架数", "출품 수");
    public static string ColCurrentPrice => T("Current", "現在価格", "Aktuell", "Actuel", "当前价格", "현재가");
    public static string ColNewPrice => T("New", "新価格", "Neu", "Nouveau", "新价格", "신가격");
    public static string PriceUnknown => T("unknown", "未取得", "unbekannt", "inconnu", "未获取", "미취득");
    public static string EditedSummary => T("Editing: by item {0} / individual {1}", "編集中: アイテム単位 {0} / 個別 {1}", "Bearbeitung: pro Artikel {0} / einzeln {1}", "Édition : par article {0} / individuel {1}", "编辑中：按物品 {0} / 个别 {1}", "편집 중: 아이템 {0} / 개별 {1}");

    // List tab
    public static string ColOwned => T("Owned", "所持", "Bestand", "Stock", "拥有", "보유");
    public static string ColListed => T("Listed", "出品中", "Eingestellt", "En vente", "上架中", "출품 중");
    public static string ColMaxStack => T("Max/listing", "最大/出品", "Max./Posten", "Max/mise", "最大/单挂", "최대/등록");
    public static string ColPrice => T("Price", "提示価格", "Preis", "Prix", "价格", "가격");
    public static string ColPlan => T("Plan", "計画", "Plan", "Plan", "计划", "계획");
    public static string ListableOnly => T("Listable only", "出品可能アイテムのみ", "Nur einstellbare", "Vendables uniquement", "仅可上架", "등록 가능만");
    public static string Unsellable => T(" (not listable)", "（出品不可）", " (nicht einstellbar)", " (non vendable)", "（不可上架）", "（등록 불가）");
    public static string PlanCount => T("{0} listings", "{0}件", "{0} Posten", "{0} mises", "{0} 项", "{0}건");
    public static string PlanOverflow => T("{0} qty cannot fit", "※{0}個 余り", "{0} überzählig", "{0} en surplus", "余 {0} 个", "{0}개 초과");
    public static string ListSummary => T("To list: {0} item type(s)", "出品予定: {0} アイテム種", "Einzustellen: {0} Artikelart(en)", "À mettre en vente : {0} type(s)", "待上架：{0} 种物品", "등록 예정: {0} 종");

    // Confirm dialog
    public static string ConfirmTitle => T(
        "About to apply {0} actions across {1} retainer(s).",
        "{0} 件のアクションを {1} リテイナーに適用しようとしています。",
        "Wende {0} Aktionen auf {1} Gehilfen an.",
        "Sur le point d'appliquer {0} actions à {1} intendant(s).",
        "即将对 {1} 名雇员执行 {0} 个操作。",
        "{1}명의 리테이너에 {0}개 작업을 적용합니다.");
    public static string ConfirmBreakdown => T(
        "Reprice: {0} / New listings: {1}",
        "リプライス: {0} 件 / 新規出品: {1} 件",
        "Preis ändern: {0} / Neu einstellen: {1}",
        "Reprix : {0} / Nouvelles mises : {1}",
        "重新定价：{0} / 新上架：{1}",
        "재가격: {0} / 신규 등록: {1}");
    public static string ConfirmEta => T(
        "Estimated time: ~{0}s (each retainer ~5s overhead, ~3s per action)",
        "推定時間: 約 {0}秒（リテイナーごと約5秒、アクションごと約3秒）",
        "Geschätzt: ~{0}s",
        "Durée estimée : ~{0}s",
        "预计耗时：约 {0} 秒",
        "예상 시간: 약 {0}초");
    public static string ConfirmStart => T("Start", "開始", "Starten", "Démarrer", "开始", "시작");

    // Settings tab
    public static string SettingsAutoOpenOnBell => T(
        "Auto-show window when retainer bell is open",
        "リテイナーベル展開時にウィンドウを自動表示",
        "Fenster bei geöffneter Glocke automatisch anzeigen",
        "Afficher automatiquement quand la cloche est ouverte",
        "雇员铃打开时自动显示窗口",
        "벨이 열렸을 때 창 자동 표시");
    public static string SettingsLanguage => T("Language", "言語", "Sprache", "Langue", "语言", "언어");
    public static string SettingsLanguageAuto => T("Follow client", "クライアントに従う", "Spiel-Sprache folgen", "Suivre le client", "跟随客户端", "클라이언트 따름");

    // Toasts / warnings
    public static string ToastAutoRetainerPresent => T(
        "AutoRetainer is also loaded. Both plugins drive retainer summon flow — coordinate manually to avoid conflicts.",
        "AutoRetainer も読み込まれています。両者ともリテイナー巡回を制御するため、衝突を避けるよう手動で調整してください。",
        "AutoRetainer ist ebenfalls geladen. Beide Plugins steuern den Gehilfen-Aufruf — manuell koordinieren.",
        "AutoRetainer est aussi chargé. Les deux plugins pilotent l'invocation des intendants — coordonne manuellement.",
        "AutoRetainer 也已加载。两个插件都会驱动雇员召唤流程，请手动协调以避免冲突。",
        "AutoRetainer도 로드되어 있습니다. 두 플러그인 모두 리테이너 호출 흐름을 다루므로 충돌을 피하도록 수동 조정하세요.");
}
