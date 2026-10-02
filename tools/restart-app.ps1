[CmdletBinding()]
param(
    [ValidateRange(1, 60)]
    [int]$StartupWaitSeconds = 5
)

$ErrorActionPreference = 'Stop'
$repositoryDirectory = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$buildDirectory = Join-Path $repositoryDirectory 'build'
$executable = Join-Path $buildDirectory 'SethsAIUsageMonitor.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw 'Build the signed release with package-release.bat before restarting.'
}
if ((Get-AuthenticodeSignature -LiteralPath $executable).Status -ne 'Valid') {
    throw 'The copied release executable does not have a valid signature.'
}
$alreadyRunning = @(Get-Process -Name SethsAIUsageMonitor -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $executable })
if ($alreadyRunning.Count -gt 0) {
    throw 'The copied build is already running. package-release.bat stops it before replacing it.'
}

# Start-Process inherits the command runner's Windows job. A kill-on-close job can
# terminate the tray app after the agent finishes, despite a successful startup check.
# Ask Windows to detach this specific, signed GUI executable from that job. This
# does not change job limits or launch a visible shell, and fails if breakaway is denied.
if (-not ('AIUsageMonitor.ReleaseLauncher' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace AIUsageMonitor
{
    public static class ReleaseLauncher
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfo
        {
            public uint Size;
            public IntPtr Reserved, Desktop, Title;
            public uint X, Y, XSize, YSize, XChars, YChars, FillAttribute, Flags;
            public ushort ShowWindow, ReservedSize;
            public IntPtr ReservedBytes, StdInput, StdOutput, StdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInfo
        {
            public IntPtr Process, Thread;
            public uint ProcessId, ThreadId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcessW(string application, StringBuilder command,
            IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
            uint creationFlags, IntPtr environment, string directory,
            ref StartupInfo startup, out ProcessInfo process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryInformationJobObject(IntPtr job, int infoClass,
            byte[] information, uint length, out uint returned);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        public static bool BelongsToLauncherJob(int processId)
        {
            bool launcherInJob;
            if (!IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out launcherInJob))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!launcherInJob) return false;
            for (int size = 1024; size <= 1048576; size *= 2)
            {
                var data = new byte[size];
                uint returned;
                if (!QueryInformationJobObject(IntPtr.Zero, 3, data, (uint)size, out returned))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 234) continue; // ERROR_MORE_DATA
                    throw new Win32Exception(error);
                }
                uint count = BitConverter.ToUInt32(data, 4);
                for (int index = 0; index < count; index++)
                {
                    int offset = 8 + index * IntPtr.Size;
                    ulong id = IntPtr.Size == 8 ? BitConverter.ToUInt64(data, offset) : BitConverter.ToUInt32(data, offset);
                    if (id == (ulong)processId) return true;
                }
                return false;
            }
            throw new InvalidOperationException("Could not enumerate the launcher job.");
        }

        public static int Start(string executable, string directory)
        {
            var startup = new StartupInfo { Size = (uint)Marshal.SizeOf(typeof(StartupInfo)) };
            ProcessInfo process;
            const uint BreakawayFromJob = 0x01000000;
            const uint NoConsoleWindow = 0x08000000;
            if (!CreateProcessW(executable, new StringBuilder("\"" + executable + "\""),
                IntPtr.Zero, IntPtr.Zero, false, BreakawayFromJob | NoConsoleWindow,
                IntPtr.Zero, directory, ref startup, out process))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not launch the app independently of the command session.");
            try { return checked((int)process.ProcessId); }
            finally
            {
                CloseHandle(process.Thread);
                CloseHandle(process.Process);
            }
        }
    }
}
'@
}

$launchedId = [AIUsageMonitor.ReleaseLauncher]::Start($executable, $buildDirectory)
$launchedProcess = Get-Process -Id $launchedId -ErrorAction Stop
try {
    if ($launchedProcess.WaitForExit($StartupWaitSeconds * 1000)) {
        throw "The app exited during startup with code $($launchedProcess.ExitCode)."
    }
    # Windows may retain a desktop-wide parent job. Check this launcher's immediate
    # job specifically, not IsProcessInJob(..., null), which includes those parents.
    if ([AIUsageMonitor.ReleaseLauncher]::BelongsToLauncherJob($launchedId)) {
        throw 'The app still belongs to the launcher job; restart persistence is not confirmed.'
    }
    "RESTART_OK PID=$launchedId SIGNATURE=Valid IN_LAUNCHER_JOB=False"
}
finally {
    $launchedProcess.Dispose()
}
