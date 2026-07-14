# VALOWATCH Discordアプリ説明

VALOWATCHは、VALORANT起動中だけ指定Discord VCへ接続し、配布先PCの物理マイク入力、LINE通話プロセスの音声、必要時だけDiscordデスクトップアプリの音声を中継するWindows常駐アプリです。

## 実行すること

- VALORANT起動時に指定VCへ自動接続
- PCログイン後、VALORANT未起動でもDiscord Gatewayへ接続してbotをオンライン表示にする
- 物理マイク入力を48kHz / 16bit / stereo PCMへ変換して送信
- LINEが起動中の場合だけ、LINEプロセス音声を追加ミックス
- Discord音声中継は既定OFF。`/valowatch-discord-audio enabled:true` / `enabled:false` でON/OFF
- Discordへ送る物理マイク＋LINE＋任意のDiscordアプリ音声を無料のローカルVoskモデルで文字起こしして、入室中VCのテキストチャットへ投稿
- 文字起こし投稿に、LINE捕捉中は `LINE会話`、Discord音声中継中はbotが入っているサーバー名とVC名を表示
- Discord音声停止時の自動再接続
- VALORANT起動、使用マイク、実行バージョン、更新完了のテキスト投稿
- 音声ピーク診断と、秘密情報を除外した実行ログのコードブロック差分送信
- `Alt + T` によるstrats.ggオーバーレイ
- GitHub Releasesからの無人更新

## 実行しないこと

- WAV / MP3録音
- 画面・カメラ録画
- MP3 / MP4共有
- Discord Go Live / 画面共有
- PC全体の音声取得
- VC内の他ユーザー音声をDiscord Gateway/Voiceから受信して話者別に文字起こし
- Windowsのマイク音量・スピーカー音量・LINE出力先の変更
- VALORANT未起動時のVC参加・マイク送信

## 必要なDiscord権限

- View Channel
- Connect
- Speak
- Send Messages
- Read Message History
- Use Application Commands

`/valowatch-discord-audio` はサーバー管理権限を持つユーザーだけが使えます。

ログはファイル添付せず、Discordの2000文字上限内に分割したコードブロックで送ります。初回接続時、起動20秒後、以後5分ごと、VALORANT終了時に未送信行だけを送り、`.env`、bot token、暗号化設定、ユーザープロファイルの実パスを除外します。bot tokenはGitHub公開更新版へ含めず、初回配布EXEからWindows DPAPIで暗号化保存した設定を利用します。

Discordアプリ音声はプロセス単位で捕捉します。Discord内の話者や、配布先ユーザーがDiscord画面上でどのサーバーを見ているかまでは分離しません。文字起こしに表示するサーバー名とVC名は、botが接続している指定VCの情報です。

文字起こしは録音ファイルを保存しません。短い音声チャンクをメモリ上で16kHz/mono PCMへ変換し、exeに埋め込んだVosk日本語smallモデルでローカル認識します。外部の有料APIへ音声を送信しません。
