#if HAS_WINRT_CAMERA
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace Ledjoro
{
    /// <summary>
    /// The first webcam, through Windows' own camera API (the same one the Camera app uses). Nothing is recorded: frames are looked at and thrown away.
    /// Compiled only where the Windows SDK is available; without it the "scan" button simply does not appear.
    /// </summary>
    sealed class Webcam : IDisposable
    {
        MediaCapture capture; MediaFrameReader reader;

        public async Task StartAsync()
        {
            var groups = await MediaFrameSourceGroup.FindAllAsync();
            var group = groups.FirstOrDefault(g => g.SourceInfos.Any(i => i.SourceKind == MediaFrameSourceKind.Color));
            if (group == null) throw new InvalidOperationException("No camera was found on this computer.");
            capture = new MediaCapture();
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = group, SharingMode = MediaCaptureSharingMode.SharedReadOnly, MemoryPreference = MediaCaptureMemoryPreference.Cpu, StreamingCaptureMode = StreamingCaptureMode.Video,
            });
            var source = capture.FrameSources.Values.First(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
            // a medium size is plenty for a QR code and keeps reading fast: the one closest to 1280 x 720
            var best = source.SupportedFormats.Where(f => f.VideoFormat.Width >= 640).OrderBy(f => Math.Abs((int)f.VideoFormat.Width - 1280) + Math.Abs((int)f.VideoFormat.Height - 720)).FirstOrDefault();
            if (best != null) { try { await source.SetFormatAsync(best); } catch (Exception) { /* keep the default */ } }
            reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            var status = await reader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success) throw new InvalidOperationException("The camera would not start (" + status + ").");
        }

        /// <summary>The newest picture from the camera, or null if there is not one yet.</summary>
        public Bitmap TryGetFrame()
        {
            if (reader == null) return null;
            using (var fr = reader.TryAcquireLatestFrame())
            {
                var sb = fr == null || fr.VideoMediaFrame == null ? null : fr.VideoMediaFrame.SoftwareBitmap;
                if (sb == null) return null;
                using (var conv = sb.BitmapPixelFormat == BitmapPixelFormat.Bgra8 ? sb : SoftwareBitmap.Convert(sb, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied))
                {
                    int w = conv.PixelWidth, h = conv.PixelHeight;
                    var buf = new Windows.Storage.Streams.Buffer((uint)(w * h * 4)); conv.CopyToBuffer(buf);
                    byte[] bytes = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(buf);
                    var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                    var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    try { for (int y = 0; y < h; y++) Marshal.Copy(bytes, y * w * 4, data.Scan0 + y * data.Stride, w * 4); } finally { bmp.UnlockBits(data); }
                    return bmp;
                }
            }
        }

        public void Dispose()
        {
            try { if (reader != null) reader.StopAsync().AsTask().Wait(2000); } catch (Exception) { }
            try { if (reader != null) reader.Dispose(); } catch (Exception) { }
            try { if (capture != null) capture.Dispose(); } catch (Exception) { }
            reader = null; capture = null;
        }

        /// <summary>Why the camera could not be used, in plain words, and what to do.</summary>
        public static string Explain(Exception e)
        {
            if (e is UnauthorizedAccessException)
                return "Windows is not letting Ledjoro use the camera. Open Windows Settings, then Privacy & security, then Camera, and turn on “Let desktop apps access your camera”. Then try again. (You can also type the recovery key instead.)";
            string m = e.Message ?? "";
            if (m.StartsWith("No camera")) return "No camera was found on this computer. Plug in a webcam and try again, or type the recovery key instead.";
            uint hr = unchecked((uint)e.HResult);
            if (hr == 0xC00D3704 || hr == 0x8007001F || hr == 0xC00D3E85 || m.IndexOf("in use", StringComparison.OrdinalIgnoreCase) >= 0)
                return "The camera is being used by another program (such as a video call). Close that program and try again.";
            return "The camera could not be started (" + (m.Length > 160 ? m.Substring(0, 160) + "…" : m) + "). Check that it is plugged in and not being used by another program, or type the recovery key instead.";
        }
    }
}
#endif
