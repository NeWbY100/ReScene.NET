using ReScene.NET.Models;
using ReScene.NET.Services;

namespace ReScene.NET.Tests;

/// <summary>
/// Reusable no-op <see cref="IFileDialogService"/> double. Every member returns an empty/cancelled
/// result (null, empty list), and both confirmation seams default to <c>false</c>. Tests derive from
/// this and <c>override</c> only the members they actually exercise.
/// </summary>
public class NoOpFileDialogService : IFileDialogService
{
    public virtual Task<string?> OpenFileAsync(string title, IReadOnlyList<string> filters) => Task.FromResult<string?>(null);
    public virtual Task<IReadOnlyList<string>> OpenFilesAsync(string title, IReadOnlyList<string> filters) => Task.FromResult<IReadOnlyList<string>>([]);
    public virtual Task<string?> SaveFileAsync(string title, string defaultExtension, IReadOnlyList<string> filters, string? defaultFileName = null) => Task.FromResult<string?>(null);
    public virtual Task<string?> OpenFolderAsync(string title) => Task.FromResult<string?>(null);
    public virtual Task<bool> ShowConfirmAsync(string title, string message) => Task.FromResult(false);
    public virtual Task<string?> PromptForTextAsync(string title, string message, string initialValue) => Task.FromResult<string?>(null);
    public virtual void ShowError(string title, string message) { }
    public virtual void ShowWarning(string title, string message) { }
    public virtual void ShowInfo(string title, string message) { }
    public virtual bool Confirm(string title, string message) => false;
}

/// <summary>
/// Reusable no-op <see cref="ITempDirectoryService"/> double. <see cref="CreateTempDirectory"/>
/// throws by default — tests that need a real temp directory override it; <see cref="Cleanup"/>
/// is a no-op.
/// </summary>
public class NoOpTempDirectoryService : ITempDirectoryService
{
    public virtual string CreateTempDirectory() => throw new InvalidOperationException("Temp dir should not be created in unit tests.");
    public virtual void Cleanup(string? tempDir) { }
}

/// <summary>
/// Reusable no-op <see cref="IAppSettingsService"/> double: <see cref="Load"/> returns fresh
/// defaults, <see cref="Save"/> does nothing, and <see cref="Changed"/> is inert.
/// </summary>
public class NoOpAppSettingsService : IAppSettingsService
{
    public event EventHandler? Changed { add { } remove { } }
    public virtual AppSettings Load() => new();
    public virtual void Save(AppSettings settings) { }
}

/// <summary>Records every <see cref="IImagePreviewService.Preview"/> call for assertions.</summary>
public sealed class RecordingImagePreviewService : ReScene.NET.Services.IImagePreviewService
{
    public List<(byte[] Data, string FileName)> Calls { get; } = [];

    public void Preview(byte[] data, string fileName) => Calls.Add((data, fileName));
}

/// <summary>Records every <see cref="IFilePreviewService.Preview"/> call for assertions.</summary>
public sealed class RecordingFilePreviewService : ReScene.NET.Services.IFilePreviewService
{
    public List<(byte[] Data, string FileName)> Calls { get; } = [];

    public void Preview(byte[] data, string fileName) => Calls.Add((data, fileName));
}
