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

using System.Security.Principal;

namespace Roxa.Core;

public static class AdminHelper
{
    public static bool IsRunningAsAdmin()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        // Unix: only euid 0 means elevated. /proc/self/status is present on
        // every Linux (the field is "Uid:\treal\teffective\tsaved\tfs"); the
        // user-name fallback covers the odd system without procfs, where
        // "root" is the only name that implies privileges.
        try
        {
            foreach (var line in System.IO.File.ReadLines("/proc/self/status"))
            {
                if (!line.StartsWith("Uid:", System.StringComparison.Ordinal)) continue;
                var fields = line.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
                // fields[0] is "Uid:", fields[1] real, fields[2] effective.
                return fields.Length > 2 && fields[2] == "0";
            }
        }
        catch { /* no procfs: fall through to the name check */ }
        return Environment.UserName == "root";
    }

    public static void RestartAsAdmin()
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "",
            Arguments = "--wait-exit",
            Verb = "runas",
            UseShellExecute = true
        };
        try
        {
            System.Diagnostics.Process.Start(psi);
            // After Process.Start succeeds (a declined UAC prompt never gets
            // here); the tray icon must go before Environment.Exit on the UI thread.
            try
            {
                if (Avalonia.Application.Current is Roxa.App app)
                    app.DisposeTray();
            }
            catch { }
            Environment.Exit(0);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User cancelled UAC
        }
    }
}