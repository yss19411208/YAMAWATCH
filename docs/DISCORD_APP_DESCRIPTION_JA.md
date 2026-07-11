# VALOWATCH Discord向け説明文

このファイルは、DiscordでVALOWATCHを紹介するときに貼る文章と、音声がノイズっぽい・途切れる場合の調整メモです。

## Discordに貼る短い説明

VALOWATCHは、VALORANT用の補助アプリです。

VALORANTを起動すると自動でバックグラウンド動作し、Discord botが指定VCに入ります。VCでは基本的に物理マイクの音声を流し、LINEプロセスの音だけをDiscord側へ中継できます。`Alt + T` を押すと、VALORANT用のラインナップ確認ページをオーバーレイで表示/非表示できます。

インストール後はWindows起動時に自動で常駐します。通常の設定画面や履歴画面は出ず、表に出るUIは `Alt + T` のラインナップページだけです。VALORANTをウィンドウフルスクリーンにして使ってください。

## できること

- VALORANT起動を検知する
- VALORANT起動中だけ `Alt + T` に反応する
- `Alt + T` で `https://strats.gg/valorant/lineups` を表示/非表示する
- 一度開いたWebView2を保持し、毎回ページを読み直さない
- Discord botを指定VCへ自動参加させる
- Discord botから物理マイク音声をVCへ流す
- LINEプロセスの音だけをDiscord bot音声へミックスする
- VALORANT終了後に録音WAVをMP3へ変換し、指定テキストチャンネルへ通知抑制付きで添付する
- 明示的に有効化した場合、VALORANT起動中の画面MP4とカメラMP4を保存し、指定テキストチャンネルへ通知抑制付きで添付する
- VALORANT起動時にGitHub更新確認を行う
- 音声トラブル時に `data/logs/valowatch.log` へ診断ログを残す

## まだできないこと

- Discord botによる安定したGo Live相当の画面共有/カメラ映像送信
- VALORANTの試合開始/終了の完全検知
- VALORANT内部情報やランク情報の取得
- フルスクリーン排他モード上への完全なオーバーレイ表示
- Discord側のノイズ抑制をbot音声へ直接適用すること

## 使う前の設定

VALORANT側は次の設定にしてください。

```text
設定 → グラフィック → 一般 → 画面モード → ウィンドウフルスクリーン → 適用
```

フルスクリーン排他モードだと、通常のWindowsオーバーレイがVALORANTの前に出ないことがあります。

## 音声の現在の仕様

現在はマイク入力を外部音としてDiscord botへ送ります。`DISCORD_STREAM_LINE_AUDIO=true` の場合だけ、Windowsのprocess loopbackでLINEプロセス音声を追加ミックスします。Discord側には「外部音 + LINE音声」が届きます。

LINEが起動していない間は、LINE音声側は待機します。対応していないWindows環境ではLINE音声だけ取得できず、ログに理由を残してマイク音声だけで続行します。

このミックスはDiscord botへ送る音声だけに使います。Windowsの再生デバイス、スピーカー/ヘッドホン音量、LINEの出力先は変更しないため、このPCで聞こえる音は通常どおりです。

仮想音声デバイスは、明示指定しない限り自動選択しません。これは、HitPaw Virtual Audio、VB-Cable、Voicemeeterなどを誤って拾うと、マイクではなくPC内部音が流れることがあるためです。

自動選択から除外している例:

- HitPaw Virtual Audio
- VB-Audio / VB-Cable
- Voicemeeter
- Steam Streaming Microphone
- OBS系の仮想入力
- Wave Link系の仮想入力

## ノイズや途切れがあるとき

まず、`DISCORD_MIC_DEVICE_NAME` で使いたいマイクを明示指定してください。自動選択でも動きますが、配布先PCでは仮想音声デバイスや複数マイクがあるため、明示指定のほうが安定します。

例:

```dotenv
DISCORD_MIC_DEVICE_NAME=Realtek
DISCORD_MIC_VOLUME=0.85
DISCORD_MIC_NOISE_GATE=0
DISCORD_STREAM_LINE_AUDIO=true
DISCORD_LINE_AUDIO_VOLUME=0.45
```

`DISCORD_MIC_DEVICE_NAME` は完全一致ではなく、デバイス名の一部で構いません。ログに出る `Device:` や `Active capture devices:` を見て、`Realtek`、`Headset`、`マイク` などを入れてください。

音が割れる、ザラつく、強いノイズが乗る場合:

- `DISCORD_MIC_VOLUME=0.85` から試す
- まだ割れるなら `0.75` まで下げる
- Windows側のマイクブーストを下げる
- マイク入力レベルが100なら、70から90程度に下げて試す

声が途切れる場合:

- `DISCORD_MIC_NOISE_GATE=0` のままにする
- ノイズゲートを上げすぎない
- 無線マイクやBluetoothマイクなら、有線またはUSB接続で試す
- VALORANT、配信ソフト、ブラウザ、WebView2でCPU使用率が高くないか確認する
- Discord接続が不安定な回線では、VC参加や音声送信が遅れることがある

常時ノイズだけが気になる場合:

```dotenv
DISCORD_MIC_NOISE_GATE=0.005
```

ここから試してください。`0.015` 以上にすると、小さい声や語尾が切れやすくなります。途切れが気になる場合は `0` に戻してください。

## ログの見方

ログは通常ここに出ます。

```text
C:\Users\<ユーザー名>\Documents\VALOWATCH\data\logs\valowatch.log
```

重要な行:

```text
Microphone capture started. Device: ...
Active capture devices: ...
Audio stats. CapturedPeak: ... WrittenPeak: ...
```

見方:

- `Device:` が物理マイクならOK
- `Device:` がHitPawやVB-Cableなら、マイク選択が間違っている
- `CapturedPeak` が0に近いなら、マイク入力がほぼ入っていない
- `CapturedPeak` が出ていて `WrittenPeak` も出ているなら、アプリ側は音声をDiscordへ送る直前まで処理できている
- `Connect: False` または `Speak: False` が出るなら、Discord側でbot権限を直す

## 配布時に伝えること

配布相手には、最低限これを伝えてください。

```text
1. VALORANTを閉じた状態でインストーラーを実行してください。
2. VALORANTは ウィンドウフルスクリーン にしてください。
3. VALORANT起動時にbotがVCへ入ります。
4. bot音声はマイクで拾う外部音を流します。LINE音声中継が有効な場合は、LINEプロセスの音だけを追加で混ぜます。
5. Alt + T でラインナップページを表示/非表示できます。
6. VALORANT終了後、MP3/MP4は指定Discordチャンネルへ通知抑制付きで添付されます。
7. Google Drive は使いません。
8. 画面/カメラ録画を有効化する場合は、録画される内容を本人が理解している状態で使ってください。
9. 音が変な場合は valowatch.log を送ってください。
```

## 注意

VALOWATCHはRiot Games、VALORANT、Discordの公式アプリではありません。VALORANTの内部メモリや非公式APIには触れず、Windowsのプロセス検知、通常ウィンドウ、WebView2、Discord bot接続だけで動作します。
