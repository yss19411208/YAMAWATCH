# VALOWATCH Discordアプリ説明

VALOWATCHは、VALORANT起動中だけ指定Discord VCへ接続し、配布先PCの物理マイク入力と、LINE通話プロセスの音声を中継するWindows常駐アプリです。

## 実行すること

- VALORANT起動時に指定VCへ自動接続
- 物理マイク入力を48kHz / 16bit / stereo PCMへ変換して送信
- LINEが起動中の場合だけ、LINEプロセス音声を追加ミックス
- Discord音声停止時の自動再接続
- VALORANT起動、使用マイク、実行バージョン、更新完了のテキスト投稿
- `Alt + T` によるstrats.ggオーバーレイ
- GitHub Releasesからの無人更新

## 実行しないこと

- WAV / MP3録音
- 画面・カメラ録画
- MP3 / MP4共有
- Discord Go Live / 画面共有
- PC全体の音声取得
- Windowsのマイク音量・スピーカー音量・LINE出力先の変更

## 必要なDiscord権限

- View Channel
- Connect
- Speak
- Send Messages
- Read Message History

ファイル添付機能は使用しません。bot tokenはGitHub公開更新版へ含めず、初回配布EXEからWindows DPAPIで暗号化保存した設定を利用します。
