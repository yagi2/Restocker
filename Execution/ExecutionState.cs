namespace Restocker.Execution;

public enum ExecutionMode
{
    /// <summary>各リテイナーを順に呼び出して SellList を一瞬開き、スナップショットを取り直すだけ。</summary>
    RefreshAll,

    /// <summary>各リテイナーで PlannedAction (Reprice / NewListing) を実行する。</summary>
    ApplyActions,
}

public enum ExecutionState
{
    Idle,

    /// <summary>RetainerList addon の確認。次のジョブのリテイナーを選ぶ。</summary>
    SelectingRetainer,

    /// <summary>RetainerList をクリック後、SelectString が出るのを待つ。</summary>
    AwaitingSelectString,

    /// <summary>SelectString で「マーケットに出品を任せる」を選ぶ。</summary>
    OpeningSellList,

    /// <summary>RetainerSellList が開くのを待つ。</summary>
    AwaitingSellList,

    /// <summary>このジョブのアクションを 1 件処理（または Refresh モードならスナップショット待ちだけ）。</summary>
    PerformingAction,

    /// <summary>新規出品: ContextMenu が開くのを待つ。</summary>
    AwaitingContextMenu,

    /// <summary>新規出品: ContextMenu の「マーケットに出品する」を選ぶ。</summary>
    ClickingPutUpForSale,

    /// <summary>Refresh モード: 出品中の各 listing を順に開いて AskingPrice を読む。</summary>
    ReadingPrices,

    /// <summary>Refresh モード: 価格読み取り用に開いた RetainerSell ダイアログが開くのを待つ。</summary>
    AwaitingSellDialogForReading,

    /// <summary>Refresh モード: 価格読み取り後、SellList に戻るのを待つ。</summary>
    AwaitingSellListAfterReading,

    /// <summary>RetainerSell ダイアログが開くのを待つ。</summary>
    AwaitingSellDialog,

    /// <summary>RetainerSell ダイアログを Confirm して閉じる。</summary>
    ConfirmingSellDialog,

    /// <summary>サドルバッグ → キャラバッグへの staging が完了するのを待つ。</summary>
    AwaitingSaddleMove,

    /// <summary>SellList を開いた直後、saddle source の必要量をまとめて bag に pre-staging する。</summary>
    PreStagingSaddle,

    /// <summary>MoveToRetainerMarket 直後、対象 market slot に該当アイテムが server 反映されるのを待つ。</summary>
    AwaitingNewListing,

    /// <summary>FetchMarketPrices: listing slot を click して RetainerSell が出るのを待つ。</summary>
    FetchAwaitingSellDialog,

    /// <summary>FetchMarketPrices: ComparePrices を click 後、ItemSearchResult のキャッシュ更新を待つ。</summary>
    FetchAwaitingMarketData,

    /// <summary>FetchMarketPrices: ItemSearchResult / RetainerSell を閉じて SellList に戻るのを待つ。</summary>
    FetchAwaitingSellListAfter,

    /// <summary>RetainerSellList を close 操作で閉じる。</summary>
    ClosingSellList,

    /// <summary>SelectString に戻ったのを待つ。</summary>
    AwaitingSelectStringAfterSell,

    /// <summary>SelectString で「終了する」を選ぶ。</summary>
    DismissingRetainer,

    /// <summary>RetainerList に戻ったのを待つ → 次のジョブへ。</summary>
    AwaitingDismissed,

    Done,
    Stopped,
}
