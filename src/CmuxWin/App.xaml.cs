namespace CmuxWin;

public partial class App : System.Windows.Application
{
    public App()
    {
        // Anchor our process to a job so any conhost/OpenConsole/shell children
        // are reaped automatically when we exit, including ungraceful exits.
        JobObjectGuard.AssignSelfToKillOnCloseJob();
    }
}
