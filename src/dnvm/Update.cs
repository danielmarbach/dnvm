using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Semver;
using Serde.Json;
using static Dnvm.Update.Result;

namespace Dnvm;

public sealed partial class Update
{
    private readonly Logger _logger;
    private readonly CommandArguments.UpdateArguments _args;
    private readonly string _feedUrl;
    private readonly GlobalOptions _globalOptions;
    private readonly string _manifestPath;
    private readonly string _sdkInstallDir;

    public const string DefaultReleasesUrl = "https://commentout.com/dnvm/releases.json";

    public Update(GlobalOptions options, Logger logger, CommandArguments.UpdateArguments args)
    {
        _globalOptions = options;
        _logger = logger;
        _args = args;
        if (_args.Verbose)
        {
            _logger.LogLevel = LogLevel.Info;
        }
        _feedUrl = _args.FeedUrl ?? GlobalOptions.DotnetFeedUrl;
        if (_feedUrl[^1] == '/')
        {
            _feedUrl = _feedUrl[..^1];
        }
        _manifestPath = options.ManifestPath;
        _sdkInstallDir = options.SdkInstallDir;
    }

    public static Task<Result> Run(GlobalOptions options, Logger logger, CommandArguments.UpdateArguments args)
    {
        return new Update(options, logger, args).Run();
    }

    public enum Result
    {
        Success,
        CouldntFetchIndex,
        NotASingleFile,
        SelfUpdateFailed
    }

    public async Task<Result> Run()
    {
        if (_args.Self)
        {
            return await UpdateSelf();
        }

        DotnetReleasesIndex releaseIndex;
        try
        {
            releaseIndex = await DotnetReleasesIndex.FetchLatestIndex(_feedUrl);
        }
        catch (Exception e)
        {
            _logger.Error("Could not fetch the releases index: ");
            _logger.Error(e.Message);
            return CouldntFetchIndex;
        }

        var manifest = ManifestUtils.ReadOrCreateManifest(_manifestPath);
        _logger.Log("Looking for available updates");
        var updateResults = FindPotentialUpdates(manifest, releaseIndex);
        if (updateResults.Count > 0)
        {
            _logger.Log("Found versions available for update");
            _logger.Log("Channel\tInstalled\tAvailable");
            _logger.Log("-------------------------------------------------");
            foreach (var (c, newestInstalled, newestAvailable) in updateResults)
            {
                _logger.Log($"{c}\t{newestInstalled}\t{newestAvailable.LatestSdk}");
            }
            _logger.Log("Install updates? [y/N]: ");
            var response = _args.Yes ? "y" : Console.ReadLine();
            if (response?.Trim().ToLowerInvariant() == "y")
            {
                foreach (var (c, _, newestAvailable) in updateResults)
                {
                    _ = await Install.InstallSdk(
                        _logger,
                        c,
                        newestAvailable.LatestSdk,
                        Utilities.CurrentRID,
                        _feedUrl,
                        manifest,
                        _manifestPath,
                        _sdkInstallDir
                        );
                }
            }
        }
        return Success;
    }

    public static List<(Channel TrackedChannel, SemVersion NewestInstalled, DotnetReleasesIndex.Release NewestAvailable)> FindPotentialUpdates(
        Manifest manifest,
        DotnetReleasesIndex releaseIndex)
    {
        var list = new List<(Channel, SemVersion, DotnetReleasesIndex.Release)>();
        foreach (var tracked in manifest.TrackedChannels)
        {
            var newestInstalled = tracked.InstalledSdkVersions
                .Select(v => SemVersion.Parse(v, SemVersionStyles.Strict))
                .Max(SemVersion.PrecedenceComparer)!;
            var release = releaseIndex.GetLatestReleaseForChannel(tracked.ChannelName);
            if (release is { LatestSdk: var sdkVersion} &&
                SemVersion.TryParse(sdkVersion, SemVersionStyles.Strict, out var newestAvailable) &&
                SemVersion.ComparePrecedence(newestInstalled, newestAvailable) < 0)
            {
                list.Add((tracked.ChannelName, newestInstalled!, release));
            }
        }
        return list;
    }

    private async Task<Result> UpdateSelf()
    {
        if (!Utilities.IsSingleFile)
        {
            _logger.Error("Cannot self-update: the current executable is not deployed as a single file.");
            return Result.NotASingleFile;
        }

        string artifactDownloadLink = await GetReleaseLink();

        string tempArchiveDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        async Task HandleDownload(string tempDownloadPath)
        {
            _logger.Info("Extraction directory: " + tempArchiveDir);
            string? retMsg = await Utilities.ExtractArchiveToDir(tempDownloadPath, tempArchiveDir);
            if (retMsg != null)
            {
                _logger.Error("Extraction failed: " + retMsg);
            }
        }

        await DownloadBinaryToTempAndDelete(artifactDownloadLink, HandleDownload);
        _logger.Info($"{tempArchiveDir} contents: {string.Join(", ", Directory.GetFiles(tempArchiveDir))}");

        string dnvmTmpPath = Path.Combine(tempArchiveDir, Utilities.ExeName);
        bool success =
            await ValidateBinary(_logger, dnvmTmpPath) &&
            SwapWithRunningFile(dnvmTmpPath);
        return success ? Success : SelfUpdateFailed;
    }

    public async Task<string> GetReleaseLink()
    {
        var releasesUrl = _args.FeedUrl ?? DefaultReleasesUrl;
        string releasesJson = await Program.HttpClient.GetStringAsync(releasesUrl);
        _logger.Info("Releases JSON: " + releasesJson);
        var releases = JsonSerializer.Deserialize<Releases>(releasesJson);
        // Dnvm doesn't currently publish ARM64 binaries for any platform
        var rid = (Utilities.CurrentRID with {
            Arch = Architecture.X64
        }).ToString();
        var artifactDownloadLink = releases.LatestVersion.Artifacts[rid];
        _logger.Info("Artifact download link: " + artifactDownloadLink);
        return artifactDownloadLink;
    }

    private async Task DownloadBinaryToTempAndDelete(string uri, Func<string, Task> action)
    {
        string tempDownloadPath = Path.GetTempFileName();
        using (var tempFile = new FileStream(
            tempDownloadPath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024 /* 64kB */,
            FileOptions.WriteThrough))
        {
            using var archiveHttpStream = await Program.HttpClient.GetStreamAsync(uri);
            await archiveHttpStream.CopyToAsync(tempFile);
            await tempFile.FlushAsync();
        }
        await action(tempDownloadPath);
    }

    public static async Task<bool> ValidateBinary(Logger logger, string fileName)
    {
        // Replace with File.SetUnixFileMode when available
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var chmod = Process.Start("chmod", $"+x \"{fileName}\"");
            await chmod.WaitForExitAsync();
            logger.Info("chmod return: " + chmod.ExitCode);
        }

        // Run exe and make sure it's OK
        var testProc = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            ArgumentList = { "--help" },
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        if (testProc is Process ps)
        {
            await testProc.WaitForExitAsync();
            var output = await ps.StandardOutput.ReadToEndAsync();
            string error = await ps.StandardError.ReadToEndAsync();
            const string usageString = "usage: ";
            if (ps.ExitCode != 0)
            {
                logger.Error("Could not run downloaded dnvm:");
                logger.Error(error);
                return false;
            }
            else if (!output.Contains(usageString))
            {
                logger.Error($"Downloaded dnvm did not contain \"{usageString}\": ");
                logger.Log(output);
                return false;
            }
            return true;
        }
        return false;
    }

    public bool SwapWithRunningFile(string newFileName)
    {
        try
        {
            string backupPath = Utilities.ProcessPath + ".bak";
            _logger.Info($"Swapping {Utilities.ProcessPath} with downloaded version at {newFileName}");
            File.Move(Utilities.ProcessPath, backupPath, overwrite: true);
            File.Move(newFileName, Utilities.ProcessPath, overwrite: false);
            _logger.Log("Process successfully upgraded");
            if (Environment.OSVersion.Platform == PlatformID.Unix)
            {
                // Can't delete the open file on Windows
                File.Delete(backupPath);
            }
            return true;
        }
        catch (Exception e)
        {
            _logger.Error("Couldn't replace existing binary: " + e.Message);
            return false;
        }
    }
}