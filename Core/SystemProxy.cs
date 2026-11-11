// Roxa - a VPN client for Windows built on Xray-core
// Copyright (C) 2026 Roxa contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.
//
// Additional terms under section 7 of that license: the "Roxa" name and
// visual identity are not licensed. See "The name and logo are not
// covered" in README.md.

using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Roxa.Core;

/// <summary>
/// Sets the system proxy. Windows writes the registry; other platforms write
/// the GNOME/GTK settings via gsettings, which is what Firefox, Chromium and
/// every GTK app read on Linux — Xfce has no proxy store of its own, and KDE
/// (kioslaverc) is not yet covered.
/// </summary>
public static class SystemProxy
{
    public static void Set(string proxy)
    {
        if (!OperatingSystem.IsWindows())
        {
            ApplyLinux(enable: true, server: proxy);
            return;
        }
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        key?.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key?.SetValue("ProxyServer", proxy, RegistryValueKind.String);
        key?.SetValue("ProxyOverride", "<local>;localhost;127.0.0.1;*.local", RegistryValueKind.String);
        NotifyChange();
    }

    public static void Clear()
    {
        if (!OperatingSystem.IsWindows())
        {
            ApplyLinux(enable: false, server: null);
            return;
        }
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        key?.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        NotifyChange();
    }

    public static bool GetCurrent(out bool enable, out string server, out string proxyOverride)
    {
        enable = false;
        server = "";
        proxyOverride = "";
        if (!OperatingSystem.IsWindows()) return GetCurrentLinux(out enable, out server);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key == null) return false;
            enable = Convert.ToInt32(key.GetValue("ProxyEnable", 0) ?? 0) == 1;
            server = key.GetValue("ProxyServer") as string ?? "";
            proxyOverride = key.GetValue("ProxyOverride") as string ?? "";
            return true;
        }
        catch { return false; }
    }

    public static void Apply(bool enable, string? server, string? proxyOverride)
    {
        if (!OperatingSystem.IsWindows())
        {
            ApplyLinux(enable, enable ? server : null);
            return;
        }
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key == null) return;
            key.SetValue("ProxyEnable", enable ? 1 : 0, RegistryValueKind.DWord);
            if (server != null) key.SetValue("ProxyServer", server, RegistryValueKind.String);
            if (proxyOverride != null) key.SetValue("ProxyOverride", proxyOverride, RegistryValueKind.String);
            NotifyChange();
        }
        catch { }
    }

    // --- Linux (gsettings) -------------------------------------------------

    /// <summary>Writes the proxy into the GNOME/GTK settings every Linux desktop reads.</summary>
    private static void ApplyLinux(bool enable, string? server)
    {
        if (!enable)
        {
            // Same semantics as ProxyEnable=0 on Windows: mode off, the previous
            // host/port kept in place so re-enabling restores them.
            Gsettings("set", "org.gnome.system.proxy", "mode", "'none'");
            return;
        }
        if (TryParseHostPort(server, out string host, out int port))
        {
            // A single HTTP proxy address serves both schemes here, exactly like
            // one ProxyServer value does on Windows: http:// goes straight, an
            // https:// URL becomes CONNECT to the same port.
            string qh = "'" + host.Replace("'", "") + "'";
            string qp = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Gsettings("set", "org.gnome.system.proxy.http", "host", qh);
            Gsettings("set", "org.gnome.system.proxy.http", "port", qp);
            Gsettings("set", "org.gnome.system.proxy.https", "host", qh);
            Gsettings("set", "org.gnome.system.proxy.https", "port", qp);
        }
        Gsettings("set", "org.gnome.system.proxy", "mode", "'manual'");
    }

    private static bool GetCurrentLinux(out bool enable, out string server)
    {
        enable = false;
        server = "";
        var (okMode, mode) = Gsettings("get", "org.gnome.system.proxy", "mode");
        if (!okMode) return false; // gsettings/dconf unavailable on this system
        enable = mode.Trim().Trim('\'') == "manual";
        var (okHost, host) = Gsettings("get", "org.gnome.system.proxy.http", "host");
        var (okPort, port) = Gsettings("get", "org.gnome.system.proxy.http", "port");
        if (okHost && okPort)
        {
            // gsettings prints strings GVariant-quoted: '127.0.0.1'
            string h = host.Trim().Trim('\'');
            if (h.Length > 0 && int.TryParse(port.Trim(), out int p) && p > 0)
                server = h + ":" + p;
        }
        return true;
    }

    /// <summary>Runs gsettings and returns (success, stdout). Never throws.</summary>
    private static (bool ok, string output) Gsettings(params string[] args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "gsettings",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return (false, "");
            string output = p.StandardOutput.ReadToEnd();
            _ = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(4000))
            {
                try { p.Kill(); } catch { }
                return (false, "");
            }
            return (p.ExitCode == 0, output.Trim());
        }
        catch { return (false, ""); } // gsettings not installed
    }

    /// <summary>Splits "host:port" (also "[v6]:port" and "http://host:port").</summary>
    private static bool TryParseHostPort(string? server, out string host, out int port)
    {
        host = "";
        port = 0;
        if (string.IsNullOrWhiteSpace(server)) return false;
        string v = server.Trim();
        int scheme = v.IndexOf("://", System.StringComparison.Ordinal);
        if (scheme >= 0) v = v[(scheme + 3)..];
        int slash = v.IndexOf('/');
        if (slash >= 0) v = v[..slash];
        if (v.StartsWith('['))
        {
            int close = v.IndexOf(']');
            if (close <= 1 || close + 2 > v.Length || v[close + 1] != ':') return false;
            host = v[1..close];
            return int.TryParse(v[(close + 2)..], out port) && port > 0 && port <= 65535;
        }
        int c = v.LastIndexOf(':');
        if (c <= 0) return false;
        host = v[..c];
        return int.TryParse(v[(c + 1)..], out port) && port > 0 && port <= 65535;
    }

    public static bool IsDeadLoopback(string? server)
    {
        if (string.IsNullOrWhiteSpace(server)) return false;
        bool sawLoopback = false;
        foreach (var part in server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string v = part;
            int eq = v.IndexOf('=');
            if (eq >= 0) v = v[(eq + 1)..];
            v = v.Trim();
            if (v.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) v = v[7..];
            int c = v.LastIndexOf(':');
            if (c <= 0) continue;
            string host = v[..c].Trim();
            // IPv6 addresses come wrapped in brackets: [::1]:8080
            if (host.Length > 1 && host[0] == '[' && host[^1] == ']') host = host[1..^1];
            if (!int.TryParse(v[(c + 1)..].Trim(), out int port) || port <= 0 || port > 65535) continue;
            if (!IsLoopbackHost(host))
            {
                // A non-loopback proxy is not ours (ours is always 127.0.0.1);
                // clearing it would break the user's own settings.
                return false;
            }
            sawLoopback = true;
            if (CanConnect(host, port)) return false;
        }
        return sawLoopback;
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.StartsWith("127.", StringComparison.Ordinal)
        || host == "::1";

    private static bool CanConnect(string host, int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var task = client.ConnectAsync(host, port);
            if (!task.Wait(400)) return false;
            return client.Connected;
        }
        catch { return false; }
    }

    [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    private static void NotifyChange()
    {
        if (!OperatingSystem.IsWindows()) return;
        InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
    }
}
