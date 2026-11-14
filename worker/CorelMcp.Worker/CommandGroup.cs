namespace CorelMcp.Worker;

internal static class CommandGroup
{
    public static void Run(Action begin, Action body, Action end, CleanupRecorder cleanup)
    {
        bool begun = false;
        try { begin(); begun = true; body(); }
        finally { if (begun) cleanup.Try("undo.end_command_group", end); }
    }
}
