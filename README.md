# 収集品一括納品 — Collectable Turn In 0.2.1

日本・グローバル版の通常のXIVLauncher / Dalamud API 15用の単独プラグインです。
Daily Routines本体、OmenTools、KamiToolKitのインストールは不要です。

**ビルドとオフライン検証を終えた試作版です。ゲーム内での納品・NPC切り替えは未検証です。**

## 0.2.1の修正

- 応答を待った後の追加待機を廃止。前回送信からの最小間隔と、ゲームの応答確認の両方を満たすと次を送ります。0msでも未応答の連続送信はしません。
- 旧設定の300ms・600msは初回更新時に100msへ移行します。それ以外の値は維持します。設定で0〜2000msに変更可能です。
- NPC検索時の一時的な選択不可状態を除外せず、ゲームデータ上のNPC名も照合します。操作直前には選択可能状態を再確認します。
- NPC切り替えを開始できない理由を画面・チャットに表示。見つからない場合と6mより遠い場合を区別します。
- 納品画面と会話状態の終了後、250ms安定して操作可能になるのを待ち、交換NPCをターゲットして話しかけます。操作が成立せず会話中でもない場合のみ、1秒間隔で最大3回まで試行します。
- 45項目のオフラインテストに合格。報告されたゲーム内症状の解消は実機での確認待ちです。

## IPC連携

0.2.0で開始・停止・状態取得・スクリップNPC切り替え・完了通知のIPC v1を追加しました。連携方法は [IPC.md](IPC.md) を参照してください。

## 導入

通常のプラグイン一覧から導入する場合、Dalamud設定のカスタムプラグインリポジトリに次のURLを追加してください。

https://raw.githubusercontent.com/bootrebooter/CollectableTurnIn/main/pluginmaster.json

以下はローカルDLLを使用する場合の手順です。

1. このフォルダを保存します。ZIPから使用する場合は、まず全体を展開します。
2. Dalamud設定 → Experimental（実験的機能）→ Dev Plugin Locationsを開きます。
3. このフォルダの直下にある `CollectableTurnIn.dll` のフルパスを追加して保存します。
4. `/xlplugins` → Dev Tools → Installed Dev Pluginsで「収集品一括納品 / Collectable Turn In」を有効にします。表示されない場合は開発用プラグイン一覧を再読み込みしてください。

DLLと同じ場所にある `CollectableTurnIn.json` も残してください。`source` 内のファイルは編集・再ビルド用です。
Daily Routinesの同じ収集品納品機能を使っている場合は、そちらを無効にしてから使用してください。二つの連続納品処理が同時に動く構成は検証していません。

## 操作

- 収集品納品NPCに話しかけ、通常の納品ボタンが有効になるように対象を選びます。
- 納品画面の横に出る「一括納品」を押します。現在の納品一覧を、1件ごとの一覧減少を確認しながら処理します。別カテゴリを自動で巡回する機能ではありません。
- 「停止」または `/cti stop` で次の納品を止めます。すでにゲームへ渡した1件の処理は取り消しません。
- 実行中は選択対象やカテゴリを変更せず、通常の納品ボタンも操作しないでください。
- 「スクリップ交換NPCへ」を押すと、6m以内の最寄りの「スクリップ取引窓口」を探し、納品画面を閉じて話しかけます。対象がいない場合は納品画面を閉じずに通知します。
- NPCが障害物の向こうにいる場合などは切り替えが成立しないことがあります。NPCに近づいて再実行してください。自動移動や自動購入は含みません。
- `/cti` で操作画面の常時表示を切り替えられます。設定から最小送信間隔（0〜2000ms、初期値100ms）、通知、素材不足メッセージの整理を変更できます。

確認画面、素材不足、一覧の想定外の変化、画面終了、エリア移動、ログアウト、10秒間進捗なしの場合に停止します。スクリップ上限などの確認画面は自動承認しません。

## 元コードの調査と組み込み

元機能: [DailyRoutines.ModulesPublic / AutoCollectableExchange.cs](https://github.com/Dalamud-DailyRoutines/DailyRoutines.ModulesPublic/blob/c93989539a6f2de0c8fe2d178f6532efe453ff89/Interface/AutoCollectableExchange.cs)

| 元機能が利用する処理 | この単独版の実装 |
| --- | --- |
| ModuleBaseによる初期化・終了管理 | Dalamudプラグインとして初期化。無効化時にイベント・フック・実行状態を解除 |
| TaskHelperによる繰り返し・待機・中断 | 1件の応答を待ってから次を送る状態管理。10秒のタイムアウトと手動停止 |
| CollectablesShopの一覧と納品ボタン | 元と同じ一覧ノード31、ボタン51を参照。ネイティブ納品処理を特定して呼び出し |
| 指定キーで既存ボタンを一括納品に切り替える処理 | 独立した「一括納品」「停止」ボタン。元のゲームボタンのイベントは置き換えない |
| KamiToolKitによる交換NPCボタン追加 | 納品画面の横にDalamudの操作パネルを表示 |
| LogMessageManagerによるログ1941抑制 | 連続納品中の「交換に必要なアイテムが足りません」のみを任意に整理し、停止理由を通知。その他のゲームメッセージはそのまま |
| EventFramework / Luminaによる交換NPC検索 | ゲームのNPC名データ（ENpcResident 1001617）と周囲のNPC一覧から検索 |
| EventStartPacktによる交換イベント開始 | グローバル版の通常のNPC操作処理 `TargetSystem.InteractWithObject` に置き換え。距離・視線チェックを使用 |
| NotifyHelperによる終了通知 | チャットとゲーム内画面通知に置き換え。Windowsトレイ通知は含まない |
| Lang / PluginConfig | 日本語UIとDalamudの設定保存に置き換え |

OmenToolsのTaskHelper、EventFrameworkExtension、EventStartPackt、LogMessageManagerも参照して依存関係を確認しました。
元コードの通信パケット送信基盤や、Daily Routinesの他の自動化モジュールは同梱していません。

## 検証範囲と限界

- インストール済みのDalamud 15.0.3.7の参照DLLに対してReleaseビルド成功（警告0、エラー0）。
- 連続処理の状態管理・IPC通知管理テスト45項目に合格。応答前の重複送信防止、待機間隔、終了、タイムアウト、手動停止、確認画面、一覧変化、再開始を確認。
- インストール済みの `ffxiv_dx11.exe` をファイルとして検査し、元コードの納品関数の検索パターンが1か所だけ一致することを確認。
- ローカルの日本語ゲームデータでNPC行1001617が「スクリップ取引窓口」、ログ1941が素材不足メッセージであることを確認。
- これらは実際のゲーム内動作を保証するものではありません。ゲーム内でのDLL読み込み、ボタン表示、連続納品、交換NPCへの切り替えは未検証です。ゲームのファイルやDalamud設定は変更していません。
- ゲーム更新後に納品処理が見つからない場合は一括納品ボタンを無効にします。APIのメジャーバージョンが変わった場合は、マニフェストの数字だけを書き換えず再ビルド・再検証してください。
- 「確認済み納品数」はリストが1件減った回数です。ゲームからの取引履歴を直接取得した件数ではありません。

## 再ビルド

.NET 10 SDKと対応するDalamudの開発用DLLが必要です。通常は `source/CollectableTurnIn.csproj` をビルドします。参照先の既定値は `%APPDATA%\XIVLauncher\addon\Hooks\dev` です。別の参照先は `DalamudLibPath` で指定できます。

```powershell
dotnet build .\source\CollectableTurnIn.csproj -c Release '-p:DalamudLibPath=C:\path\to\Dalamud'
dotnet run --project .\source\tests\BatchTests.csproj -c Release
```

変更したDLLを使う場合は、プラグインを無効にしてからDLLとJSONを差し替え、再度有効にしてください。

## 出典・ライセンス

元実装の作者: AtmoOmen / Daily Routines contributors。
本パッケージは元の収集品納品モジュールを参考に、単独プラグインへの構成変更・状態管理・UI・NPC操作を実装した派生版です。元プロジェクトの公式配布物ではありません。

元リポジトリのGNU AGPL v3に合わせ、ライセンス全文を `LICENSE`、対応するソース一式を `source` に同梱しています。Dalamudなどの実行基盤のDLLとゲーム本体は同梱していません。
