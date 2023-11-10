
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semver;
using Serde.Json;
using Zio;
using static Dnvm.Utilities;

namespace Dnvm;

public sealed class TrackCommand
{
    private readonly DnvmEnv _env;
    // Place to install dnvm
    private readonly SdkDirName _sdkDir;

    private readonly Logger _logger;
    private readonly CommandArguments.TrackArguments _installArgs;
    private readonly string _feedUrl;

    public enum Result
    {
        Success = 0,
        CouldntFetchLatestVersion,
        InstallLocationNotWritable,
        NotASingleFile,
        ExtractFailed,
        SelfInstallFailed,
        ManifestIOError,
        ManifestFileCorrupted,
        ChannelAlreadyTracked,
        CouldntFetchIndex
    }

    public TrackCommand(DnvmEnv env, Logger logger, CommandArguments.TrackArguments args)
    {
        _env = env;
        _logger = logger;
        _installArgs = args;
        if (_installArgs.Verbose)
        {
            _logger.LogLevel = LogLevel.Info;
        }
        _feedUrl = _installArgs.FeedUrl ?? env.DotnetFeedUrl;
        if (_feedUrl[^1] == '/')
        {
            _feedUrl = _feedUrl[..^1];
        }
        // Use an explicit SdkDir if specified, otherwise, only the preview channel is isolated by
        // default.
        _sdkDir = args.SdkDir switch {
            {} sdkDir => new SdkDirName(sdkDir),
            _ => DnvmEnv.DefaultSdkDirName
        };
    }

    public static Task<Result> Run(DnvmEnv env, Logger logger, CommandArguments.TrackArguments args)
    {
        return new TrackCommand(env, logger, args).Run();
    }

    public async Task<Result> Run()
    {
        var dnvmHome = _env.RealPath(UPath.Root);
        var sdkInstallPath = Path.Combine(dnvmHome, _sdkDir.Name);
        _logger.Info("Install Directory: " + dnvmHome);
        _logger.Info("SDK install directory: " + sdkInstallPath);
        try
        {
            Directory.CreateDirectory(dnvmHome);
            Directory.CreateDirectory(sdkInstallPath);
        }
        catch (UnauthorizedAccessException)
        {
            _logger.Error($"Cannot write to install location. Ensure you have appropriate permissions.");
            return Result.InstallLocationNotWritable;
        }

        return await InstallLatestFromChannel(
            _env,
            _logger,
            _installArgs.Channel,
            _installArgs.Force,
            _feedUrl,
            _sdkDir);
    }

    internal static async Task<Result> InstallLatestFromChannel(
        DnvmEnv dnvmFs,
        Logger logger,
        Channel channel,
        bool force,
        string feedUrl,
        SdkDirName sdkDir)
    {
        Manifest manifest;
        try
        {
            manifest = await ManifestUtils.ReadOrCreateManifest(dnvmFs);
        }
        catch (InvalidDataException)
        {
            logger.Error("Manifest file corrupted");
            return Result.ManifestFileCorrupted;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.Error("Error reading manifest file: " + e.Message);
            return Result.ManifestIOError;
        }

        if (manifest.TrackedChannels.Any(c => c.ChannelName == channel))
        {
            logger.Log($"Channel '{channel}' is already being tracked." +
                " Did you mean to run 'dnvm update'?");
            return Result.ChannelAlreadyTracked;
        }

        manifest = manifest with {
            TrackedChannels = manifest.TrackedChannels.Add(new TrackedChannel {
                ChannelName = channel,
                SdkDirName = sdkDir,
                InstalledSdkVersions = EqArray<SemVersion>.Empty
            })
        };

        DotnetReleasesIndex versionIndex;
        try
        {
            versionIndex = await DotnetReleasesIndex.FetchLatestIndex(feedUrl);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.Error("Could not fetch the releases index: ");
            logger.Error(e.Message);
            return Result.CouldntFetchIndex;
        }

        RID rid = Utilities.CurrentRID;

        var latestChannelIndex = versionIndex.GetChannelIndex(channel);
        if (latestChannelIndex is null)
        {
            logger.Error("Could not fetch the latest package version");
            return Result.CouldntFetchLatestVersion;
        }
        var latestSdkVersion = SemVersion.Parse(latestChannelIndex.LatestSdk, SemVersionStyles.Strict);
        logger.Log("Found latest version: " + latestSdkVersion);

        if (!force && manifest.InstalledSdkVersions.Any(s => s.SdkVersion == latestSdkVersion))
        {
            logger.Log($"Version {latestSdkVersion} is already installed." +
                " Skipping installation. To install anyway, pass --force.");
            return Result.Success;
        }

        var release = JsonSerializer.Deserialize<ChannelReleaseIndex>(
            await Program.HttpClient.GetStringAsync(latestChannelIndex.ChannelReleaseIndexUrl))
            .Releases.Single(r => r.Sdk.Version == latestSdkVersion);

        var installResult = await InstallSdkVersionFromChannel(
            dnvmFs,
            logger,
            latestSdkVersion,
            rid,
            feedUrl,
            manifest,
            sdkDir);

        if (installResult != Result.Success)
        {
            return installResult;
        }

        logger.Info($"Adding installed version '{latestSdkVersion}' to manifest.");
        manifest = manifest with { InstalledSdkVersions = manifest.InstalledSdkVersions.Add(new InstalledSdk {
            ReleaseVersion = release.ReleaseVersion,
            RuntimeVersion = release.Runtime.Version,
            AspNetVersion = release.AspNetCore.Version,
            Channel = channel,
            SdkVersion = latestSdkVersion,
            SdkDirName = sdkDir,
        }) };
        var oldTracked = manifest.TrackedChannels.First(t => t.ChannelName == channel);
        var newTracked = oldTracked with {
            InstalledSdkVersions = oldTracked.InstalledSdkVersions.Add(latestSdkVersion)
        };
        manifest = manifest with { TrackedChannels = manifest.TrackedChannels.Replace(oldTracked, newTracked) };

        logger.Info("Writing manifest");
        dnvmFs.WriteManifest(manifest);

        logger.Log("Successfully installed");

        return Result.Success;
    }

    public static async Task<Result> InstallSdkVersionFromChannel(
        DnvmEnv dnvmFs,
        Logger logger,
        SemVersion latestVersion,
        RID rid,
        string feedUrl,
        Manifest manifest,
        SdkDirName sdkDirName)
    {
        var sdkInstallPath = UPath.Root / sdkDirName.Name;
        var latestVersionString = latestVersion.ToString();
        string archiveName = ConstructArchiveName(latestVersionString, rid, Utilities.ZipSuffix);
        using var tempDir = new DirectoryResource(Directory.CreateTempSubdirectory().FullName);
        string archivePath = Path.Combine(tempDir.Path, archiveName);
        logger.Info("Archive path: " + archivePath);

        var link = ConstructDownloadLink(feedUrl, latestVersionString, archiveName);
        logger.Info("Download link: " + link);

        var result = JsonSerializer.Serialize(manifest);
        logger.Info("Existing manifest: " + result);

        logger.Log("Downloading dotnet SDK...");

        using (var tempArchiveFile = File.Create(archivePath, 64 * 1024 /* 64kB */, FileOptions.WriteThrough))
        using (var archiveResponse = await Program.HttpClient.GetAsync(link))
        using (var archiveHttpStream = await archiveResponse.Content.ReadAsStreamAsync())
        {
            if (!archiveResponse.IsSuccessStatusCode)
            {
                logger.Error("Failed archive response");
                logger.Error(await archiveResponse.Content.ReadAsStringAsync());
            }
            await archiveHttpStream.CopyToAsync(tempArchiveFile);
            await tempArchiveFile.FlushAsync();
        }
        logger.Log($"Installing to {sdkInstallPath}");
        string? extractResult = await Utilities.ExtractArchiveToDir(archivePath, dnvmFs, sdkInstallPath);
        File.Delete(archivePath);
        if (extractResult != null)
        {
            logger.Error("Extract failed: " + extractResult);
            return Result.ExtractFailed;
        }

        var dotnetExePath = sdkInstallPath / Utilities.DotnetExeName;
        if (!OperatingSystem.IsWindows())
        {
            logger.Info("chmoding downloaded host");
            try
            {
                Utilities.ChmodExec(dnvmFs.Vfs, dotnetExePath);
            }
            catch (Exception e)
            {
                logger.Error("chmod failed: " + e.Message);
                return Result.ExtractFailed;
            }
        }
        CreateSymlinkIfMissing(dnvmFs, sdkDirName);

        return Result.Success;
    }

    static string ConstructArchiveName(
        string? specificVersion,
        RID rid,
        string suffix)
    {
        return specificVersion is null
            ? $"dotnet-sdk-{rid}{suffix}"
            : $"dotnet-sdk-{specificVersion}-{rid}{suffix}";
    }

    static string ConstructDownloadLink(string feed, string latestVersion, string archiveName)
    {
        return $"{feed}/Sdk/{latestVersion}/{archiveName}";
    }

    /// <summary>
    /// Creates a symlink from the dotnet exe in the dnvm home directory to the dotnet exe in the
    /// sdk install directory.
    /// </summary>
    /// <remarks>
    /// Doesn't use a symlink on Windows because the dotnet muxer doesn't properly resolve through
    /// symlinks.
    /// </remarks>
    internal static void RetargetSymlink(DnvmEnv dnvmFs, SdkDirName sdkDirName)
    {
        var dnvmHome = dnvmFs.Vfs.ConvertPathToInternal(UPath.Root);
        RetargetSymlink(dnvmHome, sdkDirName);

        static void RetargetSymlink(string dnvmHome, SdkDirName sdkDirName)
        {
            var symlinkPath = Path.Combine(dnvmHome, DotnetSymlinkName);
            var sdkInstallDir = Path.Combine(dnvmHome, sdkDirName.Name);
            // Delete if it already exists
            try
            {
                File.Delete(symlinkPath);
            }
            catch { }
            if (OperatingSystem.IsWindows())
            {
                // On Windows, we can't create a symlink, so create a .cmd file that calls the dotnet.exe
                File.WriteAllText(symlinkPath, $"""
    @echo off
    "%~dp0{sdkDirName.Name}\{DotnetExeName}" %*
    """);
            }
            else
            {
                // On Unix, we can create a symlink
                File.CreateSymbolicLink(symlinkPath, Path.Combine(sdkInstallDir, DotnetExeName));
            }
        }
    }

    private static void CreateSymlinkIfMissing(DnvmEnv dnvmFs, SdkDirName sdkDirName)
    {
        var symlinkPath = dnvmFs.Vfs.ConvertPathToInternal(UPath.Root + DotnetSymlinkName);
        if (!File.Exists(symlinkPath))
        {
            RetargetSymlink(dnvmFs, sdkDirName);
        }
    }

}