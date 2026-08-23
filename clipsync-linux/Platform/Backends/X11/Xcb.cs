using System;
using System.Runtime.InteropServices;

namespace ClipSync.Platform.Backends.X11;

/// Minimal XCB binding — enough for selection ownership, conversion and
/// XFixes selection-change notification.
///
/// XCB rather than Xlib deliberately: Xlib's fatal IO error path ends in
/// exit(1) and can only be escaped by longjmp-ing out of a callback, which
/// is not something a managed daemon can do safely. XCB reports the same
/// condition as xcb_connection_has_error() and lets us reconnect.
internal static unsafe class Xcb
{
    private const string Lib = "libxcb.so.1";
    private const string LibXfixes = "libxcb-xfixes.so.0";

    // ---- connection -------------------------------------------------

    [DllImport(Lib)] public static extern IntPtr xcb_connect(string? displayname, int* screenp);
    [DllImport(Lib)] public static extern int xcb_connection_has_error(IntPtr c);
    [DllImport(Lib)] public static extern void xcb_disconnect(IntPtr c);
    [DllImport(Lib)] public static extern int xcb_flush(IntPtr c);
    [DllImport(Lib)] public static extern int xcb_get_file_descriptor(IntPtr c);
    [DllImport(Lib)] public static extern uint xcb_generate_id(IntPtr c);
    [DllImport(Lib)] public static extern uint xcb_get_maximum_request_length(IntPtr c);
    [DllImport(Lib)] public static extern IntPtr xcb_get_setup(IntPtr c);
    [DllImport(Lib)] public static extern ScreenIterator xcb_setup_roots_iterator(IntPtr setup);

    // ---- events -----------------------------------------------------

    [DllImport(Lib)] public static extern GenericEvent* xcb_wait_for_event(IntPtr c);
    [DllImport(Lib)] public static extern GenericEvent* xcb_poll_for_event(IntPtr c);

    // ---- windows ----------------------------------------------------

    [DllImport(Lib)]
    public static extern VoidCookie xcb_create_window(
        IntPtr c, byte depth, uint wid, uint parent, short x, short y,
        ushort width, ushort height, ushort border_width, ushort _class,
        uint visual, uint value_mask, uint* value_list);

    [DllImport(Lib)]
    public static extern VoidCookie xcb_change_window_attributes(
        IntPtr c, uint window, uint value_mask, uint* value_list);

    // ---- atoms ------------------------------------------------------

    [DllImport(Lib)]
    public static extern InternAtomCookie xcb_intern_atom(
        IntPtr c, byte only_if_exists, ushort name_len,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Lib)]
    public static extern InternAtomReply* xcb_intern_atom_reply(
        IntPtr c, InternAtomCookie cookie, IntPtr* e);

    [DllImport(Lib)]
    public static extern GetAtomNameCookie xcb_get_atom_name(IntPtr c, uint atom);

    [DllImport(Lib)]
    public static extern GetAtomNameReply* xcb_get_atom_name_reply(
        IntPtr c, GetAtomNameCookie cookie, IntPtr* e);

    [DllImport(Lib)] public static extern byte* xcb_get_atom_name_name(GetAtomNameReply* r);
    [DllImport(Lib)] public static extern int xcb_get_atom_name_name_length(GetAtomNameReply* r);

    // ---- properties -------------------------------------------------

    [DllImport(Lib)]
    public static extern GetPropertyCookie xcb_get_property(
        IntPtr c, byte delete, uint window, uint property, uint type,
        uint long_offset, uint long_length);

    [DllImport(Lib)]
    public static extern GetPropertyReply* xcb_get_property_reply(
        IntPtr c, GetPropertyCookie cookie, IntPtr* e);

    [DllImport(Lib)] public static extern void* xcb_get_property_value(GetPropertyReply* r);
    [DllImport(Lib)] public static extern int xcb_get_property_value_length(GetPropertyReply* r);

    [DllImport(Lib)]
    public static extern VoidCookie xcb_change_property(
        IntPtr c, byte mode, uint window, uint property, uint type,
        byte format, uint data_len, void* data);

    [DllImport(Lib)]
    public static extern VoidCookie xcb_delete_property(IntPtr c, uint window, uint property);

    // ---- selections -------------------------------------------------

    [DllImport(Lib)]
    public static extern VoidCookie xcb_set_selection_owner(
        IntPtr c, uint owner, uint selection, uint time);

    [DllImport(Lib)]
    public static extern GetSelectionOwnerCookie xcb_get_selection_owner(IntPtr c, uint selection);

    [DllImport(Lib)]
    public static extern GetSelectionOwnerReply* xcb_get_selection_owner_reply(
        IntPtr c, GetSelectionOwnerCookie cookie, IntPtr* e);

    [DllImport(Lib)]
    public static extern VoidCookie xcb_convert_selection(
        IntPtr c, uint requestor, uint selection, uint target, uint property, uint time);

    [DllImport(Lib)]
    public static extern VoidCookie xcb_send_event(
        IntPtr c, byte propagate, uint destination, uint event_mask, void* event_);

    // ---- extensions -------------------------------------------------

    [DllImport(Lib)]
    public static extern QueryExtensionReply* xcb_get_extension_data(IntPtr c, IntPtr ext);

    [DllImport(LibXfixes)]
    public static extern XfixesQueryVersionCookie xcb_xfixes_query_version(
        IntPtr c, uint client_major_version, uint client_minor_version);

    [DllImport(LibXfixes)]
    public static extern IntPtr xcb_xfixes_query_version_reply(
        IntPtr c, XfixesQueryVersionCookie cookie, IntPtr* e);

    [DllImport(LibXfixes)]
    public static extern VoidCookie xcb_xfixes_select_selection_input(
        IntPtr c, uint window, uint selection, uint event_mask);

    /// xcb_xfixes_id is an exported data symbol (xcb_extension_t), not a
    /// function, so it has to be fetched by address rather than DllImport.
    public static IntPtr XfixesExtension()
    {
        var handle = NativeLibrary.Load(LibXfixes);
        return NativeLibrary.GetExport(handle, "xcb_xfixes_id");
    }

    // ---- constants --------------------------------------------------

    public const byte CopyFromParent = 0;
    public const ushort WindowClassInputOnly = 2;
    public const uint CwEventMask = 1u << 11;
    public const uint EventMaskPropertyChange = 1u << 22;

    public const byte PropModeReplace = 0;
    public const byte PropModeAppend = 2;

    public const uint AtomNone = 0;
    public const uint AtomPrimary = 1;
    public const uint AtomAtom = 4;
    public const uint AtomInteger = 19;
    public const uint AtomString = 31;

    public const uint CurrentTime = 0;

    public const byte PropertyNotify = 28;
    public const byte SelectionClear = 29;
    public const byte SelectionRequest = 30;
    public const byte SelectionNotify = 31;

    public const byte PropertyNewValue = 0;
    public const byte PropertyDelete = 1;

    public const uint XfixesSetSelectionOwnerMask = 1;

    // ---- structs ----------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct VoidCookie { public uint sequence; }

    [StructLayout(LayoutKind.Sequential)]
    public struct InternAtomCookie { public uint sequence; }

    [StructLayout(LayoutKind.Sequential)]
    public struct GetAtomNameCookie { public uint sequence; }

    [StructLayout(LayoutKind.Sequential)]
    public struct GetPropertyCookie { public uint sequence; }

    [StructLayout(LayoutKind.Sequential)]
    public struct GetSelectionOwnerCookie { public uint sequence; }

    [StructLayout(LayoutKind.Sequential)]
    public struct XfixesQueryVersionCookie { public uint sequence; }

    [StructLayout(LayoutKind.Sequential)]
    public struct ScreenIterator { public Screen* data; public int rem; public int index; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Setup
    {
        public byte status; public byte pad0;
        public ushort protocol_major_version, protocol_minor_version, length;
        public uint release_number;
        public uint resource_id_base;
        public uint resource_id_mask;
        public uint motion_buffer_size;
        public ushort vendor_len;
        public ushort maximum_request_length;
        public byte roots_len, pixmap_formats_len, image_byte_order;
        public byte bitmap_format_bit_order, bitmap_format_scanline_unit;
        public byte bitmap_format_scanline_pad, min_keycode, max_keycode;
        public uint pad1;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Screen
    {
        public uint root;
        public uint default_colormap;
        public uint white_pixel;
        public uint black_pixel;
        public uint current_input_masks;
        public ushort width_in_pixels;
        public ushort height_in_pixels;
        public ushort width_in_millimeters;
        public ushort height_in_millimeters;
        public ushort min_installed_maps;
        public ushort max_installed_maps;
        public uint root_visual;
        public byte backing_stores;
        public byte save_unders;
        public byte root_depth;
        public byte allowed_depths_len;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GenericEvent
    {
        public byte response_type;
        public byte pad0;
        public ushort sequence;
        public uint pad1, pad2, pad3, pad4, pad5, pad6, pad7;
        public uint full_sequence;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct InternAtomReply
    {
        public byte response_type; public byte pad0; public ushort sequence;
        public uint length; public uint atom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GetAtomNameReply
    {
        public byte response_type; public byte pad0; public ushort sequence;
        public uint length; public ushort name_len;
        // followed by pad[22] then the name bytes
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GetPropertyReply
    {
        public byte response_type; public byte format; public ushort sequence;
        public uint length; public uint type; public uint bytes_after;
        public uint value_len;
        public uint pad0, pad1, pad2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GetSelectionOwnerReply
    {
        public byte response_type; public byte pad0; public ushort sequence;
        public uint length; public uint owner;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct QueryExtensionReply
    {
        public byte response_type; public byte pad0; public ushort sequence;
        public uint length; public byte present; public byte major_opcode;
        public byte first_event; public byte first_error;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SelectionNotifyEvent
    {
        public byte response_type; public byte pad0; public ushort sequence;
        public uint time; public uint requestor;
        public uint selection; public uint target; public uint property;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SelectionRequestEvent
    {
        public byte response_type; public byte pad0; public ushort sequence;
        public uint time; public uint owner; public uint requestor;
        public uint selection; public uint target; public uint property;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SelectionClearEvent
    {
        public byte response_type; public byte pad0; public ushort sequence;
        public uint time; public uint owner; public uint selection;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyNotifyEvent
    {
        public byte response_type; public byte pad0; public ushort sequence;
        public uint window; public uint atom; public uint time;
        public byte state; public byte pad1, pad2, pad3;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XfixesSelectionNotifyEvent
    {
        public byte response_type; public byte subtype; public ushort sequence;
        public uint window; public uint owner; public uint selection;
        public uint timestamp; public uint selection_timestamp;
        public ulong pad0;
    }
}

/// poll(2), so the event loop can sleep on the X socket instead of
/// spinning. A daemon that wakes 500 times a second to ask "anything yet?"
/// is a daemon that shows up in the user's battery report.
internal static class Posix
{
    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int fd;
        public short events;
        public short revents;
    }

    public const short POLLIN = 0x001;

    [DllImport("libc", SetLastError = true)]
    public static extern int poll(ref PollFd fds, nuint nfds, int timeout);
}
