using System.Diagnostics;
using QRCoder;

namespace PhoneRemote;

public sealed class MainForm : Form
{
    private static readonly Color Background = Color.FromArgb(18, 18, 21);
    private static readonly Color Card = Color.FromArgb(28, 28, 31);
    private static readonly Color Muted = Color.FromArgb(160, 160, 170);
    private readonly TrustStore trust;
    private readonly RemoteServer server;
    private readonly NotifyIcon tray;
    private readonly ComboBox address = new();
    private readonly PictureBox qr = new();
    private readonly Label status = new(), expiry = new();
    private readonly ListBox devices = new();
    private readonly Button refreshQr;
    private readonly Button updateButton;
    private readonly ToolStripItem updateMenu;
    private bool updating;
    private readonly CancellationTokenSource updateCancellation = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private DateTimeOffset expires;
    private string? pairingUrl;
    private bool exitRequested;
    private bool restartRequested;
    private bool stopping;
    private bool started;
    private volatile bool inputEnabled;

    public MainForm()
    {
        Text = $"parallexity {AppUpdates.CurrentVersion.ToString(3)} · пульт для твоего ПК";
        Size = new(820, 760);
        MinimumSize = new(800, 720);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;
        ForeColor = Color.White;
        Font = new("Segoe UI", 10);
        using (var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("PhoneRemote.assets.parallexity.ico")!)
        using (var logo = new Icon(iconStream)) Icon = (Icon)logo.Clone();
        trust = new(Path.Combine(AppContext.BaseDirectory, "data"));
        server = new(trust, new DesktopControls(), ApproveAsync, () => inputEnabled);
        trust.Changed += () => { if (IsHandleCreated && !IsDisposed) BeginInvoke(UpdateDevices); };

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(26), ColumnCount = 2, RowCount = 4 };
        root.ColumnStyles.Add(new(SizeType.Percent, 53)); root.ColumnStyles.Add(new(SizeType.Percent, 47));
        root.RowStyles.Add(new(SizeType.Absolute, 84)); root.RowStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.Absolute, 56)); root.RowStyles.Add(new(SizeType.Absolute, 45));
        Controls.Add(root);
        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        var brand = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new(0) };
        var brandMark = new PictureBox { Image = Icon.ToBitmap(), Size = new(36, 36), SizeMode = PictureBoxSizeMode.Zoom, Margin = new(0, 0, 10, 0) };
        brand.Controls.Add(brandMark);
        brand.Controls.Add(Label("parallexity", 22, FontStyle.Bold));
        header.Controls.Add(brand);
        FormClosed += (_, _) => brandMark.Image?.Dispose();
        header.Controls.Add(Label("Твой компьютер. Твой телефон. Один пульт.", 10, color: Muted));
        root.Controls.Add(header, 0, 0); root.SetColumnSpan(header, 2);

        var left = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Card, Padding = new(18), FlowDirection = FlowDirection.TopDown, WrapContents = false };
        left.Controls.Add(Label("01  Подключи телефон", 13, FontStyle.Bold));
        left.Controls.Add(Label("Телефон и ПК — в одной домашней сети", 9, color: Muted));
        qr.Size = new(222, 222); qr.SizeMode = PictureBoxSizeMode.Zoom; qr.BackColor = Color.White; qr.Margin = new(0, 10, 0, 6);
        left.Controls.Add(qr);
        expiry.AutoSize = true; expiry.ForeColor = Muted; expiry.Font = new("Segoe UI", 9); left.Controls.Add(expiry);
        address.Width = 300; address.DropDownStyle = ComboBoxStyle.DropDownList; address.Margin = new(0, 8, 0, 6);
        address.SelectedIndexChanged += (_, _) => { if (started) GenerateQr(); }; left.Controls.Add(address);
        refreshQr = Button("Новый QR-код", (_, _) => GenerateQr()); left.Controls.Add(refreshQr);
        root.Controls.Add(left, 0, 1);

        var right = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(18), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        right.Controls.Add(Label("02  Открой пульт", 13, FontStyle.Bold));
        right.Controls.Add(Paragraph("Сканируй QR-код камерой телефона.\nНазови устройство и подтверди\nподключение здесь, на компьютере.", Muted));
        right.Controls.Add(Button("Скопировать ссылку", (_, _) => { if (pairingUrl is not null) { Clipboard.SetText(pairingUrl); status.Text = "Ссылка скопирована. Отправь её своему телефону."; } }));
        right.Controls.Add(Button("Проверить в браузере ПК", (_, _) => { if (pairingUrl is not null) Process.Start(new ProcessStartInfo(pairingUrl) { UseShellExecute = true }); }));
        right.Controls.Add(Label("Подключённые устройства", 11, FontStyle.Bold));
        devices.Size = new(280, 86); devices.BackColor = Card; devices.ForeColor = Color.White; devices.BorderStyle = BorderStyle.None; devices.DisplayMember = "Name";
        right.Controls.Add(devices);
        right.Controls.Add(Button("Отключить выбранное", (_, _) => { if (devices.SelectedItem is TrustedDevice selected) trust.Revoke(selected.Id); }));
        right.Controls.Add(Button("Помощь с сетью", (_, _) => ShowHelp()));
        right.Controls.Add(Button("Перезапустить пульт", (_, _) => Restart()));
        updateButton = Button("Проверить обновления", async (_, _) => await UpdateAsync());
        updateButton.BackColor = Color.FromArgb(243, 243, 245);
        updateButton.ForeColor = Color.FromArgb(24, 24, 27);
        right.Controls.Add(updateButton);
        var inputToggle = new CheckBox { Text = "Разрешить мышь и клавиатуру", Width = 280, Height = 45, ForeColor = Color.White, Margin = new(0, 8, 0, 4) };
        inputToggle.CheckedChanged += (_, _) => inputEnabled = inputToggle.Checked;
        right.Controls.Add(inputToggle);
        right.Controls.SetChildIndex(inputToggle, 4);
        var inputDescription = Paragraph("Для всех подключённых устройств.\nВвод — в активное окно ПК.\nПосле перезапуска разреши заново.", Muted);
        right.Controls.Add(inputDescription);
        right.Controls.SetChildIndex(inputDescription, 5);
        right.Controls.SetChildIndex(updateButton, 4);
        root.Controls.Add(right, 1, 1);
        status.Dock = DockStyle.Fill; status.TextAlign = ContentAlignment.MiddleLeft; status.ForeColor = Color.FromArgb(210, 210, 220); status.Text = "Запускаем пульт…";
        root.Controls.Add(status, 0, 2); root.SetColumnSpan(status, 2);
        var footer = Paragraph("Локальное подключение · без аккаунта и облака\nЗакрытие окна сворачивает программу к часам. Выход — через меню значка.", Muted);
        footer.Dock = DockStyle.Fill; footer.Font = new("Segoe UI", 9); root.Controls.Add(footer, 0, 3); root.SetColumnSpan(footer, 2);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть parallexity", null, (_, _) => Restore());
        updateMenu = menu.Items.Add("Проверить обновления", null, async (_, _) => await UpdateAsync());
        menu.Items.Add("Перезапустить пульт", null, (_, _) => Restart());
        menu.Items.Add("Выход", null, (_, _) => { exitRequested = true; Close(); });
        tray = new() { Icon = Icon, Text = "parallexity — локальный пульт", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => Restore();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        timer.Tick += (_, _) =>
        {
            var seconds = Math.Max(0, (int)(expires - DateTimeOffset.UtcNow).TotalSeconds);
            expiry.Text = seconds > 0 ? $"QR-код действует ещё {seconds} сек." : "Срок истёк — нажми «Новый QR-код»";
        };
        Shown += async (_, _) => await StartAsync();
        FormClosing += async (_, e) =>
        {
            if (!exitRequested && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            if (stopping) return;
            e.Cancel = true; stopping = true; updateCancellation.Cancel(); timer.Stop(); tray.Visible = false;
            await server.DisposeAsync();
            Close();
        };
        FormClosed += (_, _) =>
        {
            tray.Dispose(); timer.Dispose(); qr.Image?.Dispose();
            if (restartRequested)
            {
                try { Process.Start(new ProcessStartInfo(AppUpdates.LaunchPath) { UseShellExecute = true }); }
                catch (Exception error) { MessageBox.Show("Открой parallexity.exe вручную.\n\n" + error.Message, "Не удалось перезапустить пульт", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        };
        UpdateDevices();
    }

    private async Task StartAsync()
    {
        try
        {
            for (var port = 8765; port <= 8775; port++)
            {
                try { await server.StartAsync(port); break; }
                catch (IOException) { await server.DisposeAsync(); if (port == 8775) throw; }
            }
            var addresses = server.Addresses.OrderByDescending(a => a.StartsWith("http://192.168.")).ToArray();
            address.Items.AddRange(addresses.Length > 0 ? addresses : new[] { $"http://127.0.0.1:{server.Port}" });
            address.SelectedIndex = 0;
            started = true;
            GenerateQr(); timer.Start();
            status.Text = addresses.Length > 0 ? "Пульт готов. Сканируй QR-код с телефона." : "Нет локальной сети. Подключи ПК к роутеру и перезапусти программу.";
        }
        catch (Exception)
        {
            status.ForeColor = Color.Salmon;
            status.Text = "Не удалось запустить подключение. Закрой другой экземпляр и попробуй снова.";
            refreshQr.Enabled = false;
        }
    }

    private void GenerateQr()
    {
        if (!started || address.SelectedItem is null) return;
        var token = trust.CreatePairingToken();
        pairingUrl = address.SelectedItem + "/#pair=" + token;
        expires = DateTimeOffset.UtcNow.AddMinutes(2);
        using var data = QRCodeGenerator.GenerateQrCode(pairingUrl, QRCodeGenerator.ECCLevel.M);
        using var code = new QRCode(data);
        var previous = qr.Image;
        qr.Image = code.GetGraphic(8);
        previous?.Dispose();
        status.Text = "Новый QR-код готов. Предыдущий больше не действует.";
    }

    private Task<bool> ApproveAsync(string name, string ip)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (IsDisposed || stopping) { result.TrySetResult(false); return result.Task; }
        BeginInvoke(() =>
        {
            if (stopping) { result.TrySetResult(false); return; }
            Restore();
            using var dialog = new Form { Text = "Подключить телефон?", Size = new(440, 260), StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = Background, ForeColor = Color.White, Font = Font };
            var text = new System.Windows.Forms.Label { Text = $"Устройство: {name}\nАдрес: {ip}\n\nРазрешить управление громкостью и плеерами?\nПодтверждай только свой телефон.", Location = new(20, 20), Size = new(400, 115) };
            dialog.Controls.Add(text);
            var yes = Button("Подключить", (_, _) => dialog.DialogResult = DialogResult.Yes); yes.Location = new(20, 155);
            var no = Button("Отклонить", (_, _) => dialog.DialogResult = DialogResult.No); no.Location = new(220, 155);
            dialog.Controls.Add(yes); dialog.Controls.Add(no); dialog.AcceptButton = yes; dialog.CancelButton = no;
            using var deadline = new System.Windows.Forms.Timer { Interval = 60000 };
            deadline.Tick += (_, _) => dialog.DialogResult = DialogResult.No;
            deadline.Start();
            result.TrySetResult(dialog.ShowDialog(this) == DialogResult.Yes);
        });
        return result.Task;
    }

    private void UpdateDevices()
    {
        devices.Items.Clear(); devices.Items.AddRange(trust.Devices);
        if (devices.Items.Count > 0) devices.SelectedIndex = 0;
    }
    private void Restore() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    private void Restart()
    {
        if (stopping) return;
        restartRequested = true;
        exitRequested = true;
        Close();
    }
    private async Task UpdateAsync()
    {
        if (updating || stopping) return;
        updating = true; updateButton.Enabled = false; updateMenu.Enabled = false;
        Restore();
        try
        {
            status.Text = "Проверяем обновления на GitHub…";
            AppUpdates.CleanOldDownloads();
            var release = await AppUpdates.CheckAsync(updateCancellation.Token);
            if (stopping || IsDisposed) return;
            if (release is null) { status.Text = "Установлена последняя версия parallexity."; return; }
            if (MessageBox.Show(this, $"Доступна версия {release.Version}.\nСкачать {Math.Ceiling(release.Size / 1048576d)} МБ и перезапустить пульт?\nПодключённые устройства сохранятся.", "Обновление parallexity", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
            { status.Text = "Обновление отложено."; return; }
            var progress = new Progress<int>(value => { if (!IsDisposed && !stopping) status.Text = $"Загружаем обновление: {value}%"; });
            var downloaded = await AppUpdates.DownloadAsync(release, progress, updateCancellation.Token);
            if (stopping || IsDisposed) return;
            AppUpdates.StartInstaller(downloaded);
            exitRequested = true; Close();
        }
        catch (Exception error)
        {
            if (!IsDisposed && !stopping) { status.Text = "Обновление не установлено. Пульт продолжает работать."; MessageBox.Show(this, error.Message, "Обновление parallexity", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }
        finally { if (!IsDisposed) { updating = false; updateButton.Enabled = true; updateMenu.Enabled = true; } }
    }
    private void ShowHelp() => MessageBox.Show(this,
        "1. Телефон и ПК должны быть подключены к одному домашнему роутеру. ПК может быть по кабелю.\n\n" +
        "2. Гостевой Wi-Fi часто запрещает связь между устройствами. Используй обычную домашнюю сеть.\n\n" +
        "3. Если Windows спросит о доступе к сети — разреши parallexity только для частных сетей. Если окно не появилось: Безопасность Windows → Брандмауэр → Разрешить работу с приложением → parallexity.exe → Частная сеть. Изменение правила может потребовать прав администратора.\n\n" +
        "4. Если включён VPN/Throne, проверь, что он разрешает доступ к локальной сети. В списке адресов выбери адрес домашнего подключения.\n\n" +
        "5. После смены сети перезапусти программу и отсканируй новый QR-код. На заблокированном экране телефона пульт может потерять связь — при открытии страницы она восстановится.\n\n" +
        "Пульт работает по HTTP в доверенной домашней сети; соединение не шифруется. Не открывай его порт в интернет.",
        "Помощь с подключением", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private static System.Windows.Forms.Label Label(string text, float size, FontStyle style = FontStyle.Regular, Color? color = null)
        => new() { Text = text, AutoSize = true, Font = new("Segoe UI", size, style), ForeColor = color ?? Color.White, Margin = new(0, 0, 0, 8) };
    private static System.Windows.Forms.Label Paragraph(string text, Color color)
        => new() { Text = text, AutoSize = true, MaximumSize = new(600, 0), ForeColor = color, Margin = new(0, 0, 0, 14) };
    private static Button Button(string text, EventHandler click)
    {
        var button = new Button { Text = text, Size = new(190, 35), FlatStyle = FlatStyle.Flat, BackColor = Card, ForeColor = Color.White, Margin = new(0, 0, 0, 9), Cursor = Cursors.Hand };
        button.FlatAppearance.BorderColor = Color.FromArgb(65, 65, 72); button.Click += click; return button;
    }
}
