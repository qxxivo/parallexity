namespace PhoneRemote;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.FirstOrDefault() == "--apply-update") { AppUpdates.Apply(args); return; }
        if (args.Contains("--self-test"))
        {
            Environment.ExitCode = SelfTest.RunAsync().GetAwaiter().GetResult();
            return;
        }
        if (args.Contains("--preview"))
        {
            SelfTest.PreviewAsync().GetAwaiter().GetResult();
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowError(e.Exception);
        try { Application.Run(new MainForm()); }
        catch (Exception e) { ShowError(e); }
    }

    private static void ShowError(Exception error)
    {
        MessageBox.Show("Не удалось выполнить действие.\n\n" + error.Message,
            "Phone Remote", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
