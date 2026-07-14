# VALOWATCH 完全版検証報告

作成日: 2026-07-12
対象環境: Windows 10/11、.NET 8、WinForms、WebView2

## 発生していた状況

友達のPCではVALOWATCHが起動していても、VALORANT上で `Alt + T` が動作しないと報告されました。友達側ログは取得できないため、旧実装を分解し、疑似フルスクリーンVALORANTと実プロセス名を使って再検証しました。

## 確認した問題

1. Alt+Tの代替監視がWinForms UIタイマー上にあり、WebView2初期化などでUI処理が遅れた場合、短いキー入力を取り逃がせる状態でした。
2. 表示直前にVALORANTプロセスを再確認し、一瞬でも検出できない場合は表示要求をログなしで捨てていました。
3. オーバーレイに `WS_EX_NOACTIVATE` を付けていたため、表示できても操作フォーカスを取得しにくい構成でした。
4. WebView2初期化失敗時のエラー表示を、Alt+T表示処理が再び隠して空画面にする経路がありました。
5. 従来の疑似テストは「中央にウィンドウがある」だけを確認し、操作フォーカス、VALORANTへの復帰、ページ保持を確認していませんでした。
6. 独立キー監視スレッドの開始判定がフラグ列挙を完全一致で比較しており、`Background | Unstarted`状態では開始されていませんでした。初回だけ低レベルフックで動作し、その後Alt+Tが反応しない直接原因です。

## 完全版の修正

- `RegisterHotKey` による通常のAlt+Tを維持。
- UIスレッドから独立した10msキー状態監視を追加。
- `RIDEV_INPUTSINK` Raw Inputで物理キーボードをバックグラウンド受信。
- Raw Input、キー状態監視、通常ホットキーの重複通知は500msクールダウンで1回に統合。
- 低レベルキーボードフックのコールバックは専用メッセージを送るだけにし、表示・ログ処理をコールバック外へ分離する。
- VALORANT検出済み状態を20秒保持し、一瞬のプロセス検出揺れで表示要求を破棄しない。
- `VALORANT-Win64-Shipping` と `VALORANT-*` を検出。
- オーバーレイ表示時に操作フォーカスを取得し、非表示時にVALORANTへ戻す。
- 非表示中も同じWebView2を保持し、再表示で再読み込みしない。
- WebView2の通信失敗時だけ次回表示で再試行する。
- GitHub更新と生存監視を本体から独立した常駐 `GITHUB.exe` へ分離する。
- `GITHUB.exe` が5秒ごとに本体を監視し、異常終了時は別PIDで再起動する。
- `GITHUB.exe` 自身もRelease digest検証後に旧プロセスを終了し、自己置換して監視を再開する。
- 旧版互換用 `VALOWATCH_Update.exe` は更新後に `GITHUB.exe` を配置して常駐方式へ移行する。
- 更新判定ではAssembly informational versionのcommitを外部`.env`より優先する。
- 更新用EXEでも設置済み本体のSHA-256を比較し、同一Releaseの再配置を止める。
- 途中ダウンロードを保持し、再試行時にHTTP Rangeで再開する。

## 自動検証結果

| 検証 | 結果 |
| --- | --- |
| Releaseビルド | 成功、エラー0 |
| Alt+T Raw Input登録 | 成功 |
| AltなしTの無視 | 成功 |
| Alt+T初回入力 | 成功 |
| キーリピート抑止 | 成功 |
| Altを先に押した起動状態 | 成功 |
| 通常疑似フルスクリーン表示 | 成功 |
| RegisterHotKey競合時の代替表示 | 成功 |
| オーバーレイ操作フォーカス | 成功 |
| 非表示後のVALORANTフォーカス復帰 | 成功 |
| 同一WebView2での再表示 | 成功 |
| `VALORANT-Win64-Shipping`プロセス検出 | 成功 |
| 実マイク入力 | 成功 |
| LINE process loopback基盤 | 成功 |
| マイク＋LINEミックス | 成功 |
| ローカルVosk文字起こし用PCM変換 | 成功 |
| Discord音声DLL | 成功 |
| 5分更新周期 | 成功 |
| 本体とGitHub更新処理の通常実行経路分離 | 成功 |
| 独立GITHUB監視から本体を初回起動 | 成功 |
| 本体強制終了後の5秒監視・別PID再起動 | 2回成功 |
| GITHUB監視の多重起動抑止 | 成功 |
| GITHUB自身の検証済み自己置換・再常駐 | 2回成功 |
| インストーラー内GITHUBのSHA-256一致 | 2回成功 |
| インストール直後のGITHUB経由本体起動 | 2回成功 |
| 古い`.env`より実行中EXEのcommitを優先 | 成功 |
| 暗号化設定復元 | 成功 |
| 更新失敗時の旧本体再起動 | 成功 |
| 実行中本体の停止・ロック解除待ち・置換・一時ファイル除去 | 成功 |
| 旧appのクリーン再インストール | 成功 |
| `.env` と `data` の保持 | 成功 |
| 開発ソースフォルダーの削除拒否 | 成功 |
| 再インストール診断ログの保留保存 | 成功 |
| 診断ログからユーザープロファイルパスを置換 | 成功 |
| Discord送信失敗時のログ保持 | 成功 |
| 開発PCでの安全な実行先自動切り替え | 成功 |
| 再インストーラーの無画面・無確認実行 | 成功 |
| `RegisterHotKey`競合時の低レベルキーボードフック | 成功 |
| 独立キー監視スレッドの実起動・ハートビート | 3回成功 |
| 30秒以上RegisterHotKeyを競合させた多重Alt+T監視 | 成功 |
| 実行ログの再帰収集・秘密情報除外・2000文字分割 | 4回成功 |
| 送信済み行の永続化と追記分だけの再収集 | 4回成功 |
| ログ送信より先にマイク中継を開始 | コード順序・ビルド成功を確認 |
| 初回・起動20秒後・5分ごと・終了時のDiscordコードブロック送信 | 外部データ送信制約により実Discord接続では未実施 |

## 自動検証できない範囲

テストのキー入力にはWindows `SendInput` を使います。`SendInput` は物理HIDのRaw Inputではないため、物理キーボードから `WM_INPUT` が届く最終経路だけは完全自動化できません。WindowsへのRaw Input登録と入力状態処理は個別に検証しています。最終的な物理入力確認は、実PCでVALORANTをウィンドウフルスクリーンにして実際に `Alt + T` を押す必要があります。

フルスクリーン排他モードのDirectX画面には、通常のWin32最前面ウィンドウを重ねられません。VALORANT側は `設定 → グラフィック → 一般 → 画面モード → ウィンドウフルスクリーン → 適用` が前提です。

## 参照した仕様

- RegisterHotKey: https://learn.microsoft.com/ja-jp/windows/win32/api/winuser/nf-winuser-registerhotkey
- Raw Input: https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-rawinputdevice
- WM_INPUT: https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-input
- GetAsyncKeyState: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getasynckeystate
- SetForegroundWindow: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow
- SetWindowsHookEx: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw
- KBDLLHOOKSTRUCT: https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-kbdllhookstruct
