using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClipSync.Net;
using ClipSync.Security;
using Tmds.DBus.Protocol;

namespace ClipSync.Ui;

/// The tray icon, as a StatusNotifierItem plus its dbusmenu.
///
/// There is no toolkit here on purpose. An SNI tray entry is two D-Bus
/// objects and a registration call — pulling in GTK to draw something the
/// shell draws itself would add a large dependency, a second main loop, and
/// a hard requirement on a display the daemon may start before.
///
/// The host is a separate process (on GNOME, the AppIndicator extension).
/// Where none is running the registration fails and the daemon says so
/// rather than silently showing nothing.
internal sealed class TrayIcon : IAsyncDisposable, IPathMethodHandler
{
    private const string ItemPath = "/StatusNotifierItem";
    private const string MenuPath = "/MenuBar";
    private const string ItemIface = "org.kde.StatusNotifierItem";
    private const string MenuIface = "com.canonical.dbusmenu";
    private const string WatcherService = "org.kde.StatusNotifierWatcher";
    private const string PropsIface = "org.freedesktop.DBus.Properties";

    /// The layout signature dbusmenu uses for an item: id, properties, and
    /// children as variants of the same structure.
    private const string LayoutSignature = "(ia{sv}av)";

    private readonly TrayMenu _menu = new();
    private readonly string _busName;
    private DBusConnection? _conn;
    private uint _revision = 1;

    private Func<TrayState>? _state;
    private TrayActions? _actions;

    public bool Registered { get; private set; }

    public TrayIcon()
    {
        // The name is prescribed by the spec: the host looks for
        // org.kde.StatusNotifierItem-<pid>-<instance>.
        _busName = $"org.kde.StatusNotifierItem-{Environment.ProcessId}-1";
    }

    /// Registered at the root, not at the item path: the menu lives at
    /// /MenuBar, which is a *sibling* of /StatusNotifierItem rather than a
    /// child, so a handler rooted at the item path never sees it and every
    /// menu call comes back as "no such method".
    public string Path => "/";

    public bool HandlesChildPaths => true;

    public void Bind(Func<TrayState> state, TrayActions actions)
    {
        _state = state;
        _actions = actions;
        Rebuild();
    }

    public async Task<bool> StartAsync()
    {
        try
        {
            _conn = new DBusConnection(DBusAddress.Session
                ?? throw new InvalidOperationException("no session D-Bus address"));
            await _conn.ConnectAsync();
            _conn.AddMethodHandler(this);
            await _conn.RequestNameAsync(_busName, RequestNameOptions.None);

            await CallWatcherAsync("RegisterStatusNotifierItem", _busName);
            Registered = true;
            Identity.Log($"Tray: registered {_busName} with {WatcherService}");
            return true;
        }
        catch (Exception ex)
        {
            // Overwhelmingly the "no SNI host" case: on GNOME that means the
            // AppIndicator extension is not enabled. Worth naming explicitly,
            // because the failure is otherwise invisible — the daemon runs
            // perfectly and simply has no icon.
            Identity.Log($"Tray: no StatusNotifierWatcher ({ex.GetType().Name}: {ex.Message})");
            Registered = false;
            return false;
        }
    }

    private async Task CallWatcherAsync(string member, string arg)
    {
        var conn = _conn!;
        MessageBuffer Build()
        {
            var w = conn.GetMessageWriter();
            try
            {
                w.WriteMethodCallHeader(WatcherService, "/StatusNotifierWatcher",
                                        WatcherService, member, "s");
                w.WriteString(arg);
                return w.CreateMessage();
            }
            finally { w.Dispose(); }
        }
        await conn.CallMethodAsync(Build());
    }

    /// Rebuild the menu, and tell the host to re-read it only if the menu
    /// actually changed.
    ///
    /// The signature check is what stops a feedback loop: the host calls
    /// AboutToShow before opening the menu, which rebuilds; announcing a new
    /// revision every time made the host re-read, which it answered with
    /// another AboutToShow. Eleven GetLayout round trips in forty
    /// milliseconds, for a menu nobody had touched.
    public void Rebuild()
    {
        if (_state is null || _actions is null) return;
        _menu.Build(_state(), _actions);

        var signature = Signature();
        if (signature == _lastSignature) return;

        _lastSignature = signature;
        _revision++;
        EmitLayoutUpdated();
    }

    /// Everything the host can see about the menu. Ids are included because
    /// they are what an Event refers to, so a renumbering is a real change
    /// even when every label is identical.
    private string Signature()
        => string.Join('\u001f', _menu.All().Select(
               i => $"{i.Id}\u001e{i.Label}\u001e{i.Enabled}\u001e{i.Checked}\u001e{i.IsSeparator}"));

    private string? _lastSignature;

    private void EmitLayoutUpdated()
    {
        if (_conn is not { } conn || !Registered) return;
        try
        {
            var w = conn.GetMessageWriter();
            try
            {
                w.WriteSignalHeader(null, MenuPath, MenuIface, "LayoutUpdated", "ui");
                w.WriteUInt32(_revision);
                w.WriteInt32(0);                  // parent: the whole menu
                conn.TrySendMessage(w.CreateMessage());
            }
            finally { w.Dispose(); }
        }
        catch (Exception ex)
        {
            Identity.Log($"Tray: LayoutUpdated failed: {ex.Message}");
        }
    }

    // ---- D-Bus method dispatch --------------------------------------

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        var request = context.Request;
        var path = request.PathAsString;
        var iface = request.InterfaceAsString;
        var member = request.MemberAsString;

        // Log every inbound call. Without this, "the log is empty" is
        // ambiguous between "the host called nothing" and "the host called
        // something we do not log", which are very different problems.
        Identity.Log($"Tray: <- {iface}.{member} on {path}");

        try
        {
            if (iface == PropsIface) HandleProperties(context, path, member);
            else if (path == MenuPath && iface == MenuIface) HandleMenu(context, member);
            else if (path == ItemPath && iface == ItemIface) HandleItem(context, member);
            else if (member == "Introspect") ReplyIntrospect(context, path);
            else context.ReplyUnknownMethodError();
        }
        catch (Exception ex)
        {
            Identity.Log($"Tray: {member} failed: {ex.GetType().Name}: {ex.Message}");
            if (!context.ReplySent) context.ReplyError("org.freedesktop.DBus.Error.Failed", ex.Message);
        }

        return default;
    }

    private void HandleItem(MethodContext context, string? member)
    {
        switch (member)
        {
            // A click on the icon. With ItemIsMenu the host shows the menu
            // itself, so there is nothing to do but acknowledge.
            case "Activate":
            case "SecondaryActivate":
            case "ContextMenu":
            case "Scroll":
                context.Reply(context.CreateReplyWriter("").CreateMessage());
                break;
            default:
                context.ReplyUnknownMethodError();
                break;
        }
    }

    private void HandleMenu(MethodContext context, string? member)
    {
        switch (member)
        {
            case "GetLayout":
            {
                var reader = context.Request.GetBodyReader();
                var parentId = reader.ReadInt32();
                var depth = reader.ReadInt32();
                Identity.Log($"Tray: GetLayout(parent={parentId}, depth={depth})");

                var w = context.CreateReplyWriter("u(ia{sv}av)");
                try
                {
                    w.WriteUInt32(_revision);
                    var root = _menu.Find(parentId) ?? _menu.Root;

                    // Always send the whole subtree, whatever depth was
                    // asked for. The menu is a dozen items, so there is
                    // nothing to save by trimming it, and a host that asks
                    // for depth 1 and never comes back for the rest shows
                    // submenus that open empty.
                    WriteLayout(ref w, root, -1);
                    context.Reply(w.CreateMessage());
                }
                finally { w.Dispose(); }
                break;
            }

            case "GetGroupProperties":
            {
                var reader = context.Request.GetBodyReader();
                var ids = reader.ReadArrayOfInt32();

                var w = context.CreateReplyWriter("a(ia{sv})");
                try
                {
                    var array = w.WriteArrayStart(DBusType.Struct);
                    foreach (var id in ids)
                    {
                        if (_menu.Find(id) is not { } item) continue;
                        w.WriteStructureStart();
                        w.WriteInt32(item.Id);
                        WriteProperties(ref w, item);
                    }
                    w.WriteArrayEnd(array);
                    context.Reply(w.CreateMessage());
                }
                finally { w.Dispose(); }
                break;
            }

            case "AboutToShow":
            {
                // Rebuild before the menu opens so it shows current peer
                // state rather than whatever it was when last invalidated.
                Rebuild();
                var w = context.CreateReplyWriter("b");
                try { w.WriteBool(true); context.Reply(w.CreateMessage()); }
                finally { w.Dispose(); }
                break;
            }

            case "Event":
            {
                var reader = context.Request.GetBodyReader();
                var id = reader.ReadInt32();
                var eventId = reader.ReadString();
                Identity.Log($"Tray: event '{eventId}' on item {id} " +
                             $"({_menu.Find(id)?.Label ?? "<unknown>"})");

                if (eventId == "clicked" && _menu.Find(id) is { Activate: { } action })
                {
                    Identity.Log($"Tray: menu item {id} clicked");
                    action();
                    Rebuild();
                }

                context.Reply(context.CreateReplyWriter("").CreateMessage());
                break;
            }

            case "EventGroup":
            {
                // Reply shape is the list of ids that failed; none do.
                var w = context.CreateReplyWriter("ai");
                try
                {
                    w.WriteArray(Array.Empty<int>());
                    context.Reply(w.CreateMessage());
                }
                finally { w.Dispose(); }
                break;
            }

            case "GetProperty":
            {
                var reader = context.Request.GetBodyReader();
                var id = reader.ReadInt32();
                var name = reader.ReadString();

                var item = _menu.Find(id);
                var w = context.CreateReplyWriter("v");
                try
                {
                    switch (name)
                    {
                        case "label": w.WriteVariantString(item?.Label ?? ""); break;
                        case "enabled": w.WriteVariantBool(item?.Enabled ?? false); break;
                        case "visible": w.WriteVariantBool(item is not null); break;
                        case "type": w.WriteVariantString(item?.IsSeparator == true ? "separator" : "standard"); break;
                        case "toggle-type":
                            w.WriteVariantString(item?.Checked is null ? "" : "checkmark");
                            break;
                        case "toggle-state":
                            w.WriteVariantInt32(item?.Checked == true ? 1 : 0);
                            break;
                        case "children-display":
                            w.WriteVariantString(item?.Children.Count > 0 ? "submenu" : "");
                            break;
                        default: w.WriteVariantString(""); break;
                    }
                    context.Reply(w.CreateMessage());
                }
                finally { w.Dispose(); }
                break;
            }

            case "AboutToShowGroup":
            {
                var reader = context.Request.GetBodyReader();
                var ids = reader.ReadArrayOfInt32();
                Rebuild();

                var w = context.CreateReplyWriter("aiai");
                try
                {
                    w.WriteArray(ids);                      // all need updating
                    w.WriteArray(Array.Empty<int>());       // none errored
                    context.Reply(w.CreateMessage());
                }
                finally { w.Dispose(); }
                break;
            }

            default:
                context.ReplyUnknownMethodError();
                break;
        }
    }

    /// Write one item as `(ia{sv}av)`. Children are variants of the same
    /// structure, so this recurses; depth < 0 means "as deep as it goes",
    /// which is what hosts normally ask for.
    private void WriteLayout(ref MessageWriter w, MenuItem item, int depth)
    {
        w.WriteStructureStart();
        w.WriteInt32(item.Id);
        WriteProperties(ref w, item);

        var children = w.WriteArrayStart(DBusType.Variant);
        if (depth != 0)
        {
            foreach (var child in item.Children)
            {
                // A variant is a signature followed by the value, so the
                // nested structure can be written inline. VariantValue has
                // no factory for a struct, which is why this is by hand.
                w.WriteSignature(LayoutSignature);
                WriteLayout(ref w, child, depth - 1);
            }
        }
        w.WriteArrayEnd(children);
    }

    private static void WriteProperties(ref MessageWriter w, MenuItem item)
    {
        // Written out longhand rather than through a helper: MessageWriter
        // is a ref struct, so it cannot be captured by a local function or
        // lambda.
        var dict = w.WriteDictionaryStart();

        if (item.IsSeparator)
        {
            w.WriteDictionaryEntryStart();
            w.WriteString("type");
            w.WriteVariantString("separator");
        }
        else
        {
            w.WriteDictionaryEntryStart();
            w.WriteString("label");
            w.WriteVariantString(item.Label);

            w.WriteDictionaryEntryStart();
            w.WriteString("enabled");
            w.WriteVariantBool(item.Enabled);

            w.WriteDictionaryEntryStart();
            w.WriteString("visible");
            w.WriteVariantBool(true);

            if (item.Checked is { } isChecked)
            {
                w.WriteDictionaryEntryStart();
                w.WriteString("toggle-type");
                w.WriteVariantString("checkmark");

                w.WriteDictionaryEntryStart();
                w.WriteString("toggle-state");
                w.WriteVariantInt32(isChecked ? 1 : 0);
            }

            if (item.Children.Count > 0)
            {
                w.WriteDictionaryEntryStart();
                w.WriteString("children-display");
                w.WriteVariantString("submenu");
            }
        }

        w.WriteDictionaryEnd(dict);
    }

    // ---- properties -------------------------------------------------

    private void HandleProperties(MethodContext context, string? path, string? member)
    {
        var reader = context.Request.GetBodyReader();
        var iface = reader.ReadString();

        switch (member)
        {
            case "Get":
            {
                var name = reader.ReadString();
                var w = context.CreateReplyWriter("v");
                try
                {
                    if (!WriteProperty(ref w, path, name))
                    {
                        w.Dispose();
                        context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", name);
                        return;
                    }
                    context.Reply(w.CreateMessage());
                }
                finally { if (!context.ReplySent) w.Dispose(); }
                break;
            }

            case "GetAll":
            {
                var w = context.CreateReplyWriter("a{sv}");
                try
                {
                    var dict = w.WriteDictionaryStart();
                    foreach (var name in PropertyNames(path))
                    {
                        w.WriteDictionaryEntryStart();
                        w.WriteString(name);
                        WriteProperty(ref w, path, name);
                    }
                    w.WriteDictionaryEnd(dict);
                    context.Reply(w.CreateMessage());
                }
                finally { w.Dispose(); }
                break;
            }

            default:
                context.ReplyUnknownMethodError();
                break;
        }
    }

    private static string[] PropertyNames(string? path) => path == MenuPath
        ? ["Version", "Status", "TextDirection", "IconThemePath"]
        : ["Category", "Id", "Title", "Status", "IconName", "Menu", "ItemIsMenu"];

    private bool WriteProperty(ref MessageWriter w, string? path, string name)
    {
        if (path == MenuPath)
        {
            switch (name)
            {
                case "Version": w.WriteVariantUInt32(3); return true;
                case "Status": w.WriteVariantString("normal"); return true;
                case "TextDirection": w.WriteVariantString("ltr"); return true;
                case "IconThemePath":
                    w.WriteSignature("as");
                    w.WriteArray(Array.Empty<string>());
                    return true;
                default: return false;
            }
        }

        switch (name)
        {
            case "Category": w.WriteVariantString("ApplicationStatus"); return true;
            case "Id": w.WriteVariantString("clipsync"); return true;
            case "Title": w.WriteVariantString(_menu.Paused ? "ClipSync — Paused" : "ClipSync"); return true;
            case "Status": w.WriteVariantString("Active"); return true;

            // A stock icon name rather than a bundled pixmap: the shell then
            // renders it in the panel's own style, and there is no icon file
            // to install or theme to match.
            case "IconName":
                w.WriteVariantString(_menu.Paused ? "edit-paste-symbolic" : "edit-copy-symbolic");
                return true;

            case "Menu": w.WriteVariantObjectPath(MenuPath); return true;

            // Tells the host this item has no useful left-click action of
            // its own and should just show the menu.
            case "ItemIsMenu": w.WriteVariantBool(true); return true;

            default: return false;
        }
    }

    /// Honest introspection XML for both objects.
    ///
    /// This is not decoration. GNOME's AppIndicator extension builds its
    /// menu proxy from introspection, so an interface that declares no
    /// methods yields a proxy with nothing to call: the icon appears, and
    /// clicking it does nothing at all, with no error on either side. The
    /// menu is only ever fetched if GetLayout is advertised here.
    private const string ItemInterfaceXml = """
        <interface name="org.kde.StatusNotifierItem">
          <property name="Category" type="s" access="read"/>
          <property name="Id" type="s" access="read"/>
          <property name="Title" type="s" access="read"/>
          <property name="Status" type="s" access="read"/>
          <property name="IconName" type="s" access="read"/>
          <property name="Menu" type="o" access="read"/>
          <property name="ItemIsMenu" type="b" access="read"/>
          <method name="Activate">
            <arg type="i" name="x" direction="in"/>
            <arg type="i" name="y" direction="in"/>
          </method>
          <method name="SecondaryActivate">
            <arg type="i" name="x" direction="in"/>
            <arg type="i" name="y" direction="in"/>
          </method>
          <method name="ContextMenu">
            <arg type="i" name="x" direction="in"/>
            <arg type="i" name="y" direction="in"/>
          </method>
          <method name="Scroll">
            <arg type="i" name="delta" direction="in"/>
            <arg type="s" name="orientation" direction="in"/>
          </method>
          <signal name="NewIcon"/>
          <signal name="NewTitle"/>
          <signal name="NewStatus">
            <arg type="s" name="status"/>
          </signal>
        </interface>
        """;

    private const string MenuInterfaceXml = """
        <interface name="com.canonical.dbusmenu">
          <property name="Version" type="u" access="read"/>
          <property name="TextDirection" type="s" access="read"/>
          <property name="Status" type="s" access="read"/>
          <property name="IconThemePath" type="as" access="read"/>
          <method name="GetLayout">
            <arg type="i" name="parentId" direction="in"/>
            <arg type="i" name="recursionDepth" direction="in"/>
            <arg type="as" name="propertyNames" direction="in"/>
            <arg type="u" name="revision" direction="out"/>
            <arg type="(ia{sv}av)" name="layout" direction="out"/>
          </method>
          <method name="GetGroupProperties">
            <arg type="ai" name="ids" direction="in"/>
            <arg type="as" name="propertyNames" direction="in"/>
            <arg type="a(ia{sv})" name="properties" direction="out"/>
          </method>
          <method name="GetProperty">
            <arg type="i" name="id" direction="in"/>
            <arg type="s" name="name" direction="in"/>
            <arg type="v" name="value" direction="out"/>
          </method>
          <method name="Event">
            <arg type="i" name="id" direction="in"/>
            <arg type="s" name="eventId" direction="in"/>
            <arg type="v" name="data" direction="in"/>
            <arg type="u" name="timestamp" direction="in"/>
          </method>
          <method name="EventGroup">
            <arg type="a(isvu)" name="events" direction="in"/>
            <arg type="ai" name="idErrors" direction="out"/>
          </method>
          <method name="AboutToShow">
            <arg type="i" name="id" direction="in"/>
            <arg type="b" name="needUpdate" direction="out"/>
          </method>
          <method name="AboutToShowGroup">
            <arg type="ai" name="ids" direction="in"/>
            <arg type="ai" name="updatesNeeded" direction="out"/>
            <arg type="ai" name="idErrors" direction="out"/>
          </method>
          <signal name="ItemsPropertiesUpdated">
            <arg type="a(ia{sv})" name="updatedProps"/>
            <arg type="a(ias)" name="removedProps"/>
          </signal>
          <signal name="LayoutUpdated">
            <arg type="u" name="revision"/>
            <arg type="i" name="parent"/>
          </signal>
          <signal name="ItemActivationRequested">
            <arg type="i" name="id"/>
            <arg type="u" name="timestamp"/>
          </signal>
        </interface>
        """;

    private static void ReplyIntrospect(MethodContext context, string? path)
    {
        static ReadOnlyMemory<byte> Utf8(string s)
            => new(System.Text.Encoding.UTF8.GetBytes(s));

        switch (path)
        {
            case MenuPath:
                context.ReplyIntrospectXml([Utf8(MenuInterfaceXml)], Array.Empty<string>());
                break;
            case ItemPath:
                context.ReplyIntrospectXml([Utf8(ItemInterfaceXml)], Array.Empty<string>());
                break;
            default:
                // The root: advertise the two children so a client walking
                // the tree can find them.
                context.ReplyIntrospectXml([], new[] { "StatusNotifierItem", "MenuBar" });
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_conn is { } conn)
        {
            try { await conn.ReleaseNameAsync(_busName); } catch { }
            conn.Dispose();
        }
        _conn = null;
    }
}
