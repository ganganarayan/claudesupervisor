using System.Runtime.InteropServices;

namespace ClaudeSupervisor.Services;

/// <summary>
/// Keeps the machine awake while a resume is scheduled, and puts it to sleep afterwards.
/// </summary>
public static class SystemPower
{
    [Flags]
    private enum ExecutionState : uint
    {
        Continuous = 0x80000000,
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    /// <summary>
    /// Prevents the system (and optionally the display) from sleeping. Keeping the display on
    /// matters: Claude is a GPU-rendered app and can stop drawing when the screen is off.
    /// </summary>
    public static void KeepAwake(bool keepDisplayOn = true)
    {
        uint flags = (uint)(ExecutionState.Continuous | ExecutionState.SystemRequired);
        if (keepDisplayOn)
            flags |= (uint)ExecutionState.DisplayRequired;
        SetThreadExecutionState(flags);
    }

    /// <summary>Clears the keep-awake request so normal power management resumes.</summary>
    public static void AllowSleep() => SetThreadExecutionState((uint)ExecutionState.Continuous);

    /// <summary>Puts the machine into sleep (suspend to RAM).</summary>
    public static bool Sleep()
    {
        AllowSleep(); // don't fight our own keep-awake request
        return SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false);
    }
}
