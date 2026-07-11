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
    private readonly GoogleDriveSettingsStore settingsStore;

    public GoogleDriveUploader(AppPaths appPaths)
    {
        this.appPaths = appPaths;
        settingsStore = new GoogleDriveSettingsStore(appPaths);
    }

    public bool HasClientSecret => File.Exists(appPaths.GoogleClientSecretPath);

    public bool HasStoredCredential =>
        Directory.Exists(appPaths.GoogleTokenDirectory) &&
        Directory.EnumerateFiles(appPaths.GoogleTokenDirectory, "*", SearchOption.AllDirectories).Any();

    public async Task<DriveUploadResult> UploadAsync(string recordingFilePath, CancellationToken cancellationToken)
    {
        return await UploadAsync(
            recordingFilePath,
            "VALOWATCH match audio recording",
            "audio/wav",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DriveUploadResult> UploadAsync(
        string filePath,
        string description,
        string mimeType,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Upload target file was not found.", filePath);
        }

        if (!HasClientSecret)
        {
            throw new InvalidOperationException(
                $"Google Drive integration requires {appPaths.GoogleClientSecretPath}.");
        }

        GoogleDriveSettings settings = settingsStore.Load();
        UserCredential userCredential = await AuthorizeAsync(cancellationToken).ConfigureAwait(false);
        using DriveService driveService = new(new BaseClientService.Initializer
        {
            HttpClientInitializer = userCredential,
            ApplicationName = "VALOWATCH"
        });

        await using FileStream recordingStream = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        DriveFile fileMetadata = new()
        {
            Name = Path.GetFileName(filePath),
            Description = description
        };
        if (!string.IsNullOrWhiteSpace(settings.FolderId))
        {
            fileMetadata.Parents = [settings.FolderId.Trim()];
        }

        FilesResource.CreateMediaUpload uploadRequest = driveService.Files.Create(
            fileMetadata,
            recordingStream,
            mimeType);
        uploadRequest.Fields = "id,name,webViewLink";

        IUploadProgress uploadProgress = await uploadRequest.UploadAsync(cancellationToken).ConfigureAwait(false);
        if (uploadProgress.Status != UploadStatus.Completed)
        {
            Exception? uploadException = uploadProgress.Exception;
            throw new InvalidOperationException(
                uploadException is null ? "Google Drive upload did not complete." : uploadException.Message,
                uploadException);
        }

        DriveFile uploadedFile = uploadRequest.ResponseBody;
        return new DriveUploadResult(uploadedFile.Id ?? string.Empty, uploadedFile.WebViewLink ?? string.Empty);
    }

    private async Task<UserCredential> AuthorizeAsync(CancellationToken cancellationToken)
    {
        GoogleDriveSettings settings = settingsStore.Load();
        await using FileStream clientSecretStream = new(
            appPaths.GoogleClientSecretPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        GoogleClientSecrets clientSecrets = GoogleClientSecrets.FromStream(clientSecretStream);
        string credentialUser = string.IsNullOrWhiteSpace(settings.CredentialUser)
            ? "VALOWATCH"
            : settings.CredentialUser.Trim();

        return await GoogleWebAuthorizationBroker.AuthorizeAsync(
            clientSecrets.Secrets,
            Scopes,
            credentialUser,
            cancellationToken,
            new FileDataStore(appPaths.GoogleTokenDirectory, true)).ConfigureAwait(false);
    }
}
