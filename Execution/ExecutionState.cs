namespace Restocker.Execution;

/// <summary>
/// Executor が辿る状態。Framework.Update でフレームごとに遷移を試みる。
/// </summary>
public enum ExecutionState
{
    Idle,

    /// <summary>RetainerList addon が出るのを待つ。</summary>
    AwaitingBell,

    /// <summary>RetainerList から対象リテイナーを選ぶ。</summary>
    SelectingRetainer,

    /// <summary>SelectString が出るのを待つ。</summary>
    AwaitingSelectString,

    /// <summary>SelectString で「マーケットに商品を出す」を選ぶ。</summary>
    OpeningSellList,

    /// <summary>RetainerSellList が開くのを待つ。</summary>
    AwaitingSellList,

    /// <summary>このリテイナー向けの reprice / new listing を 1 件処理する。</summary>
    PerformingAction,

    /// <summary>RetainerSellList を閉じて SelectString に戻る。</summary>
    ClosingSellList,

    /// <summary>SelectString で「終了」を選ぶ。</summary>
    DismissingRetainer,

    /// <summary>RetainerList に戻ったのを待ち、次のリテイナーへ進む。</summary>
    AwaitingDismissed,

    /// <summary>全アクション完了。</summary>
    Done,

    /// <summary>致命的エラー / 予期せぬ addon 状態 / 中断要求で停止。</summary>
    Stopped,
}
