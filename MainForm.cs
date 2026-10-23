using System.Diagnostics;
using QRCoder;

namespace PhoneRemote;

public sealed class MainForm : Form
{
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var dark = 1;
        if (DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(Handle, 19, ref dark, sizeof(int));
    }
    private static readonly Color Background = DesktopTheme.Background;
    private static readonly Color Card = DesktopTheme.Card;
    private static readonly Color Muted = DesktopTheme.Muted;
    private readonly TrustStore trust;
    private readonly RemoteServer server;
    private readonly NotifyIcon tray;
    private readonly ComboBox address = new();
    private readonly PictureBox qr = new();
    private readonly Label status = new(), expiry = new();
    private readonly ListBox devices = new();
    private readonly Label emptyDevices = new();
    private readonly Button disconnectButton;
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
    private readonly bool designPreview;
    private bool refreshingAddresses;
    private int networkCheckTicks;

    internal MainForm(DesktopInstance instance, bool startInTray = false, bool designPreview = false)
    {
        this.designPreview = designPreview;
        if (startInTray) { WindowState = FormWindowState.Minimized; ShowInTaskbar = false; }
        Text = designPreview ? "parallexity · предпросмотр дизайна" : $"parallexity {AppUpdates.CurrentVersion.ToString(3)} · пульт для твоего ПК";
        Size = new(920, 900);
        MinimumSize = new(840, 800);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;
        ForeColor = Color.White;
        Font = new("Segoe UI", 10);
        using (var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("PhoneRemote.assets.parallexity.ico")!)
        using (var logo = new Icon(iconStream, 64, 64)) Icon = (Icon)logo.Clone();
        trust = new(Path.Combine(AppContext.BaseDirectory, designPreview ? "test-data/desktop-" + Guid.NewGuid().ToString("N") : "data"));
        server = new(trust, designPreview ? new FakeControls() : new DesktopControls(), ApproveAsync, () => inputEnabled, designPreview ? new ViewingMode(_=>true) : null);
        trust.Changed += () => { if (IsHandleCreated && !IsDisposed) BeginInvoke(UpdateDevices); };

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(24), ColumnCount = 2, RowCount = 4 };
        root.ColumnStyles.Add(new(SizeType.Percent, 50)); root.ColumnStyles.Add(new(SizeType.Percent, 50));
        root.RowStyles.Add(new(SizeType.Absolute, 100)); root.RowStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.Absolute, 48)); root.RowStyles.Add(new(SizeType.Absolute, 64));
        Controls.Add(root);
        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new(4, 10, 0, 0) };
        var brandMark = new PictureBox { Image = Icon.ToBitmap(), Size = new(56, 56), SizeMode = PictureBoxSizeMode.Zoom, Margin = new(0, 0, 14, 0) };
        header.Controls.Add(brandMark);
        var brand = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        brand.Controls.Add(Label("parallexity", 25, FontStyle.Bold));
        brand.Controls.Add(Label(designPreview ? "Предпросмотр · команды ПК имитируются" : "Твой компьютер. Под рукой.", 10, color: Muted));
        header.Controls.Add(brand);
        FormClosed += (_, _) => brandMark.Image?.Dispose();
        root.Controls.Add(header, 0, 0); root.SetColumnSpan(header, 2);

        var leftCard = new RoundedCard { Dock = DockStyle.Fill, Padding = new(24), Margin = new(0, 0, 8, 0) };
        var left = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Card, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        leftCard.Controls.Add(left);
        left.Controls.Add(Label("Подключить телефон", 17, FontStyle.Bold));
        left.Controls.Add(Label("Отсканируй QR-код камерой телефона", 10, color: Muted));
        qr.Size = new(208, 208); qr.SizeMode = PictureBoxSizeMode.Zoom; qr.BackColor = Color.White; qr.Margin = new(50, 20, 0, 16);
        left.Controls.Add(qr);
        expiry.AutoSize = true; expiry.ForeColor = Muted; expiry.Font = new("Segoe UI", 9); expiry.Margin = new(35, 0, 0, 16); left.Controls.Add(expiry);
        var copy = Button("Скопировать ссылку", (_, _) => { if (pairingUrl is not null) { Clipboard.SetText(pairingUrl); status.Text = "Ссылка скопирована. Открой её на телефоне."; } });
        copy.Width = 270; copy.Height = 48; copy.BackColor=DesktopTheme.Text;copy.ForeColor=Background;left.Controls.Add(copy);
        refreshQr = Button("Новый QR-код", (_, _) => GenerateQr()); refreshQr.Width = 270; refreshQr.Height = 48; left.Controls.Add(refreshQr);
        var network = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = 280, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        address.AccessibleName = "Адрес подключения";
        address.Width = 270; address.DropDownStyle = ComboBoxStyle.DropDownList;
        address.BackColor = DesktopTheme.Surface; address.ForeColor = Color.White; address.FlatStyle = FlatStyle.Flat;
        address.SelectedIndexChanged += (_, _) => { if (started && !refreshingAddresses) GenerateQr(); }; network.Controls.Add(address);
        left.Controls.Add(network);
        left.Controls.Add(Paragraph("Телефон и ПК должны быть подключены\nк одному домашнему роутеру.", Muted));
        root.Controls.Add(leftCard, 0, 1);

        var rightCard = new RoundedCard { Dock = DockStyle.Fill, Padding = new(24), Margin = new(8, 0, 0, 0) };
        var right = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Card, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        rightCard.Controls.Add(right);
        right.Controls.Add(Label("Мои устройства", 17, FontStyle.Bold));
        right.Controls.Add(Paragraph("Доверенные телефоны с доступом к пульту.", Muted));
        devices.Size = new(270, 88); devices.BackColor = Card; devices.ForeColor = Color.White; devices.BorderStyle = BorderStyle.None; devices.DisplayMember = "Name";
        devices.DrawMode=DrawMode.OwnerDrawFixed;devices.ItemHeight=48;
        devices.DrawItem+=(_,e)=>
        {
            if(e.Index<0)return;
            using var fill=new SolidBrush(e.State.HasFlag(DrawItemState.Selected)?DesktopTheme.Surface:Card);
            e.Graphics.FillRectangle(fill,e.Bounds);
            var device=devices.Items[e.Index] as TrustedDevice;
            TextRenderer.DrawText(e.Graphics,device?.Name??"Телефон",devices.Font,Rectangle.Inflate(e.Bounds,-12,0),DesktopTheme.Text,TextFormatFlags.VerticalCenter|TextFormatFlags.Left|TextFormatFlags.EndEllipsis);
            if(e.State.HasFlag(DrawItemState.Focus))e.DrawFocusRectangle();
        };
        right.Controls.Add(devices);
        emptyDevices.Text = "Пока нет подключённых телефонов.\nОтсканируй QR-код слева."; emptyDevices.ForeColor = Muted;
        emptyDevices.Size = new(270, 64); emptyDevices.TextAlign = ContentAlignment.MiddleLeft;
        right.Controls.Add(emptyDevices);
        disconnectButton = Button("Отключить выбранное", (_, _) => { if (devices.SelectedItem is TrustedDevice selected) trust.Revoke(selected.Id); });
        disconnectButton.Width = 270; right.Controls.Add(disconnectButton);
        right.Controls.Add(Label("Настройки", 13, FontStyle.Bold));
        var inputToggle = new ToggleSwitch { Text = "Мышь и клавиатура", Width = 270, Height = 48, ForeColor = Color.White, Margin = new(0, 8, 0, 0) };
        inputToggle.CheckedChanged += (_, _) => inputEnabled = inputToggle.Checked;
        right.Controls.Add(inputToggle);
        right.Controls.Add(Paragraph("Ввод в активное окно ПК.\nПосле перезапуска разреши заново.", Muted));
        var startupToggle = new ToggleSwitch { Text = "Запускать с Windows", Width = 270, Height = 48, ForeColor = Color.White, Margin = new(0, 8, 0, 0) };
        try { startupToggle.Checked = DesktopStartup.Enabled; } catch { startupToggle.Enabled = false; }
        var changingStartup = false;
        startupToggle.CheckedChanged += (_, _) =>
        {
            if (changingStartup) return;
            try { DesktopStartup.Enabled = startupToggle.Checked; }
            catch (Exception)
            {
                changingStartup = true; startupToggle.Checked = !startupToggle.Checked; changingStartup = false;
                MessageBox.Show(this, "Не удалось изменить автозапуск. Попробуй ещё раз.", "parallexity", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };
        right.Controls.Add(startupToggle);
        if (designPreview) startupToggle.Enabled = false;
        right.Controls.Add(Paragraph("Сразу в трей, без открытия окна.", Muted));
        var favoritesSetup=Button("Избранное для телефона",(_,_)=>ShowFavorites());favoritesSetup.Width=270;right.Controls.Add(favoritesSetup);
        var browserSetup=Button("Подключить браузер",(_,_)=>ShowBrowserSetup());browserSetup.Width=270;right.Controls.Add(browserSetup);
        var help = Button("Помощь с подключением", (_, _) => ShowHelp()); help.Width = 270; right.Controls.Add(help);
        void FitContents(FlowLayoutPanel panel)
        {
            var available=Math.Max(180,panel.ClientSize.Width-SystemInformation.VerticalScrollBarWidth-4);
            foreach(Control child in panel.Controls)
            {
                if(child==qr){child.Margin=new(Math.Max(0,(available-child.Width)/2),20,0,16);continue;}
                if(child==expiry){child.Margin=new(0,0,0,16);}
                if(child is System.Windows.Forms.Label label){if(label.AutoSize)label.MaximumSize=new(available,0);else label.Width=available;}
                else child.Width=available;
            }
            address.Width=Math.Max(180,available-8);
        }
        left.SizeChanged+=(_,_)=>FitContents(left);right.SizeChanged+=(_,_)=>FitContents(right);
        root.Controls.Add(rightCard, 1, 1);
        status.Dock = DockStyle.Fill; status.TextAlign = ContentAlignment.MiddleLeft; status.ForeColor = Color.FromArgb(210, 210, 220); status.Text = "Запускаем пульт…";
        root.Controls.Add(status, 0, 2); root.SetColumnSpan(status, 2);
        updateButton = Button("Проверить обновления", async (_, _) => await UpdateAsync());
        updateButton.Height = 48; root.Controls.Add(updateButton, 0, 3);
        var footer = Paragraph("Закрытие окна сворачивает пульт в трей.\nВыход и перезапуск — в меню возле часов.", Muted);
        footer.Dock = DockStyle.Fill; footer.Font = new("Segoe UI", 9); root.Controls.Add(footer, 1, 3);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть parallexity", null, (_, _) => Restore());
        menu.Items.Add("Расширение браузера…",null,(_,_)=>ShowBrowserSetup());
        var viewingMenu=menu.Items.Add("Режим просмотра",null,(_,_)=>
        {
            if(!server.SetViewingMode(!server.ViewingEnabled))MessageBox.Show(this,"Windows не разрешила режим просмотра. Попробуй ещё раз.","Режим просмотра");
        });
        updateMenu = menu.Items.Add("Проверить обновления", null, async (_, _) => await UpdateAsync());
        menu.Items.Add("Перезапустить пульт", null, (_, _) => Restart());
        menu.Items.Add("Выход", null, (_, _) => { exitRequested = true; Close(); });
        tray = new() { Icon = Icon, Text = "parallexity — локальный пульт", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => Restore();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        timer.Tick += (_, _) =>
        {
            ((ToolStripMenuItem)viewingMenu).Checked=server.ViewingEnabled;
            if (instance.OpenRequested()) Restore();
            if (started && !stopping && !updating && ++networkCheckTicks % 15 == 0) RefreshAddresses();
            var seconds = Math.Max(0, (int)(expires - DateTimeOffset.UtcNow).TotalSeconds);
            expiry.Text = seconds > 0 ? $"QR-код действует ещё {seconds} сек." : "Срок истёк — нажми «Новый QR-код»";
        };
        Shown += async (_, _) =>
        {
            FitContents(left);FitContents(right);
            if (startInTray) { Hide(); WindowState = FormWindowState.Normal; ShowInTaskbar = true; }
            timer.Start();
            await StartAsync();
        };
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
                try { Process.Start(new ProcessStartInfo(AppUpdates.LaunchPath) { UseShellExecute = true, Arguments = "--restart" }); }
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
                try { await server.StartAsync(designPreview ? 0 : port, designPreview); break; }
                catch (IOException) { await server.DisposeAsync(); if (port == 8775) throw; }
            }
            RefreshAddresses();
            started = true;
            GenerateQr(); timer.Start();
            if (designPreview) File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "test-design-preview.json"), System.Text.Json.JsonSerializer.Serialize(new { url = pairingUrl, fake = true }));
            // The installer can still hold its lock during our first moments.
            // Run on the UI context and stop when an update begins, so no fresh
            // download is ever removed by a delayed startup cleanup.
            _ = CleanupAfterStartupAsync();
            status.Text = address.SelectedItem?.ToString()?.StartsWith("http://127.0.0.1:") == true && !designPreview
                ? "Нет локальной сети. Подключи ПК к роутеру — адрес обновится автоматически."
                : "Пульт готов. Сканируй QR-код с телефона.";
        }
        catch (Exception)
        {
            status.ForeColor = Color.Salmon;
            status.Text = "Не удалось запустить подключение. Закрой другой экземпляр и попробуй снова.";
            refreshQr.Enabled = false;
        }
    }

    private void RefreshAddresses()
    {
        try
        {
            var available = designPreview ? new[] { $"http://127.0.0.1:{server.Port}" } : server.Addresses.OrderByDescending(a => a.StartsWith("http://192.168.")).ThenBy(a => a, StringComparer.Ordinal).ToArray();
            if (available.Length == 0) available = new[] { $"http://127.0.0.1:{server.Port}" };
            if (address.Items.Cast<string>().SequenceEqual(available)) return;
            var previous = address.SelectedItem as string;
            refreshingAddresses = true;
            try
            {
                address.Items.Clear(); address.Items.AddRange(available);
                address.SelectedIndex = Math.Max(0, Array.IndexOf(available, previous));
            }
            finally { refreshingAddresses = false; }
            if (started && previous != address.SelectedItem as string)
            {
                GenerateQr();
                status.Text = available[0].StartsWith("http://127.0.0.1:") && !designPreview
                    ? "Нет локальной сети. Ждём подключение к роутеру…"
                    : "Адрес ПК изменился. Подключи телефон новым QR-кодом.";
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { /* Retry after the network settles. */ }
    }

    private async Task CleanupAfterStartupAsync()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(500);
            if (updating || stopping || IsDisposed) return;
            AppUpdates.CleanOldDownloads(successfulStartup: true);
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
            using var dialog = new Form { Text = "Подключить телефон?", Size = new(440, 300), StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = Background, ForeColor = Color.White, Font = Font };
            var text = new System.Windows.Forms.Label { Text = $"Устройство: {name}\nАдрес: {ip}\n\nРазрешить управление плеерами, громкостью\nи выключение экрана?\nПодтверждай только свой телефон.", Location = new(20, 20), Size = new(400, 155) };
            dialog.Controls.Add(text);
            var yes = Button("Подключить", (_, _) => dialog.DialogResult = DialogResult.Yes); yes.Location = new(20, 195);
            var no = Button("Отклонить", (_, _) => dialog.DialogResult = DialogResult.No); no.Location = new(220, 195);
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
        var selectedId = (devices.SelectedItem as TrustedDevice)?.Id;
        devices.Items.Clear(); devices.Items.AddRange(trust.Devices);
        devices.Visible = devices.Items.Count > 0; emptyDevices.Visible = devices.Items.Count == 0;
        disconnectButton.Enabled = devices.Items.Count > 0;
        disconnectButton.Visible = devices.Items.Count > 0;
        if (devices.Items.Count > 0) devices.SelectedIndex = Math.Max(0, devices.Items.Cast<TrustedDevice>().ToList().FindIndex(d => d.Id == selectedId));
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
        if (designPreview) return;
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
    private void ShowBrowserSetup()
    {
        if(!started||stopping)return;
        Restore();
        using var dialog=new Form{Text="Подключить браузер",Size=new(550,410),StartPosition=FormStartPosition.CenterParent,BackColor=Background,ForeColor=Color.White,Font=Font,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false};
        dialog.Controls.Add(new Label{Text="1. Нажми «Подготовить расширение»: откроется его папка.\n2. В Chrome / Edge / Яндекс включи режим разработчика\n   chrome://extensions → «Загрузить распакованное».\n3. Открой значок расширения и вставь код ниже.\n4. Нажми «Подключить и разрешить управление».\n\nКод действует 2 минуты. Подключение сохраняется\nпосле перезапуска ПК. Подключение по новому коду заменит старый доступ.",Location=new(18,18),Size=new(510,180)});
        var code=new TextBox{Text=server.CreateBrowserCode(),ReadOnly=true,Location=new(18,206),Width=450,BackColor=Card,ForeColor=Color.White};dialog.Controls.Add(code);
        var copy=Button("Скопировать код",(_,_)=>{code.Text=server.CreateBrowserCode();Clipboard.SetText(code.Text);});copy.Location=new(18,248);dialog.Controls.Add(copy);
        var download=Button("Подготовить расширение",(_,_)=>PrepareBrowserExtension());download.Location=new(250,248);download.Width=255;dialog.Controls.Add(download);
        var disconnect=Button("Отключить браузер",(_,_)=>{server.DisconnectBrowser();dialog.Close();});disconnect.Location=new(18,300);dialog.Controls.Add(disconnect);
        dialog.ShowDialog(this);
    }

    private void ShowFavorites()
    {
        using var dialog=new Form {Text="Избранное для телефона",Size=new(600,610),MinimumSize=new(560,580),StartPosition=FormStartPosition.CenterParent,BackColor=Background,ForeColor=DesktopTheme.Text,Font=Font};
        dialog.HandleCreated+=(_,_)=>{var dark=1;if(DwmSetWindowAttribute(dialog.Handle,20,ref dark,sizeof(int))!=0)DwmSetWindowAttribute(dialog.Handle,19,ref dark,sizeof(int));};
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new(24),ColumnCount=1,RowCount=7};
        layout.ColumnStyles.Add(new(SizeType.Percent,100));
        foreach(var height in new[]{64,180,40,44,44,52,60})layout.RowStyles.Add(new(SizeType.Absolute,height));
        dialog.Controls.Add(layout);
        var note=Paragraph("Добавь приложения и сайты здесь.\nНа телефоне они появятся в разделе «ПК».",Muted);note.Dock=DockStyle.Fill;layout.Controls.Add(note,0,0);
        var list=new ListBox{Dock=DockStyle.Fill,BackColor=Card,ForeColor=DesktopTheme.Text,BorderStyle=BorderStyle.None,DisplayMember="Name",IntegralHeight=false,AccessibleName="Сохранённое избранное",DrawMode=DrawMode.OwnerDrawFixed,ItemHeight=40};layout.Controls.Add(list,0,1);
        list.DrawItem+=(_,e)=>{if(e.Index<0)return;using var fill=new SolidBrush(e.State.HasFlag(DrawItemState.Selected)?DesktopTheme.Surface:Card);e.Graphics.FillRectangle(fill,e.Bounds);TextRenderer.DrawText(e.Graphics,((SavedFavorite)list.Items[e.Index]).Name,list.Font,Rectangle.Inflate(e.Bounds,-8,0),DesktopTheme.Text,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);if(e.State.HasFlag(DrawItemState.Focus))e.DrawFocusRectangle();};
        var remove=Button("Удалить выбранное",(_,_)=>{});remove.Dock=DockStyle.Fill;layout.Controls.Add(remove,0,2);
        var name=new TextBox{Dock=DockStyle.Fill,PlaceholderText="Название, например «Музыка»",MaxLength=40,BackColor=DesktopTheme.Surface,ForeColor=DesktopTheme.Text,BorderStyle=BorderStyle.FixedSingle,AccessibleName="Название ярлыка"};layout.Controls.Add(name,0,3);
        var link=new TextBox{Dock=DockStyle.Fill,PlaceholderText="https://… — для ссылки",MaxLength=2048,BackColor=DesktopTheme.Surface,ForeColor=DesktopTheme.Text,AccessibleName="Адрес сайта"};layout.Controls.Add(link,0,4);
        var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};layout.Controls.Add(actions,0,5);
        list.SelectedIndexChanged+=(_,_)=>remove.Enabled=list.SelectedItem is SavedFavorite;
        void Reload(){list.Items.Clear();list.Items.AddRange(server.ComputerForSettings.Favorites.Items);if(list.Items.Count>0)list.SelectedIndex=0;remove.Enabled=list.SelectedItem is SavedFavorite;}
        void Add(string kind,string target)
        {
            try{server.ComputerForSettings.Favorites.Add(name.Text,kind,target);name.Clear();link.Clear();Reload();}
            catch(Exception error)when(error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException){MessageBox.Show(dialog,error.Message,"Не удалось сохранить",MessageBoxButtons.OK,MessageBoxIcon.Information);}
        }
        var app=Button("Выбрать приложение…",(_,_)=>
        {
            using var picker=new OpenFileDialog{Title="Выбрать приложение для избранного",Filter="Приложение Windows (*.exe)|*.exe",CheckFileExists=true,Multiselect=false};
            if(picker.ShowDialog(dialog)!=DialogResult.OK)return;
            if(string.IsNullOrWhiteSpace(name.Text))name.Text=Path.GetFileNameWithoutExtension(picker.FileName);
            Add("app",picker.FileName);
        });
        var website=Button("Добавить ссылку",(_,_)=>Add("link",link.Text));actions.Controls.Add(app);actions.Controls.Add(website);
        remove.Click+=(_,_)=>
        {
            if(list.SelectedItem is not SavedFavorite favorite)return;
            try{server.ComputerForSettings.Favorites.Remove(favorite.Id);Reload();}
            catch(Exception error)when(error is IOException or UnauthorizedAccessException){MessageBox.Show(dialog,error.Message,"Не удалось сохранить");}
        };
        layout.Controls.Add(Paragraph("До 24 ярлыков. Приложения — локальные EXE без параметров.\nСсылки — только http и https. Нажатие запускает их на ПК.",Muted),0,6);
        Reload();dialog.ShowDialog(this);
    }

    private void PrepareBrowserExtension()
    {
        try
        {
            var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"parallexity","browser-extension");
            Directory.CreateDirectory(folder);
            var assembly=typeof(MainForm).Assembly;
            const string prefix="PhoneRemote.browser-extension.";
            foreach(var resource in assembly.GetManifestResourceNames().Where(name=>name.StartsWith(prefix,StringComparison.Ordinal)))
            {
                var name=resource[prefix.Length..];
                if(Path.GetFileName(name)!=name)throw new InvalidOperationException("Некорректное имя файла расширения.");
                using var source=assembly.GetManifestResourceStream(resource)!;
                using var destination=File.Create(Path.Combine(folder,name));source.CopyTo(destination);
            }
            Clipboard.SetText(folder);
            Process.Start(new ProcessStartInfo(folder){UseShellExecute=true});
            MessageBox.Show(this,"Папка расширения готова, её путь скопирован.\n\nВ Chrome открой chrome://extensions (в Edge — edge://extensions). Включи «Режим разработчика» → «Загрузить распакованное» и вставь путь папки.\n\nЕсли расширение уже установлено из этой папки, нажми кнопку перезагрузки на его карточке.","Подключить расширение",MessageBoxButtons.OK,MessageBoxIcon.Information);
        }
        catch(Exception error){MessageBox.Show(this,"Не удалось подготовить расширение: "+error.Message,"Расширение браузера",MessageBoxButtons.OK,MessageBoxIcon.Error);}
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
        var button = new RoundedButton { Text = text, Size = new(190, 48), BackColor = DesktopTheme.Surface, ForeColor = DesktopTheme.Text, Margin = new(0, 0, 0, 8), Cursor = Cursors.Hand };
        button.FlatAppearance.BorderColor = Color.FromArgb(65, 65, 72); button.Click += click; return button;
    }
}
