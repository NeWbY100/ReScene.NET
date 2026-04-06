namespace ReScene.NET.Helpers;

/// <summary>
/// Centralized file dialog filter definitions used across ViewModels.
/// </summary>
internal static class FileDialogFilters
{
    /// <summary>
    /// Scene files including RAR — for Inspector and Compare views.
    /// </summary>
    public static readonly string[] SceneFilesWithRar =
    [
        "Scene Files|*.srr;*.srs;*.rar",
        "SRR Files|*.srr",
        "SRS Files|*.srs",
        "RAR Files|*.rar",
        "All Files|*.*"
    ];

    /// <summary>
    /// Scene files without RAR — for main window open dialog.
    /// </summary>
    public static readonly string[] SceneFiles =
    [
        "Scene Files|*.srr;*.srs",
        "SRR Files|*.srr",
        "SRS Files|*.srs",
        "All Files|*.*"
    ];

    /// <summary>
    /// SRR Creator input — SFV or RAR files.
    /// </summary>
    public static readonly string[] SfvAndRar =
    [
        "SFV Files|*.sfv",
        "RAR Files|*.rar",
        "All Files|*.*"
    ];

    /// <summary>
    /// Stored files for SRR creation — NFO, SFV, text files.
    /// </summary>
    public static readonly string[] StoredFiles =
    [
        "NFO/SFV Files|*.nfo;*.sfv;*.txt",
        "All Files|*.*"
    ];

    /// <summary>
    /// SRR files only.
    /// </summary>
    public static readonly string[] SrrFiles =
    [
        "SRR Files|*.srr",
        "All Files|*.*"
    ];

    /// <summary>
    /// SRS files only.
    /// </summary>
    public static readonly string[] SrsFiles =
    [
        "SRS Files|*.srs",
        "All Files|*.*"
    ];

    /// <summary>
    /// Media sample files — for SRS Creator input.
    /// </summary>
    public static readonly string[] MediaSamples =
    [
        "Video Samples|*.avi;*.mkv;*.mp4;*.wmv;*.m4v",
        "Audio Samples|*.flac;*.mp3",
        "Stream Samples|*.vob;*.m2ts;*.ts;*.mpg;*.mpeg;*.evo",
        "ISO Images|*.iso;*.img",
        "All Files|*.*"
    ];

    /// <summary>
    /// Media files — for SRS Reconstructor media input.
    /// </summary>
    public static readonly string[] MediaFiles =
    [
        "Video Files|*.avi;*.mkv;*.mp4;*.wmv;*.m4v;*.mov",
        "Audio Files|*.flac;*.mp3",
        "Stream Files|*.vob;*.m2ts;*.ts;*.mpg;*.mpeg;*.evo;*.m2v",
        "ISO Images|*.iso;*.img",
        "All Files|*.*"
    ];

    /// <summary>
    /// Verification files — SFV and SHA1.
    /// </summary>
    public static readonly string[] VerificationFiles =
    [
        "SFV Files|*.sfv",
        "SHA1 Files|*.sha1",
        "All Files|*.*"
    ];

    /// <summary>
    /// SRR save dialog filter.
    /// </summary>
    public static readonly string[] SrrSave = ["SRR Files|*.srr"];

    /// <summary>
    /// SRS save dialog filter.
    /// </summary>
    public static readonly string[] SrsSave = ["SRS Files|*.srs"];

    /// <summary>
    /// All files — for generic save/export dialogs.
    /// </summary>
    public static readonly string[] AllFiles = ["All Files|*.*"];
}
