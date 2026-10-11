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
            var portIndex = Array.IndexOf(args, "--preview-port");
            var port = portIndex >= 0 && int.TryParse(args.ElementAtOrDefault(portIndex + 1), out var parsed) && parsed is >0 and <=65535 ? parsed : 0;
            SelfTest.PreviewAsync(port).GetAwaiter().GetResult();
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowError(e.Exception);
        try
        {
            var designPreview = args.Contains("--design-preview");
            using var instance = new DesktopInstance(designPreview ? "preview-" + Guid.NewGuid().ToString("N") : null);
            if (!instance.Acquire(args.Contains("--restart") ? 3000 : 0))
            {
                if (!args.Contains("--startup")) instance.RequestOpen();
                return;
            }
            Application.Run(new MainForm(instance, args.Contains("--startup"), designPreview));
        }
        catch (Exception e) { ShowError(e); }
    }

    private static void ShowError(Exception error)
    {
        MessageBox.Show("Не удалось выполнить действие.\n\n" + error.Message,
            "Phone Remote", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
