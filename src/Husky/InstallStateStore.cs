using System.Text.Json;

namespace Husky;

/// <summary>
/// Remembers the version Husky installed last, in <c>husky.state.json</c>
/// in the working directory (LEASH §6). It is read only when the executable
/// carries no version of its own — native binaries and .NET single-file
/// bundles on Linux/macOS (LEASH §5.3 step 7).
/// </summary>
internal static class InstallStateStore
{
    public const string FileName = "husky.state.json";

    public static void Write(string workingDirectory, string version, string executableRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(executableRelativePath);

        InstallState state = new(version, Normalize(executableRelativePath));
        string json = JsonSerializer.Serialize(state, HuskyConfigJsonContext.Default.InstallState);
        File.WriteAllText(Path.Combine(workingDirectory, FileName), json);
    }

    /// <summary>
    /// The remembered version, or null when there is none for this
    /// executable: no file, an unreadable one, or one written for a
    /// different executable (the source-supplied config may have moved it).
    /// </summary>
    public static string? ReadVersion(string workingDirectory, string executableRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(executableRelativePath);

        string path = Path.Combine(workingDirectory, FileName);
        if (!File.Exists(path)) return null;

        InstallState? state;
        try
        {
            state = JsonSerializer.Deserialize(File.ReadAllText(path), HuskyConfigJsonContext.Default.InstallState);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // Fields missing from the file deserialize as null despite the
        // non-nullable record, so check both before trusting the version.
        if (state is null || string.IsNullOrWhiteSpace(state.Version)) return null;
        return string.Equals(state.Executable, Normalize(executableRelativePath), StringComparison.Ordinal)
            ? state.Version
            : null;
    }

    private static string Normalize(string relativePath) => relativePath.Replace('\\', '/');
}
