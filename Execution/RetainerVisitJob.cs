using System.Collections.Generic;
using Restocker.Data;

namespace Restocker.Execution;

/// <summary>
/// Executor が 1 リテイナーぶん処理する単位。
/// 名前で呼び出し、SellList を開き、Actions を順に実行し、終了する。
/// </summary>
public sealed class RetainerVisitJob
{
    /// <summary>RetainerList addon に表示される名前。完全一致でクリック対象を特定する。</summary>
    public required string RetainerName { get; init; }

    /// <summary>このリテイナーで処理するアクション列。Refresh モードでは空。</summary>
    public required List<PlannedAction> Actions { get; init; }
}
