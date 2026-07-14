using System.IO.Compression;
using System.Reflection;

namespace VALOWATCH;

internal static class VoskModelProvider
{
    private const string EmbeddedJapaneseModelZipResourceName = "VALOWATCH.Models.vosk-model-small-ja-0.22.zip";
    private const string JapaneseModelDirectoryName = "vosk-model-small-ja-0.22";

    public static string EnsureJapaneseModel(
        AppPaths appPaths,
        string configuredModelPath,
        Action<string, Exception?> writeLog)
    {
        if (!string.IsNullOrWhiteSpace(configuredModelPath))
        {
            string expandedConfiguredPath = Environment.ExpandEnvironmentVariables(configuredModelPath.Trim());
            string fullConfiguredPath = Path.GetFullPath(expandedConfiguredPath);
            if (IsUsableModelDirectory(fullConfiguredPath))
            {
                writeLog($"Offline transcription will use configured Vosk model. Path: {fullConfiguredPath}", null);
                return fullConfiguredPath;
            }

            throw new DirectoryNotFoundException(
                $"Configured Vosk model directory was not found or is incomplete: {fullConfiguredPath}");
        }

        string dataModelDirectory = Path.Combine(appPaths.DataDirectory, "models", JapaneseModelDirectoryName);
        if (IsUsableModelDirectory(dataModelDirectory))
        {
            writeLog($"Offline transcription will use data Vosk model. Path: {dataModelDirectory}", null);
            return dataModelDirectory;
        }

        string applicationModelDirectory = Path.Combine(AppContext.BaseDirectory, "models", JapaneseModelDirectoryName);
        if (IsUsableModelDirectory(applicationModelDirectory))
        {
            writeLog($"Offline transcription will use application Vosk model. Path: {applicationModelDirectory}", null);
            return applicationModelDirectory;
        }

        if (!TryExtractEmbeddedJapaneseModel(dataModelDirectory, writeLog))
        {
            throw new FileNotFoundException(
                "Offline transcription model is missing. The GitHub release build must embed vosk-model-small-ja-0.22.zip, or VALOWATCH_TRANSCRIPTION_MODEL_PATH must point to an extracted Vosk model directory.");
        }

        if (!IsUsableModelDirectory(dataModelDirectory))
        {
            throw new InvalidOperationException(
                $"Embedded Vosk model extraction completed, but the model directory is incomplete: {dataModelDirectory}");
        }

        writeLog($"Offline transcription extracted embedded Vosk model. Path: {dataModelDirectory}", null);
        return dataModelDirectory;
    }

    private static bool TryExtractEmbeddedJapaneseModel(
        string dataModelDirectory,
        Action<string, Exception?> writeLog)
    {
        Assembly assembly = typeof(VoskModelProvider).Assembly;
        using Stream? modelZipStream = assembly.GetManifestResourceStream(EmbeddedJapaneseModelZipResourceName);
        if (modelZipStream is null)
        {
            return false;
        }

        string modelParentDirectory = Path.GetDirectoryName(dataModelDirectory) ??
            Path.Combine(AppContext.BaseDirectory, "models");
        string extractionDirectory = Path.Combine(
            modelParentDirectory,
            $".extract-{Environment.ProcessId}-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(modelParentDirectory);
            if (Directory.Exists(extractionDirectory))
            {
                Directory.Delete(extractionDirectory, recursive: true);
            }

            ZipFile.ExtractToDirectory(modelZipStream, extractionDirectory);
            string extractedModelDirectory = Path.Combine(extractionDirectory, JapaneseModelDirectoryName);
            if (!Directory.Exists(extractedModelDirectory))
            {
                string[] childDirectories = Directory.GetDirectories(extractionDirectory);
                if (childDirectories.Length == 1)
                {
                    extractedModelDirectory = childDirectories[0];
                }
            }

            if (Directory.Exists(dataModelDirectory))
            {
                Directory.Delete(dataModelDirectory, recursive: true);
            }

            Directory.Move(extractedModelDirectory, dataModelDirectory);
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            writeLog("Embedded Vosk model extraction failed.", exception);
            return false;
        }
        finally
        {
            TryDeleteDirectory(extractionDirectory);
        }
    }

    private static bool IsUsableModelDirectory(string modelDirectory)
    {
        if (!Directory.Exists(modelDirectory))
        {
            return false;
        }

        string configurationDirectory = Path.Combine(modelDirectory, "conf");
        string acousticModelDirectory = Path.Combine(modelDirectory, "am");
        return Directory.Exists(configurationDirectory) && Directory.Exists(acousticModelDirectory);
    }

    private static void TryDeleteDirectory(string directoryPath)
    {
        try
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
