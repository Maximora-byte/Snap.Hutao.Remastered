using Microsoft.UI.Xaml.Controls;
using System.IO;
using Snap.Hutao.Remastered.Core;
using Snap.Hutao.Remastered.Core.Caching;
using Snap.Hutao.Remastered.Core.IO;
using Snap.Hutao.Remastered.Web.Hoyolab.HoyoPlay;
using Windows.Media.Core;
using Windows.Media.Playback;
using System.Collections.Frozen;
using System.Runtime.InteropServices;

namespace Snap.Hutao.Remastered.Service.BackgroundMediaPlayer;

[Service(ServiceLifetime.Singleton, typeof(IBackgroundMediaPlayerService))]
public sealed partial class BackgroundMediaPlayerService : IBackgroundMediaPlayerService
{
    private static readonly HashSet<string> AllowedVideoFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".m4v", ".mov", ".wmv", ".avi"
    };

    private static readonly FrozenDictionary<string, string> videoUriMapper = new Dictionary<string, string>()
    {
        { "https://launcher-webstatic.mihoyo.com/launcher-public/2026/08/05/8e1c78aaa6e33ed60b88e12a461f8ee5_6759941859929004894.webm", "https://static.snaphutaorp.org/static/launcher/20260812.mp4" }
    }.ToFrozenDictionary();

    private readonly AppOptions appOptions;
    private readonly ILogger<BackgroundMediaPlayerService> logger;
    private readonly IServiceProvider serviceProvider;

    // The player owned by the MediaPlayerElement of the current window. It is only accessed from the
    // UI thread; Stop() detaches it atomically so that it can be disposed exactly once.
    private MediaPlayer? mediaPlayer;

    [GeneratedConstructor]
    public partial BackgroundMediaPlayerService(IServiceProvider serviceProvider);

    public void Pause()
    {
        mediaPlayer?.Pause();
    }

    public void Play()
    {
        mediaPlayer?.Play();
    }

    public void Stop()
    {
        MediaPlayer? player = Interlocked.Exchange(ref mediaPlayer, null);
        if (player is null)
        {
            return;
        }

        try
        {
            player.Source = null;
        }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException)
        {
            // The window, and with it the player, is already being torn down.
            logger.LogWarning(ex, "Failed to clear the source of the background media player");
        }

        try
        {
            player.Dispose();
        }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException)
        {
            // Nothing left to release: the player is already closed.
            logger.LogWarning(ex, "Failed to dispose the background media player");
        }
    }

    public async ValueTask UpdateMediaPlayerElementAsync(MediaPlayerElement element, CancellationToken token = default)
    {
        if (element is null)
        {
            return;
        }

        ITaskContext taskContext = TaskContext.GetForDependencyObject(element);

        await taskContext.SwitchToMainThreadAsync();
        token.ThrowIfCancellationRequested();

        element.AutoPlay = true;
        element.AreTransportControlsEnabled = false;

        if (element.MediaPlayer is not null)
        {
            element.MediaPlayer.IsMuted = appOptions.IsBackgroundMediaMuted.Value;
            element.MediaPlayer.IsLoopingEnabled = appOptions.IsBackgroundMediaLooping.Value;
        }

        mediaPlayer = element.MediaPlayer;

        Uri? videoUri = appOptions.BackgroundMediaType.Value switch
        {
            BackgroundMediaType.LocalFolder => await GetLocalFolderVideoUriAsync(token).ConfigureAwait(false),
            BackgroundMediaType.OfficialLauncher => await GetOfficialLauncherVideoUriAsync(token).ConfigureAwait(false),
            _ => null,
        };

        await taskContext.SwitchToMainThreadAsync();
        token.ThrowIfCancellationRequested();

        element.Source = videoUri is null ? null : MediaSource.CreateFromUri(videoUri);

        // The underlying MediaPlayer is created lazily by the MediaPlayerElement when a source is
        // assigned, so the player can only be reached after the switch above. A background video
        // behaves like a wallpaper and must not appear in the system media transport controls
        // (SMTC) nor react to the hardware media keys.
        DisableSystemMediaTransportControls(element.MediaPlayer);
    }

    private async ValueTask<Uri?> GetLocalFolderVideoUriAsync(CancellationToken token)
    {
        string folder = string.IsNullOrEmpty(appOptions.BackgroundMediaPath.Value) ? HutaoRuntime.GetDataBackgroundDirectory() : appOptions.BackgroundMediaPath.Value!;

        string? selected;
        try
        {
            // Recursively enumerating a user selected folder (possibly a network share) is never
            // cheap enough for the UI thread, and it happens on every refresh.
            selected = await Task.Run(
                () =>
                {
                    if (!Directory.Exists(folder))
                    {
                        return null;
                    }

                    return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                        .Where(static path => AllowedVideoFormats.Contains(Path.GetExtension(path)))
                        .OrderBy(static _ => Guid.NewGuid())
                        .FirstOrDefault();
                },
                token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to enumerate the background media folder '{Folder}'", folder);
            return null;
        }

        if (selected is null)
        {
            return null;
        }

        // Ensure the local file path uses the file:// scheme for MediaSource.
        return new Uri(Path.GetFullPath(selected));
    }

    private async ValueTask<Uri?> GetOfficialLauncherVideoUriAsync(CancellationToken token)
    {
        using (IServiceScope scope = serviceProvider.CreateScope())
        {
            OfficialLauncherClient? launcherClient = scope.ServiceProvider.GetService<OfficialLauncherClient>();
            if (launcherClient is null)
            {
                return null;
            }

            try
            {
                string? videoUrl = await launcherClient.GetBackgroundVideoUrlAsync(token).ConfigureAwait(false);
                if (string.IsNullOrEmpty(videoUrl))
                {
                    return null;
                }

                if (videoUriMapper.TryGetValue(videoUrl, out string? mappedUrl))
                {
                    videoUrl = mappedUrl;
                }

                Uri targetUri = new(videoUrl);

                IImageCache? imageCache = scope.ServiceProvider.GetService<IImageCache>();
                if (imageCache is null)
                {
                    return targetUri;
                }

                return await TryGetCachedVideoUriAsync(imageCache, targetUri).ConfigureAwait(false) ?? targetUri;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to resolve the official launcher background video");
                return null;
            }
        }
    }

    private async ValueTask<Uri?> TryGetCachedVideoUriAsync(IImageCache imageCache, Uri targetUri)
    {
        try
        {
            ValueFile file = await imageCache.GetFileFromCacheAsync(targetUri).ConfigureAwait(false);

            string filePath = file.ToString();
            string cacheDir = HutaoRuntime.GetLocalCacheImageCacheDirectory();
            if (Path.GetFullPath(filePath).StartsWith(Path.GetFullPath(cacheDir), StringComparison.OrdinalIgnoreCase) && File.Exists(filePath))
            {
                return new Uri(filePath);
            }

            try
            {
                imageCache.Remove(targetUri);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to remove the invalid cached background video '{Uri}'", targetUri);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to cache the background video '{Uri}', streaming it instead", targetUri);
        }

        return null;
    }

    private static void DisableSystemMediaTransportControls(MediaPlayer? player)
    {
        if (player is null)
        {
            return;
        }

        player.CommandManager.IsEnabled = false;
    }
}
