using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.System;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace ScdDesktop
{
    internal sealed class HostBackdrop : IDisposable
    {
        private static DispatcherQueueController queueController;
        private Compositor compositor;
        private DesktopWindowTarget target;
        private SpriteVisual surface;
        private CompositionBackdropBrush brush;

        [StructLayout(LayoutKind.Sequential)]
        private struct QueueOptions
        {
            public int Size, ThreadType, ApartmentType;
        }

        [DllImport("CoreMessaging.dll")]
        private static extern int CreateDispatcherQueueController(QueueOptions options, out IntPtr controller);

        [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICompositorDesktopInterop
        {
            void CreateDesktopWindowTarget(IntPtr window, bool topmost, out IntPtr target);
        }

        public static HostBackdrop TryCreate(IntPtr window)
        {
            try { return new HostBackdrop(window); }
            catch (COMException error) { Debug.WriteLine(error); }
            catch (InvalidCastException error) { Debug.WriteLine(error); }
            catch (TypeLoadException error) { Debug.WriteLine(error); }
            return null;
        }

        private HostBackdrop(IntPtr window)
        {
            if (queueController == null && DispatcherQueue.GetForCurrentThread() == null)
            {
                var options = new QueueOptions {
                    Size = Marshal.SizeOf(typeof(QueueOptions)), ThreadType = 2, ApartmentType = 2
                };
                IntPtr rawController;
                Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out rawController));
                try { queueController = (DispatcherQueueController)Marshal.GetObjectForIUnknown(rawController); }
                finally { Marshal.Release(rawController); }
            }

            try
            {
                compositor = new Compositor();
                IntPtr rawTarget;
                ((ICompositorDesktopInterop)(object)compositor).CreateDesktopWindowTarget(window, false, out rawTarget);
                try { target = (DesktopWindowTarget)Marshal.GetObjectForIUnknown(rawTarget); }
                finally { Marshal.Release(rawTarget); }
                brush = compositor.CreateHostBackdropBrush();
                surface = compositor.CreateSpriteVisual();
                surface.RelativeSizeAdjustment = Vector2.One;
                surface.Brush = brush;
                target.Root = surface;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (target != null) target.Dispose();
            if (surface != null) surface.Dispose();
            if (brush != null) brush.Dispose();
            if (compositor != null) compositor.Dispose();
        }
    }
}
