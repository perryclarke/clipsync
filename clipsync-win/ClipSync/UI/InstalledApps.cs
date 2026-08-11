using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using ClipSync.Settings;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClipSync.UI;

/// `IconPng` is raw PNG bytes, not a WinUI type: `Enumerate` runs on a
/// background thread, and `BitmapImage` has hard UI-thread affinity. Convert
/// with `InstalledApps.ToImageSource` on the UI thread when displaying.
public sealed record InstalledApp(AppIdentity Identity, byte[]? IconPng);

/// Enumerates the shell's AppsFolder — the same list Start > All apps
/// shows, covering both desktop and Store apps.
///
/// Each child's parent-relative parsing name is its AUMID. Store apps use
/// `PackageFamilyName!AppId`; desktop entries are backed by a Start Menu
/// shortcut whose target executable we read from PKEY_Link_TargetParsing.
public static class InstalledApps
{
    /// Blocking: enumerating and rasterising a few hundred icons takes
    /// roughly 200-500 ms. Call from a background thread. No WinUI types are
    /// touched here — see `InstalledApp.IconPng`.
    public static IReadOnlyList<InstalledApp> Enumerate(CancellationToken ct = default)
    {
        var results = new List<InstalledApp>();
        var seen = new HashSet<AppIdentity>();

        try
        {
            var iid = typeof(IShellItem).GUID;
            if (SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero, ref iid, out var folder) != 0
                || folder is null)
            {
                Security.Identity.Log("InstalledApps: could not open AppsFolder");
                return results;
            }

            var enumIid = typeof(IEnumShellItems).GUID;
            folder.BindToHandler(IntPtr.Zero, BHID_EnumItems, ref enumIid, out var enumObj);
            if (enumObj is not IEnumShellItems items)
            {
                Security.Identity.Log("InstalledApps: AppsFolder returned no enumerator");
                return results;
            }

            var buffer = new IShellItem[1];
            while (!ct.IsCancellationRequested && items.Next(1, buffer, out var fetched) == 0 && fetched == 1)
            {
                var item = buffer[0];
                try
                {
                    var identity = IdentityOf(item);
                    if (identity is null || !seen.Add(identity)) continue;
                    results.Add(new InstalledApp(identity, IconOf(item)));
                }
                catch (Exception ex)
                {
                    Security.Identity.Log($"InstalledApps: skipped an entry: {ex.GetType().Name}");
                }
                finally { Marshal.ReleaseComObject(item); }
            }
        }
        catch (Exception ex)
        {
            Security.Identity.Log($"InstalledApps: enumeration failed: {ex.GetType().Name}");
        }

        results.Sort((a, b) => string.Compare(a.Identity.DisplayName, b.Identity.DisplayName,
                                              StringComparison.CurrentCultureIgnoreCase));
        return results;
    }

    /// Build an Exe identity from a path the user browsed to.
    public static AppIdentity FromExecutable(string exePath)
    {
        string name;
        try
        {
            var desc = FileVersionInfo.GetVersionInfo(exePath).FileDescription;
            name = string.IsNullOrWhiteSpace(desc) ? Path.GetFileNameWithoutExtension(exePath) : desc!;
        }
        catch { name = Path.GetFileNameWithoutExtension(exePath); }

        return new AppIdentity(AppKind.Exe, exePath, name, exePath);
    }

    private static AppIdentity? IdentityOf(IShellItem item)
    {
        item.GetDisplayName(SIGDN.NORMALDISPLAY, out var displayPtr);
        var display = Marshal.PtrToStringUni(displayPtr) ?? "";
        Marshal.FreeCoTaskMem(displayPtr);

        item.GetDisplayName(SIGDN.PARENTRELATIVEPARSING, out var aumidPtr);
        var aumid = Marshal.PtrToStringUni(aumidPtr) ?? "";
        Marshal.FreeCoTaskMem(aumidPtr);

        if (string.IsNullOrWhiteSpace(display) || string.IsNullOrWhiteSpace(aumid)) return null;

        // Store app: PackageFamilyName!AppId
        var bang = aumid.IndexOf('!');
        if (bang > 0) return new AppIdentity(AppKind.Package, aumid[..bang], display);

        // Desktop app: resolve the backing shortcut's target executable.
        var target = TargetOf(item);
        if (target is null || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        return new AppIdentity(AppKind.Exe, target, display, target);
    }

    private static string? TargetOf(IShellItem item)
    {
        if (item is not IShellItem2 item2) return null;
        try
        {
            var key = PKEY_Link_TargetParsing;
            return item2.GetString(ref key, out var value) == 0 ? value : null;
        }
        catch { return null; }
    }

    /// Rasterises the shell item's 32px icon to PNG bytes.
    ///
    /// `GetImage` returns an HBITMAP backed by a top-down 32bpp
    /// premultiplied-ARGB DIB section. `Image.FromHbitmap` would discard
    /// that alpha channel (yielding `Format32bppRgb` — transparent pixels
    /// bake in as black), so instead we read the DIB's raw bits via
    /// `GetObject`/`BITMAP` and wrap them as `Format32bppPArgb`, then clone
    /// into GDI+-owned memory before the HBITMAP (and its backing bits) are
    /// freed in `finally`.
    private static byte[]? IconOf(IShellItem item)
    {
        if (item is not IShellItemImageFactory factory) return null;
        var hbitmap = IntPtr.Zero;
        try
        {
            if (factory.GetImage(new SIZE { cx = 32, cy = 32 }, SIIGBF.ICONONLY, out hbitmap) != 0
                || hbitmap == IntPtr.Zero)
                return null;

            var info = new BITMAP();
            if (GetObject(hbitmap, Marshal.SizeOf<BITMAP>(), ref info) == 0 || info.bmBits == IntPtr.Zero)
                return null;

            using var view = new System.Drawing.Bitmap(info.bmWidth, info.bmHeight, info.bmWidthBytes,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb, info.bmBits);
            using var owned = new System.Drawing.Bitmap(view); // deep-copies pixel data
            using var ms = new MemoryStream();
            owned.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }
        catch { return null; }
        finally { if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap); }
    }

    /// Icon PNG bytes for browsed executables, which have no shell item.
    /// Background-thread safe, like `Enumerate`.
    public static byte[]? IconBytesForExecutable(string exePath)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon is null) return null;
            using var bmp = icon.ToBitmap();
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }
        catch { return null; }
    }

    /// Converts icon PNG bytes into a WinUI `ImageSource`.
    ///
    /// UI THREAD ONLY: `BitmapImage` is a `DependencyObject` with hard
    /// UI-thread affinity. This is the single conversion point between the
    /// background-thread-safe byte[] results of `Enumerate`/
    /// `IconBytesForExecutable` and the visual tree.
    public static ImageSource? ToImageSource(byte[]? png)
    {
        if (png is null || png.Length == 0) return null;
        try
        {
            // Synchronous wrap — no WinRT async operation is involved, so
            // there is nothing to block the UI thread on (unlike the
            // DataWriter/InMemoryRandomAccessStream route this replaced,
            // which blocked on StoreAsync().GetAwaiter().GetResult()).
            //
            // Deliberately NOT disposed: SetSource's synchronous signature
            // does not guarantee the decode has finished reading the stream
            // by the time it returns — decoding completes asynchronously
            // under the hood. Disposing here races the decoder and can hit
            // a disposed stream, failing the image load. The BitmapImage
            // holds the only reference; it (and the stream) become
            // collectible once the image itself is no longer referenced.
            var stream = new MemoryStream(png).AsRandomAccessStream();
            var image = new BitmapImage();
            image.SetSource(stream);
            return image;
        }
        catch { return null; }
    }

    // ---- COM interop ----

    private static readonly Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");

    private static PROPERTYKEY PKEY_Link_TargetParsing => new()
    {
        fmtid = new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"),
        pid = 2,
    };

    private enum SIGDN : uint
    {
        NORMALDISPLAY = 0x00000000,
        PARENTRELATIVEPARSING = 0x80018001,
    }

    [Flags]
    private enum SIIGBF { RESIZETOFIT = 0x00, ICONONLY = 0x04 }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid,
                           ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        void GetParent(out IShellItem parent);
        void GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int order);
    }

    [ComImport, Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2 : IShellItem
    {
        new void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid,
                               ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        new void GetParent(out IShellItem parent);
        new void GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);
        new void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        new void Compare(IShellItem psi, uint hint, out int order);

        void GetPropertyStore(uint flags, ref Guid riid, out IntPtr ppv);
        void GetPropertyStoreWithCreateObject(uint flags, [MarshalAs(UnmanagedType.IUnknown)] object punk,
                                              ref Guid riid, out IntPtr ppv);
        void GetPropertyStoreForKeys(IntPtr rgKeys, uint cKeys, uint flags, ref Guid riid, out IntPtr ppv);
        void GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
        void Update(IntPtr pbc);
        void GetProperty(ref PROPERTYKEY key, out IntPtr ppropvar);
        void GetCLSID(ref PROPERTYKEY key, out Guid pclsid);
        void GetFileTime(ref PROPERTYKEY key, out long pft);
        void GetInt32(ref PROPERTYKEY key, out int pi);
        [PreserveSig] int GetString(ref PROPERTYKEY key, [MarshalAs(UnmanagedType.LPWStr)] out string ppsz);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig] int Next(uint celt, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IShellItem[] rgelt,
                               out uint pceltFetched);
        [PreserveSig] int Skip(uint celt);
        [PreserveSig] int Reset();
        void Clone(out IEnumShellItems ppenum);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem? item);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, ref BITMAP lpvObject);
}
