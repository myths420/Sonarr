using System;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Processes;

namespace NzbDrone.Core.AnimeSite
{
    public interface ISiteFileRepairService
    {
        // Re-mux a stream-downloaded MP4 in place with clean container flags
        // (faststart moov, no edit list, zeroed timestamps) so it direct-plays
        // on clients that choked on the pre-fix remux. Returns true when the
        // file was rewritten; false means it couldn't be fixed by re-muxing
        // (truncated / unreadable) and the caller should re-download.
        bool TryRepairInPlace(string path);
    }

    public class SiteFileRepairService : ISiteFileRepairService
    {
        private readonly IProcessProvider _processProvider;
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        public SiteFileRepairService(IProcessProvider processProvider, IDiskProvider diskProvider, Logger logger)
        {
            _processProvider = processProvider;
            _diskProvider = diskProvider;
            _logger = logger;
        }

        public bool TryRepairInPlace(string path)
        {
            if (string.IsNullOrEmpty(path) || !_diskProvider.FileExists(path))
            {
                return false;
            }

            var original = _diskProvider.GetFileSize(path);
            var tmp = path + ".repair.mp4";
            _diskProvider.DeleteFile(tmp);

            var args =
                $"-hide_banner -loglevel error -y -ignore_editlist 1 -i \"{path}\" " +
                "-map 0 -c copy -movflags +faststart -avoid_negative_ts make_zero " +
                $"\"{tmp}\"";

            ProcessOutput result = null;
            try
            {
                result = _processProvider.StartAndCapture("ffmpeg", args);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Sites repair: ffmpeg not runnable for '{0}'", path);
            }

            var ok = result != null && result.ExitCode == 0 && _diskProvider.FileExists(tmp);
            var newSize = ok ? _diskProvider.GetFileSize(tmp) : 0;

            // A clean re-mux is within a few percent of the original. A much
            // smaller output means the source was truncated -- re-download.
            if (!ok || newSize < original * 0.85)
            {
                _diskProvider.DeleteFile(tmp);
                _logger.Debug("Sites repair: '{0}' not re-muxable (exit {1}, {2} -> {3} bytes)", path, result?.ExitCode, original, newSize);
                return false;
            }

            _diskProvider.MoveFile(tmp, path, true);
            _logger.Info("Sites repair: re-muxed '{0}'", path);
            return true;
        }
    }
}
