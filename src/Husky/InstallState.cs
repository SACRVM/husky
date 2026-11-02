namespace Husky;

/// <summary>
/// The contents of <c>husky.state.json</c>: which version Husky installed
/// last, and for which executable (LEASH §6).
/// </summary>
internal sealed record InstallState(string Version, string Executable);
