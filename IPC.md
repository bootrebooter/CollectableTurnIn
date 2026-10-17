# CollectableTurnIn IPC v1

プラグイン 0.2.0以降。DalamudのIPCを使用します。HTTPサーバーや追加ライブラリは不要です。

## 公開インターフェース

|名前|GetIpcSubscriberの型|戻り値・通知|
|---|---|---|
|`CollectableTurnIn.GetApiVersion`|`<int>`|`1`|
|`CollectableTurnIn.GetStatus`|`<string>`|状態JSON|
|`CollectableTurnIn.StartTurnIn`|`<string>`|受付結果JSON|
|`CollectableTurnIn.Stop`|`<string>`|停止結果JSON|
|`CollectableTurnIn.SwitchToScripNpc`|`<string>`|受付結果JSON|
|`CollectableTurnIn.OnCompleted`|`<string, object>`|JSON文字列を引数とするイベント|

操作系3関数は必ず `IFramework.RunOnFrameworkThread` 経由で呼び出してください。別スレッドからの呼び出しは `WrongThread` で拒否します。バージョンと状態取得は別スレッドでも可能です。状態は最後に発行したスナップショットです。

納品NPCの画面を開き、納品可能な対象を選んでから開始してください。NPC切り替えにも納品画面が必要です。6m以内のスクリップ取引窓口への会話開始のみを行い、移動・購入は行いません。処理中の新しい開始要求は拒否します。

## 受付結果

```json
{"apiVersion":1,"accepted":true,"code":"Accepted","operationId":"..."}
```

`accepted` は受付の成否です。実際の完了を表しません。開始が受理されると操作IDが割り当てられ、後続の完了通知にも同じIDが含まれます。拒否時のIDはnullで、完了イベントも発生しません。

- 開始成功: `Accepted`
- 停止成功: `Stopped`（停止した操作のID）
- 拒否: `Unavailable`, `WrongThread`, `Busy`, `NotLoggedIn`, `ShopNotOpen`, `DialogOpen`, `NpcNotFound`, `UnsupportedGameVersion`, `NoItems`, `NotReady`, `NotRunning`, `Error`

停止は今後の送信を止めます。すでに送信済みの納品1件を取り消すことはできません。

## 状態

```json
{"apiVersion":1,"state":"TurningIn","busy":true,"operationId":"...","operation":"TurnIn","completedCount":2,"message":"納品中","turnInSupported":true,"lastResult":null}
```

- state: `Idle`, `PendingTurnIn`, `TurningIn`, `PendingSwitch`, `SwitchingNpc`, `Unavailable`
- operation: `TurnIn`, `SwitchToScripNpc`、待機中はnull
- completedCount: 現在の納品操作で一覧の1件減少を確認した回数。待機中・NPC切り替え時は0
- lastResult: 直近に終了した操作の完了JSON。まだない場合null。次の操作の開始後も前の結果を保持
- message: 表示用日本語。分岐にはstate/codeを使用

## 完了通知

```json
{"apiVersion":1,"operationId":"...","operation":"TurnIn","success":true,"code":"Completed","completedCount":3,"message":"納品が完了しました"}
```

受理済み操作の終了ごとに1回通知します。UIボタンで開始した操作も通知対象です。自分の操作IDと照合してください。開始と停止を同じフレームで行った場合も通知は次の更新処理で届きます。終了通知中の新規操作は許可されますが、その操作の終了通知は次回以降に送出します。

- successは `Completed` の場合だけtrue
- 終了code: `Completed`, `Cancelled`, `NoItems`, `PreconditionsChanged`, `LoggedOut`, `TerritoryChanged`, `InsufficientItems`, `ShopClosed`, `DialogOpen`, `ListChanged`, `TimedOut`, `NpcNotFound`, `Error`, `Unloaded`
- NPC切り替えの完了は対象メニューの表示検出。商品の購入完了ではありません
- 納品件数は一覧変化に基づきます。取引履歴に基づく確定件数ではありません
- プラグイン無効化時は可能な範囲で `Unloaded` を送出して登録を解除します。プロセス終了・クラッシュ時の通知は保証されません
- 通常はFramework更新中に通知しますが、無効化時のスレッドは保証しません。ハンドラーからゲームを操作する場合はFrameworkへ処理を渡してください
- 通知ハンドラーは短くし、例外を外に出さないでください。購読側で例外が発生すると他の購読者への配送に影響する場合があります。取り逃しはGetStatusのlastResultで補完できますが、保持されるのは最新1件だけです

## C#での利用例

```csharp
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using System.Text.Json;

public sealed class TurnInClient : IDisposable
{
    private readonly IDalamudPluginInterface pi;
    private readonly IFramework framework;
    private readonly ICallGateSubscriber<string, object> completed;
    public event Action<string>? ResultReceived;

    public TurnInClient(IDalamudPluginInterface pi, IFramework framework)
    {
        this.pi = pi;
        this.framework = framework;
        completed = pi.GetIpcSubscriber<string, object>("CollectableTurnIn.OnCompleted");
        completed.Subscribe(OnCompleted); // 操作を開始する前に購読
    }

    public async Task<string> StartAsync()
    {
        return await framework.RunOnFrameworkThread(() =>
        {
            if (pi.GetIpcSubscriber<int>("CollectableTurnIn.GetApiVersion").InvokeFunc() != 1)
                throw new NotSupportedException("Unsupported CollectableTurnIn IPC version");
            return pi.GetIpcSubscriber<string>("CollectableTurnIn.StartTurnIn").InvokeFunc();
        });
    }

    public Task<string> StopAsync() => framework.RunOnFrameworkThread(() =>
        pi.GetIpcSubscriber<string>("CollectableTurnIn.Stop").InvokeFunc());

    public Task<string> SwitchAsync() => framework.RunOnFrameworkThread(() =>
        pi.GetIpcSubscriber<string>("CollectableTurnIn.SwitchToScripNpc").InvokeFunc());

    public string GetStatus() =>
        pi.GetIpcSubscriber<string>("CollectableTurnIn.GetStatus").InvokeFunc();

    private void OnCompleted(string json)
    {
        try { ResultReceived?.Invoke(json); }
        catch { /* 呼び出し元プラグインのログに記録する */ }
    }

    public void Dispose() => completed.Unsubscribe(OnCompleted);
}
```

呼び出し側はIPC提供元が未インストール・無効・更新中の場合の例外を処理し、自動連打しないでください。戻り値をJsonDocumentなどで読み、acceptedとoperationIdを確認してください。ゲーム内での他プラグインからの呼び出しは未検証です。
