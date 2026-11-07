# Chrome Taskbar

起動済みの Chrome のトップレベルウィンドウをボタンとして並べる WPF バー。ボタンにホバーするとそのウィンドウのタブ一覧をポップアップし、クリックでウィンドウの切り替えやタブ選択ができる。Chrome 拡張機能・Native Messaging・昇格は使わず、Win32 と Windows UI Automation（UIA）だけで動作する。

## 要件

- Windows（検証は Windows 26H2 build 26300、Chrome 157.0.8103.38、144 DPI、ja-JP）
- .NET 10 SDK（全プロジェクトが `net10.0-windows`、App と UiaWorker は WPF）
- Google Chrome（起動済みであること。本アプリは Chrome を起動しない）
- 通常権限で実行する（自動昇格はしない）

## ビルド・テスト・実行

リポジトリルートで実行する。

```powershell
dotnet build ChromeTaskbar.sln
dotnet test ChromeTaskbar.sln
dotnet run --project src/ChromeTaskbar.App
```

App は UiaWorker の実行ファイル `ChromeTaskbar.UiaWorker.exe` を次の順で探す（`WorkerLocator`）。

1. `--worker <path>` で指定したパス
2. App と同じディレクトリ
3. 開発配置の `src/ChromeTaskbar.UiaWorker/bin/<構成>/<TFM>/`（兄弟プロジェクトのビルド出力）

`WorkerLocator` はテストから呼べるよう `public static` である。見つからなければ起動時に例外になる。事前に Worker もビルドしておくこと（`dotnet build ChromeTaskbar.sln` で両方ビルドされる）。

```powershell
dotnet run --project src/ChromeTaskbar.App -- --worker <ChromeTaskbar.UiaWorker.exe のパス>
```

## 構成

| プロジェクト | 役割 |
|---|---|
| `src/ChromeTaskbar.App` | WPF バー本体。ウィンドウ列挙・前面化/最小化（Win32）、ホバー制御、ポップアップ、Worker の起動・管理（`UiaWorkerBroker`） |
| `src/ChromeTaskbar.UiaWorker` | 補助プロセス。UIA でタブの取得・選択を行い、ハングを UI から隔離する |
| `src/ChromeTaskbar.Contracts` | App と Worker が共有するプロトコル・モデル（`Protocol`、`TabSnapshot` 等） |
| `tests/ChromeTaskbar.Tests` | 単体・結合テスト。`WiringAndWin32Tests.cs`（13 件）は Win32 列挙の無例外・妥当性（2 件）と事前フィルター不変条件・フィルターは絞り込みのみ（1 件）、`PipeServer` がブローカーの Pipe にクライアント接続して Hello 送信・誤エポックに Stale 応答する往復（1 件）、`WorkerLocator` の探索順（2 件）、`PopupStatusText` のポップアップ状態文言の合成（1 件、4 ケース）、`EvidenceFiles` による T001（PoC スキーマ・証跡）/T004（受け入れ証跡）の証跡・スキーマ・ゲート整合（3 件）、`UiaWorkerBroker` 診断シンクの通知・復旧失敗・シンク例外の無害化（3 件） |

App は起動ごとにランダム名の Named Pipe（`ChromeTaskbar.<hex>`）を作り、Worker を `--pipe` 付きで起動する。接続後に Worker の Hello（エポック）が一致して初めて要求を送る。タブは Worker 発行の不透明な TabToken で指定し、スナップショット ID・エポック・ウィンドウキーが不一致なら Stale として拒否する。

## 動作

- ホバー: 同じボタンに 500 ms（`HoverController.HoverDelayMs`）滞在するとタブのポップアップを開く。ホバーだけでは Chrome の前面化・復元・タブ選択・グループ展開はしない。
- ウィンドウボタンのクリック: 押下前に前面だった Chrome は最小化、背面または最小化中なら復元して前面化する（トグル）。
- タブボタンのクリック: 対象 Chrome を復元・前面化し、UIA の `SelectionItemPattern` でそのタブを選択する。既に前面でも最小化しない。
- ポップアップ: ボタンからポップアップへ移動しても保持する。Esc・外側クリック・ボタン離脱で閉じる。
- UIA が取得できない場合はタブ機能だけが縮退し、ウィンドウの一覧と切り替えは継続する。

## 調査・受け入れの証跡と検証

| ファイル（`docs/plans/`） | 内容 |
|---|---|
| `chrome-taskbar-architecture.md` / `chrome-taskbar-architecture-readable.md` | 設計 |
| `chrome-taskbar-plan.json` | 実装計画（タスク列） |
| `chrome-taskbar-poc-evidence.json` | PoC（AC-01〜04）の実測。`poc_gate` は Pass（全 16 行 Pass） |
| `chrome-taskbar-acceptance-evidence.json` | 受け入れ（AC-05〜12）の記録。`acceptance_gate` は Blocked |
| `measure-chrome-taskbar-poc.ps1` | PoC 計測スクリプト（分離プロファイルの Chrome を起動して UIA 取得数を測る） |
| `validate-chrome-taskbar-plan.ps1` | 計画・設計・証跡の検証 |

検証は `pwsh`（PowerShell 7 以降）で実行する。Windows PowerShell 5 は対象外。

```powershell
pwsh -NoProfile -File docs/plans/validate-chrome-taskbar-plan.ps1 -PlanPath docs/plans/chrome-taskbar-plan.json -DesignPath docs/plans/chrome-taskbar-architecture.md -EvidencePath docs/plans/chrome-taskbar-poc-evidence.json -RequirePocGatePass
pwsh -NoProfile -File docs/plans/validate-chrome-taskbar-plan.ps1 -PlanPath docs/plans/chrome-taskbar-plan.json -DesignPath docs/plans/chrome-taskbar-architecture.md -EvidencePath docs/plans/chrome-taskbar-acceptance-evidence.json -RequireAcceptanceEvidence
```

前者は PoC ゲートが Pass であること、後者は受け入れ証跡の形式と除外項目を検証する（受け入れゲート自体は Blocked のまま）。

## 既知の制限

- 受け入れ 25 条件のうち 11 件が Blocked: AC-08-04（更新中ポップアップ）、AC-10（表示、4 件）、AC-11（表示差、3 件）、AC-12（負荷、3 件）。実機で未検証であり、AC-07・AC-09 の Pass は自動単体テストによるもので実機 PoC の代替ではない。
- タブ取得は RawView（`TreeWalker.RawViewWalker`）で行う。Chrome は折り畳みグループ内のタブを ControlView から省くため。ノード数が多いのでプロパティをキャッシュし、葉コントロール（TabItem・Button・Text・Edit・Separator・Slider）は辿らない。折り畳みグループの扱いは Chrome 157 の分離プロファイルで確認した範囲に限り、他の版・条件での完全性は保証しない。
- 走査上限は 1000 ノード・深さ 12・750 ms。到達時は Incomplete とし、部分的なタブ一覧は表示しない。
- ウィンドウ前面化は、`SetForegroundWindow` 成功でも直後に前面が確認できないと `ActivationPending`（失敗ではない）を返す。待機・再試行はせず、呼び出し側が後で確認する。
- Win32 のウィンドウ列挙（`Win32WindowEnumerator`）の単体テストは「例外なく返り、返った観測値が妥当（Chrome クラス名・非ゼロ HWND/PID）」に加え、事前フィルター不変条件（HWND 重複なし・可視・Chrome クラス名、`IsChromeMainWindow` 通過分は owner/tool window/cloaked なし・プロセス開始時刻>0・実行ファイル名 chrome.exe、フィルターは件数を増やさない）の確認に限る（`tests/ChromeTaskbar.Tests/WiringAndWin32Tests.cs`）。実際の Chrome ウィンドウ列挙結果の網羅性は実機でのみ確認している。
- `UiaWorkerBroker` の任意コンストラクター引数 `diagnostics`（`Action<string>`、既定は `Trace.TraceError`）は想定外のジョブ失敗や Worker 復旧失敗を通知し、最新メッセージは `LastDiagnostic` で参照できる。シンクが投げた例外はブローカーを壊さないよう意図的に握りつぶす。
- PWA・独立 DevTools・シークレットウィンドウは Win32 判定だけでは区別できず、Chrome ウィンドウとして表示される場合がある。

## 対象外

Chrome 拡張機能、Native Messaging、自動昇格、Chrome 設定変更・デバッグ起動、Chrome の終了、パッケージング・配布、永続化。
