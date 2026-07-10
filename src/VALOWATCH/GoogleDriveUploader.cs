using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.Util.Store;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace VALOWATCH;

public sealed class GoogleDriveUploader
{
    private static readonly string[] Scopes = [DriveService.Scope.DriveFile];

    private readonly AppPaths appPaths;

    public GoogleDriveUploader(AppPaths appPaths)
    {
        this.appPaths = appPaths;
    }

    public bool HasClientSecret => File.Exists(appPaths.GoogleClientSecretPath);

    public async Task<DriveUploadResult> UploadAsync(string recordingFilePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(recordingFilePath))
        {
            throw new FileNotFoundException("アップロード対象の録音ファイルが見つかりません。", recordingFilePath);
        }

        if (!HasClientSecret)
        {
            throw new InvalidOperationException(
                $"Google Drive連携には {appPaths.GoogleClientSecretPath} が必要です。Google CloudでDesktop app用OAuthクライアントを作成し、この名前で配置してください。");
        }

        UserCredential userCredential = await AuthorizeAsync(cancellationToken).ConfigureAwait(false);
        using DriveService driveService = new(new BaseClientService.Initializer
        {
            HttpClientInitializer = userCredential,
            ApplicationName = "VALOWATCH"
        });

        await using FileStream recordingStream = new(
            recordingFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        DriveFile fileMetadata = new()
        {
            Name = Path.GetFileName(recordingFilePath),
            Description = "VALOWATCH match audio recording"
        };

        FilesResource.CreateMediaUpload uploadRequest = driveService.Files.Create(
            fileMetadata,
            recordingStream,
            "audio/wav");
        uploadRequest.Fields = "id,name,webViewLink";

        IUploadProgress uploadProgress = await uploadRequest.UploadAsync(cancellationToken).ConfigureAwait(false);
        if (uploadProgress.Status != UploadStatus.Completed)
        {
            Exception? uploadException = uploadProgress.Exception;
            throw new InvalidOperationException(
                uploadException is null ? "Google Driveアップロードが完了しませんでした。" : uploadException.Message,
                uploadException);
        }

        DriveFile uploadedFile = uploadRequest.ResponseBody;
        return new DriveUploadResult(uploadedFile.Id ?? string.Empty, uploadedFile.WebViewLink ?? string.Empty);
    }

    private async Task<UserCredential> AuthorizeAsync(CancellationToken cancellationToken)
    {
        await using FileStream clientSecretStream = new(
            appPaths.GoogleClientSecretPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        GoogleClientSecrets clientSecrets = GoogleClientSecrets.FromStream(clientSecretStream);
        return await GoogleWebAuthorizationBroker.AuthorizeAsync(
            clientSecrets.Secrets,
            Scopes,
            "VALOWATCH",
            cancellationToken,
            new FileDataStore(appPaths.GoogleTokenDirectory, true)).ConfigureAwait(false);
    }
}
