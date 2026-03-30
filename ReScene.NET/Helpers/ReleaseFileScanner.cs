using System.Text.RegularExpressions;

namespace ReScene.NET.Helpers;

/// <summary>
/// Scans a scene release directory for files that should be stored in the SRR.
/// Implements filtering rules based on pyrescene conventions.
/// </summary>
internal static partial class ReleaseFileScanner
{
    // ── Stored file extensions ──────────────────────────────

    private static readonly HashSet<string> StoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nfo", ".sfv", ".m3u", ".cue", ".log", ".srs"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif"
    };

    private static readonly HashSet<string> SampleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avi", ".mkv", ".mp4", ".wmv", ".m4v",
        ".flac", ".mp3",
        ".vob", ".m2ts", ".ts", ".mpg", ".mpeg", ".evo"
    };

    private static readonly HashSet<string> MusicExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".mp2"
    };

    // ── Blacklisted filenames ───────────────────────────────

    private static readonly HashSet<string> NfoBlacklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "imdb.nfo", "tvmaze.nfo", "movie.nfo", "scc.nfo", "motechnetfiles.nfo", "no.nfo"
    };

    private static readonly HashSet<string> LogBlacklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "rushchk.log", ".upchk.log", "ufxpcrc.log"
    };

    // ── Subdirectory names ──────────────────────────────────

    private static readonly HashSet<string> ProofDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "proof", "proofs", "sample", "cover", "covers", "screenshots", "compare"
    };

    private static readonly HashSet<string> SubtitleDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "subs", "vobsubs", "vobsub", "subtitles", "sub", "subpack",
        "vobsubs-full", "vobsubs-light", "czsubs"
    };

    private static readonly HashSet<string> DiscDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "codec", "codecs"
    };

    // ── Fix release detection ───────────────────────────────

    [GeneratedRegex(@"(SFV|PPF|sync|proof?|dir|nfo|Interleaving|Trackorder).?(Fix|Patch)", RegexOptions.IgnoreCase)]
    private static partial Regex FixPattern1();

    [GeneratedRegex(@"\.(FiX|FIX)(\.|-)", RegexOptions.None)]
    private static partial Regex FixPattern2();

    [GeneratedRegex(@"\.DVDR\.(REPACK\.)?Fix-", RegexOptions.IgnoreCase)]
    private static partial Regex FixPattern3();

    [GeneratedRegex(@"^CD\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex CdDirPattern();

    // ── Public methods ──────────────────────────────────────

    /// <summary>
    /// Scans the release directory for files that should be stored in the SRR.
    /// Returns (FullPath, StoredName) tuples sorted appropriately.
    /// </summary>
    /// <param name="releaseDir">The root directory of the scene release.</param>
    /// <returns>A sorted list of file paths and their stored names for the SRR.</returns>
    public static List<(string FullPath, string StoredName)> ScanReleaseDirectory(string releaseDir)
    {
        var files = new List<(string FullPath, string StoredName)>();
        bool isMusicRelease = IsMusicRelease(releaseDir);

        // Scan the release root
        ScanDirectory(releaseDir, releaseDir, files);

        // Scan known subdirectories
        foreach (string subDir in Directory.GetDirectories(releaseDir))
        {
            string dirName = Path.GetFileName(subDir);

            if (ProofDirs.Contains(dirName) || SubtitleDirs.Contains(dirName) ||
                DiscDirs.Contains(dirName) || CdDirPattern().IsMatch(dirName))
            {
                ScanDirectory(subDir, releaseDir, files);
            }
        }

        return SortStoredFiles(files, isMusicRelease);
    }

    /// <summary>
    /// Finds sample media files in the release directory (Sample/ subdir or files with "sample" in name).
    /// </summary>
    /// <param name="releaseDir">The root directory of the scene release.</param>
    /// <returns>A list of sample media file paths.</returns>
    public static List<string> FindSampleFiles(string releaseDir)
    {
        var samples = new List<string>();

        // Check Sample/ subdirectory
        string sampleDir = Path.Combine(releaseDir, "Sample");
        if (Directory.Exists(sampleDir))
        {
            foreach (string file in Directory.GetFiles(sampleDir))
            {
                if (SampleExtensions.Contains(Path.GetExtension(file)))
                {
                    samples.Add(file);
                }
            }
        }

        // Check root for files with "sample" in the name
        foreach (string file in Directory.GetFiles(releaseDir))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name.Contains("sample", StringComparison.OrdinalIgnoreCase) &&
                SampleExtensions.Contains(Path.GetExtension(file)) &&
                !samples.Contains(file))
            {
                samples.Add(file);
            }
        }

        return samples;
    }

    /// <summary>
    /// Finds subtitle SFV files in subtitle subdirectories.
    /// </summary>
    /// <param name="releaseDir">The root directory of the scene release.</param>
    /// <returns>A list of SFV file paths found in subtitle subdirectories.</returns>
    public static List<string> FindSubtitleSfvFiles(string releaseDir)
    {
        var sfvFiles = new List<string>();

        foreach (string subDir in Directory.GetDirectories(releaseDir))
        {
            string dirName = Path.GetFileName(subDir);
            if (SubtitleDirs.Contains(dirName))
            {
                foreach (string file in Directory.GetFiles(subDir, "*.sfv"))
                {
                    sfvFiles.Add(file);
                }
            }
        }

        return sfvFiles;
    }

    /// <summary>
    /// Detects if the release is a fix/patch release by its name.
    /// </summary>
    /// <param name="releaseName">The release directory or folder name to check.</param>
    /// <returns><see langword="true"/> if the name matches a fix/patch pattern.</returns>
    public static bool IsFixRelease(string releaseName)
    {
        return FixPattern1().IsMatch(releaseName)
            || FixPattern2().IsMatch(releaseName)
            || FixPattern3().IsMatch(releaseName);
    }

    /// <summary>
    /// Checks if the release directory contains music files.
    /// </summary>
    /// <param name="releaseDir">The root directory of the scene release.</param>
    /// <returns><see langword="true"/> if music files are found in the release.</returns>
    public static bool IsMusicRelease(string releaseDir)
    {
        foreach (string file in Directory.GetFiles(releaseDir))
        {
            if (MusicExtensions.Contains(Path.GetExtension(file)))
            {
                return true;
            }
        }

        // Check CD subdirectories
        foreach (string subDir in Directory.GetDirectories(releaseDir))
        {
            if (CdDirPattern().IsMatch(Path.GetFileName(subDir)))
            {
                foreach (string file in Directory.GetFiles(subDir))
                {
                    if (MusicExtensions.Contains(Path.GetExtension(file)))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Finds all RAR files referenced by an SFV (lines with .rNN or .rar extensions).
    /// </summary>
    /// <param name="sfvPath">The path to the SFV file.</param>
    /// <returns>A list of existing RAR file paths referenced by the SFV.</returns>
    public static List<string> FindRarFilesFromSfv(string sfvPath)
    {
        string dir = Path.GetDirectoryName(sfvPath) ?? ".";
        var rarFiles = new List<string>();

        foreach (string line in File.ReadLines(sfvPath))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(';'))
            {
                continue;
            }

            int lastSpace = line.LastIndexOf(' ');
            if (lastSpace <= 0)
            {
                continue;
            }

            string fileName = line[..lastSpace].Trim();
            string ext = Path.GetExtension(fileName);

            if (ext.Equals(".rar", StringComparison.OrdinalIgnoreCase) ||
                (ext.Length == 4 && ext.StartsWith(".r", StringComparison.OrdinalIgnoreCase) &&
                 char.IsDigit(ext[2]) && char.IsDigit(ext[3])))
            {
                string fullPath = Path.Combine(dir, fileName);
                if (File.Exists(fullPath))
                {
                    rarFiles.Add(fullPath);
                }
            }
        }

        return rarFiles;
    }

    // ── Private helpers ─────────────────────────────────────

    private static void ScanDirectory(string dir, string releaseDir, List<(string FullPath, string StoredName)> files)
    {
        bool isSubDir = !string.Equals(dir, releaseDir, StringComparison.OrdinalIgnoreCase);
        string dirName = Path.GetFileName(dir);
        bool isProofDir = isSubDir && ProofDirs.Contains(dirName);

        foreach (string file in Directory.GetFiles(dir))
        {
            string ext = Path.GetExtension(file);
            string fileName = Path.GetFileName(file);

            if (StoredExtensions.Contains(ext))
            {
                if (!ShouldIncludeFile(file))
                {
                    continue;
                }

                AddFile(file, releaseDir, files);
            }
            else if (ImageExtensions.Contains(ext))
            {
                if (!ShouldIncludeImage(fileName, isProofDir, isSubDir))
                {
                    continue;
                }

                AddFile(file, releaseDir, files);
            }
        }
    }

    private static bool ShouldIncludeFile(string fullPath)
    {
        string fileName = Path.GetFileName(fullPath);
        string ext = Path.GetExtension(fileName);

        if (ext.Equals(".nfo", StringComparison.OrdinalIgnoreCase))
        {
            return !NfoBlacklist.Contains(fileName);
        }

        if (ext.Equals(".log", StringComparison.OrdinalIgnoreCase))
        {
            if (LogBlacklist.Contains(fileName))
            {
                return false;
            }

            if (fileName.StartsWith('.'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ShouldIncludeImage(string fileName, bool isProofDir, bool isSubDir)
    {
        // Only include images from proof-related subdirectories or with proof-like names
        if (!isProofDir && isSubDir)
        {
            return false;
        }

        // In root dir, only include images with proof-like names
        if (!isSubDir)
        {
            string lower = fileName.ToLowerInvariant();
            if (!lower.Contains("proof"))
            {
                return false;
            }
        }

        return !IsUnwantedImage(fileName);
    }

    private static bool IsUnwantedImage(string fileName)
    {
        string lower = fileName.ToLowerInvariant();

        // Windows Media Player album art
        if (lower.Contains("albumartsmall"))
        {
            return true;
        }

        if (lower.StartsWith("albumart_{", StringComparison.Ordinal))
        {
            return true;
        }

        // "Folder.jpg" type
        string baseName = Path.GetFileNameWithoutExtension(lower);
        if (baseName == "folder")
        {
            return true;
        }

        return false;
    }

    private static void AddFile(string fullPath, string releaseDir, List<(string FullPath, string StoredName)> files)
    {
        // Avoid duplicates
        if (files.Any(f => f.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        string storedName = Path.GetRelativePath(releaseDir, fullPath).Replace('\\', '/');
        files.Add((fullPath, storedName));
    }

    private static List<(string FullPath, string StoredName)> SortStoredFiles(
        List<(string FullPath, string StoredName)> files, bool isMusicRelease)
    {
        // pyrescene order: NFO, m3u, proof images, log, cue, SRS, vobsub SRR,
        // subtitle SFVs (in subdirs), main SFV (root) last
        return files.OrderBy(f =>
        {
            string ext = Path.GetExtension(f.FullPath).ToLowerInvariant();
            bool isInSubDir = f.StoredName.Contains(Path.DirectorySeparatorChar)
                || f.StoredName.Contains('/');

            return ext switch
            {
                ".nfo" => 0,
                ".m3u" => 1,
                _ when ImageExtensions.Contains(ext) => 2,
                ".log" when isMusicRelease => 3,
                ".log" => 4,
                ".cue" => 5,
                ".srs" => 6,
                ".srr" => 7,
                ".sfv" when isInSubDir => 8,
                ".sfv" => 9,
                _ => 4
            };
        }).ThenBy(f => f.StoredName, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
