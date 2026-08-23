using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using static ClipSync.Platform.Backends.X11.Xcb;

namespace ClipSync.Platform.Backends.X11;

/// Raised when the X connection dies (XWayland exit). The caller is
/// expected to drop this XClip and build a new one — never to keep using
/// the dead connection.
internal sealed class XConnectionLostException : Exception
{
    public XConnectionLostException(int code) : base($"X connection lost (xcb error {code})") { }
}

internal sealed unsafe class XSelection : IDisposable
{
    private IntPtr _c;
    private uint _window;
    private byte _xfixesFirstEvent;

    public uint AClipboard, ATargets, AIncr, ATimestamp, AMultiple, ASelProp, AAtomPair;
    private readonly Dictionary<string, uint> _atoms = new();
    private readonly Dictionary<uint, string> _atomNames = new();

    /// Data we are currently serving as selection owner, MIME name → bytes.
    private Dictionary<string, byte[]>? _owned;

    /// In-flight INCR sends, keyed by (requestor, property).
    private readonly Dictionary<(uint, uint), IncrSend> _incrSends = new();

    private sealed class IncrSend
    {
        public required byte[] Data;
        public required uint Type;
        public int Offset;
        public bool Finished;
    }

    /// Chunk size for INCR transfers. Kept well under the server's maximum
    /// request length; the ceiling that actually matters is measured, not
    /// assumed.
    public int IncrChunk = 256 * 1024;

    public event Action<uint>? ClipboardChanged;

    public static XSelection Connect()
    {
        var clip = new XSelection();
        clip.Init();
        return clip;
    }

    private void Init()
    {
        int screenNum;
        _c = xcb_connect(null, &screenNum);
        int err = xcb_connection_has_error(_c);
        if (err != 0) throw new XConnectionLostException(err);

        var setup = xcb_get_setup(_c);
        var iter = xcb_setup_roots_iterator(setup);
        for (int i = 0; i < screenNum && iter.rem > 0; i++) iter.data++;
        uint root = iter.data->root;
        RootWindow = root;

        // InputOnly window: we never map it. It exists only to own
        // selections and to receive PropertyNotify for INCR transfers.
        _window = xcb_generate_id(_c);
        uint mask = EventMaskPropertyChange;
        xcb_create_window(_c, CopyFromParent, _window, root, 0, 0, 1, 1, 0,
                          WindowClassInputOnly, 0, CwEventMask, &mask);

        AClipboard = Atom("CLIPBOARD");
        ATargets = Atom("TARGETS");
        AIncr = Atom("INCR");
        ATimestamp = Atom("TIMESTAMP");
        AMultiple = Atom("MULTIPLE");
        AAtomPair = Atom("ATOM_PAIR");
        ASelProp = Atom("CLIPSYNC_SEL");

        var ext = XfixesExtension();
        var extData = xcb_get_extension_data(_c, ext);
        if (extData == null || extData->present == 0)
            throw new InvalidOperationException("XFixes extension not available");
        _xfixesFirstEvent = extData->first_event;

        // XFixes requires a version handshake before any other request.
        IntPtr e;
        var reply = xcb_xfixes_query_version_reply(_c, xcb_xfixes_query_version(_c, 5, 0), &e);
        if (reply != IntPtr.Zero) Marshal.FreeHGlobal(reply);

        xcb_flush(_c);
        Check();
    }

    public void Check()
    {
        int err = xcb_connection_has_error(_c);
        if (err != 0) throw new XConnectionLostException(err);
    }

    public uint Atom(string name)
    {
        if (_atoms.TryGetValue(name, out var a)) return a;
        IntPtr e;
        var cookie = xcb_intern_atom(_c, 0, (ushort)System.Text.Encoding.UTF8.GetByteCount(name), name);
        var reply = xcb_intern_atom_reply(_c, cookie, &e);
        if (reply == null) { Check(); throw new InvalidOperationException($"intern atom {name} failed"); }
        a = reply->atom;
        NativeMemory.Free(reply);
        _atoms[name] = a;
        _atomNames[a] = name;
        return a;
    }

    public string AtomName(uint atom)
    {
        if (atom == 0) return "None";
        if (_atomNames.TryGetValue(atom, out var n)) return n;
        IntPtr e;
        var reply = xcb_get_atom_name_reply(_c, xcb_get_atom_name(_c, atom), &e);
        if (reply == null) { Check(); return $"<{atom}>"; }
        var len = xcb_get_atom_name_name_length(reply);
        var ptr = xcb_get_atom_name_name(reply);
        n = System.Text.Encoding.UTF8.GetString(ptr, len);
        NativeMemory.Free(reply);
        _atomNames[atom] = n;
        return n;
    }

    /// Ask to be told whenever the CLIPBOARD selection owner changes.
    public void WatchClipboard()
    {
        xcb_xfixes_select_selection_input(_c, _window, AClipboard, XfixesSetSelectionOwnerMask);
        xcb_flush(_c);
        Check();
    }

    // ---- reading ----------------------------------------------------

    /// The MIME/target list currently offered on the clipboard.
    public string[] ReadTargets(int timeoutMs = 3000)
    {
        var raw = ConvertAndRead(ATargets, timeoutMs);
        if (raw is null || raw.Length < 4) return Array.Empty<string>();
        var count = raw.Length / 4;
        var names = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            uint atom = BitConverter.ToUInt32(raw, i * 4);
            if (atom != 0) names.Add(AtomName(atom));
        }
        return names.ToArray();
    }

    public byte[]? ReadTarget(string mime, int timeoutMs = 30000)
        => ConvertAndRead(Atom(mime), timeoutMs);

    /// Convert the selection to `target` and read the resulting property,
    /// transparently completing an INCR transfer if the owner starts one.
    private byte[]? ConvertAndRead(uint target, int timeoutMs)
    {
        // Delete first: a stale property from a previous conversion would
        // otherwise be read as this one's result.
        xcb_delete_property(_c, _window, ASelProp);
        xcb_convert_selection(_c, _window, AClipboard, target, ASelProp, CurrentTime);
        xcb_flush(_c);
        Check();

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var ev = Poll();
            if (ev == null) { WaitForInput(20); continue; }
            byte type = (byte)(ev->response_type & 0x7f);

            if (type == SelectionNotify)
            {
                var sn = *(SelectionNotifyEvent*)ev;
                NativeMemory.Free(ev);
                if (sn.property == AtomNone) return null;   // owner refused
                var (data, isIncr) = GetProp(sn.property, delete: !IsIncrStart(sn.property));
                if (!isIncr) return data;
                return ReadIncr(sn.property, timeoutMs - (int)sw.ElapsedMilliseconds);
            }

            Dispatch(ev);
        }
        return null;
    }

    private bool IsIncrStart(uint property)
    {
        IntPtr e;
        var r = xcb_get_property_reply(_c, xcb_get_property(_c, 0, _window, property, 0, 0, 0), &e);
        if (r == null) { Check(); return false; }
        bool incr = r->type == AIncr;
        NativeMemory.Free(r);
        return incr;
    }

    private (byte[]? data, bool isIncr) GetProp(uint property, bool delete)
    {
        var all = new List<byte>();
        uint offset = 0;
        while (true)
        {
            IntPtr e;
            var r = xcb_get_property_reply(_c,
                xcb_get_property(_c, delete ? (byte)1 : (byte)0, _window, property,
                                 0 /* AnyPropertyType */, offset, 0x1FFFFFFF), &e);
            if (r == null) { Check(); return (null, false); }
            if (r->type == AIncr) { NativeMemory.Free(r); return (null, true); }

            int len = xcb_get_property_value_length(r);
            if (len > 0)
            {
                var span = new ReadOnlySpan<byte>(xcb_get_property_value(r), len);
                all.AddRange(span.ToArray());
            }
            uint after = r->bytes_after;
            NativeMemory.Free(r);
            if (after == 0) break;
            offset += (uint)(len / 4);
        }
        return (all.ToArray(), false);
    }

    /// Complete an INCR read: delete the property to signal readiness, then
    /// accumulate chunks until a zero-length one arrives.
    private byte[]? ReadIncr(uint property, int timeoutMs)
    {
        var buf = new List<byte>();
        xcb_delete_property(_c, _window, property);
        xcb_flush(_c);

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var ev = Poll();
            if (ev == null) { WaitForInput(20); continue; }
            byte type = (byte)(ev->response_type & 0x7f);

            if (type == PropertyNotify)
            {
                var pn = *(PropertyNotifyEvent*)ev;
                NativeMemory.Free(ev);

                // A Delete on someone else's window is a requestor asking
                // for the next chunk of a transfer we are serving. Dropping
                // it here stalls that transfer until it times out, and
                // while we wait we serve nobody — so it has to be handled
                // even in the middle of our own read.
                if (pn.state == PropertyDelete) { AdvanceIncr(pn.window, pn.atom); continue; }
                if (pn.window != _window || pn.atom != property || pn.state != PropertyNewValue)
                    continue;

                var (chunk, _) = GetProp(property, delete: true);
                xcb_flush(_c);
                if (chunk == null || chunk.Length == 0) return buf.ToArray();  // terminator
                buf.AddRange(chunk);
                continue;
            }

            Dispatch(ev);
        }
        return null;   // timed out mid-transfer
    }

    /// Read a property off an arbitrary window — used to identify the
    /// selection owner (WM_CLASS / _NET_WM_PID), which is the only source
    /// identification available on this stack.
    public byte[]? ReadWindowProperty(uint window, string property)
    {
        IntPtr e;
        var r = xcb_get_property_reply(_c,
            xcb_get_property(_c, 0, window, Atom(property), 0, 0, 0x1FFFFFFF), &e);
        if (r == null) { Check(); return null; }
        int len = xcb_get_property_value_length(r);
        byte[]? outb = len > 0
            ? new ReadOnlySpan<byte>(xcb_get_property_value(r), len).ToArray()
            : null;
        NativeMemory.Free(r);
        return outb;
    }

    /// WM_CLASS is two NUL-separated strings: instance, then class.
    private string? WmClassOf(uint window)
    {
        if (window == 0) return null;
        var raw = ReadWindowProperty(window, "WM_CLASS");
        if (raw == null || raw.Length == 0) return null;
        var parts = System.Text.Encoding.UTF8.GetString(raw).Split('\0',
            StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[1] : (parts.Length == 1 ? parts[0] : null);
    }

    /// Identify the app behind a selection owner.
    ///
    /// Reading WM_CLASS straight off the owner window almost never works:
    /// toolkits own selections with a dedicated unmapped window that carries
    /// no WM_CLASS. But X partitions resource IDs per client — the high bits
    /// of every XID are that client's base — so the owner can be matched to
    /// a managed toplevel from _NET_CLIENT_LIST belonging to the same client,
    /// and that window does carry WM_CLASS.
    public string? IdentifyOwner(uint owner, out string how)
    {
        how = "none";
        if (owner == 0) return null;

        if (WmClassOf(owner) is { } direct) { how = "WM_CLASS on owner"; return direct; }

        var setup = (Setup*)xcb_get_setup(_c);
        uint clientBase = owner & ~setup->resource_id_mask;

        var raw = ReadWindowProperty(RootWindow, "_NET_CLIENT_LIST");
        if (raw != null)
        {
            for (int i = 0; i + 4 <= raw.Length; i += 4)
            {
                uint w = BitConverter.ToUInt32(raw, i);
                if ((w & ~setup->resource_id_mask) != clientBase) continue;
                if (WmClassOf(w) is { } cls)
                {
                    how = $"client base 0x{clientBase:x} → toplevel 0x{w:x}";
                    return cls;
                }
            }
        }

        if (ReadWindowProperty(owner, "_NET_WM_PID") is { Length: >= 4 } pidRaw)
        {
            uint pid = BitConverter.ToUInt32(pidRaw, 0);
            how = $"_NET_WM_PID {pid}";
            try { return File.ReadAllText($"/proc/{pid}/comm").Trim(); } catch { }
        }

        return null;
    }

    public uint RootWindow { get; private set; }

    /// Our selection-owning window. Lets callers tell a clipboard change we
    /// caused from one somebody else caused.
    public uint Window => _window;

    // ---- writing ----------------------------------------------------

    /// Take ownership of CLIPBOARD and serve `data` until ownership is lost.
    public bool OwnClipboard(Dictionary<string, byte[]> data)
    {
        _owned = data;
        foreach (var mime in data.Keys) Atom(mime);

        xcb_set_selection_owner(_c, _window, AClipboard, CurrentTime);
        xcb_flush(_c);
        Check();

        IntPtr e;
        var r = xcb_get_selection_owner_reply(_c, xcb_get_selection_owner(_c, AClipboard), &e);
        if (r == null) { Check(); return false; }
        bool ok = r->owner == _window;
        NativeMemory.Free(r);
        return ok;
    }

    public bool OwnsClipboard => _owned != null;

    private void ServeRequest(SelectionRequestEvent req)
    {
        uint property = req.property != AtomNone ? req.property : req.target;
        bool ok = false;

        if (_owned != null)
        {
            if (req.target == ATargets)
            {
                var atoms = new List<uint> { ATargets, ATimestamp };
                foreach (var mime in _owned.Keys) atoms.Add(Atom(mime));
                var arr = atoms.ToArray();
                fixed (uint* p = arr)
                    xcb_change_property(_c, PropModeReplace, req.requestor, property,
                                        AtomAtom, 32, (uint)arr.Length, p);
                ok = true;
            }
            else if (req.target == ATimestamp)
            {
                uint t = CurrentTime;
                xcb_change_property(_c, PropModeReplace, req.requestor, property,
                                    AtomInteger, 32, 1, &t);
                ok = true;
            }
            else
            {
                var name = AtomName(req.target);
                if (_owned.TryGetValue(name, out var bytes))
                    ok = SendValue(req.requestor, property, req.target, bytes);
            }
        }

        var notify = new SelectionNotifyEvent
        {
            response_type = SelectionNotify,
            time = req.time,
            requestor = req.requestor,
            selection = req.selection,
            target = req.target,
            property = ok ? property : AtomNone,
        };
        // The event must be padded to the 32-byte X event size on the wire.
        var buf = stackalloc byte[32];
        for (int i = 0; i < 32; i++) buf[i] = 0;
        *(SelectionNotifyEvent*)buf = notify;
        xcb_send_event(_c, 0, req.requestor, 0, buf);
        xcb_flush(_c);
    }

    /// Write a value to the requestor, starting an INCR transfer if it is
    /// too large to hand over in one property.
    private bool SendValue(uint requestor, uint property, uint type, byte[] bytes)
    {
        if (bytes.Length <= IncrChunk)
        {
            fixed (byte* p = bytes)
                xcb_change_property(_c, PropModeReplace, requestor, property,
                                    type, 8, (uint)bytes.Length, p);
            return true;
        }

        // INCR: announce the total, then feed chunks as the requestor
        // deletes the property. We must watch the requestor for those
        // PropertyNotify(Delete) events.
        uint mask = EventMaskPropertyChange;
        xcb_change_window_attributes(_c, requestor, CwEventMask, &mask);

        uint total = (uint)bytes.Length;
        xcb_change_property(_c, PropModeReplace, requestor, property, AIncr, 32, 1, &total);
        _incrSends[(requestor, property)] = new IncrSend { Data = bytes, Type = type };
        xcb_flush(_c);
        return true;
    }

    private void AdvanceIncr(uint requestor, uint property)
    {
        if (!_incrSends.TryGetValue((requestor, property), out var send)) return;

        if (send.Finished) { _incrSends.Remove((requestor, property)); return; }

        int remaining = send.Data.Length - send.Offset;
        int n = Math.Min(IncrChunk, remaining);
        fixed (byte* p = send.Data)
            xcb_change_property(_c, PropModeReplace, requestor, property, send.Type, 8,
                                (uint)n, p + send.Offset);
        send.Offset += n;
        if (n == 0) send.Finished = true;   // zero-length write terminates
        xcb_flush(_c);
    }

    // ---- event pump -------------------------------------------------

    public GenericEvent* Poll()
    {
        var ev = xcb_poll_for_event(_c);
        if (ev == null) Check();
        return ev;
    }

    /// Handle an event we are not specifically waiting for, and free it.
    private void Dispatch(GenericEvent* ev)
    {
        byte type = (byte)(ev->response_type & 0x7f);

        if (type == SelectionRequest)
        {
            ServeRequest(*(SelectionRequestEvent*)ev);
        }
        else if (type == SelectionClear)
        {
            _owned = null;
        }
        else if (type == PropertyNotify)
        {
            var pn = *(PropertyNotifyEvent*)ev;
            if (pn.state == PropertyDelete) AdvanceIncr(pn.window, pn.atom);
        }
        else if (type == _xfixesFirstEvent)
        {
            var xf = *(XfixesSelectionNotifyEvent*)ev;
            if (xf.selection == AClipboard) ClipboardChanged?.Invoke(xf.owner);
        }

        NativeMemory.Free(ev);
    }

    /// Pump events for `ms`, dispatching everything. Used by the watcher
    /// and while serving as selection owner.
    public void Pump(int ms)
    {
        var deadline = Environment.TickCount64 + ms;
        while (true)
        {
            // Drain what XCB has already buffered before sleeping on the
            // socket: those events are readable without the fd ever
            // becoming ready again, so polling first can block on data
            // that has already arrived.
            GenericEvent* ev;
            while ((ev = Poll()) != null) Dispatch(ev);

            var remaining = (int)(deadline - Environment.TickCount64);
            if (remaining <= 0) return;
            WaitForInput(remaining);
        }
    }

    /// Sleep until the X socket has something to say, or the timeout
    /// expires. Returns early and often; callers must re-check their own
    /// condition rather than assume an event arrived.
    private void WaitForInput(int timeoutMs)
    {
        var pfd = new Posix.PollFd
        {
            fd = xcb_get_file_descriptor(_c),
            events = Posix.POLLIN,
        };
        Posix.poll(ref pfd, 1, timeoutMs);
    }

    public void Dispose()
    {
        if (_c != IntPtr.Zero) { xcb_disconnect(_c); _c = IntPtr.Zero; }
    }
}
