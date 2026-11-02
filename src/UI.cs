using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DvsRepair
{
    internal static class PaintKit
    {
        internal static readonly Color Ink = Color.FromArgb(31, 43, 67);
        internal static readonly Color Muted = Color.FromArgb(68, 83, 111);
        internal static readonly Color Teal = Color.FromArgb(13, 97, 101);
        internal static readonly Color Error = Color.FromArgb(147, 33, 60);
        internal static LinearGradientBrush Hologram(RectangleF rect, float angle)
        {
            var brush = new LinearGradientBrush(rect, Color.White, Color.White, angle);
            brush.InterpolationColors = new ColorBlend {
                Colors = new[] { Color.FromArgb(211, 235, 243), Color.FromArgb(249, 251, 254), Color.FromArgb(195, 237, 238), Color.FromArgb(216, 211, 247), Color.FromArgb(255, 224, 233), Color.FromArgb(246, 249, 250), Color.FromArgb(189, 217, 235) },
                Positions = new[] { 0f, .17f, .34f, .5f, .66f, .82f, 1f }
            };
            return brush;
        }
        internal static GraphicsPath Round(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float diameter = radius * 2;
            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class ChromeButton : Button
    {
        private bool pressed;
        private readonly float scale;
        private readonly System.Windows.Forms.Timer animation = new System.Windows.Forms.Timer { Interval = 15 };
        private readonly Stopwatch animationClock = new Stopwatch();
        private float hoverAmount;
        private float animationFrom;
        private float animationTarget;
        private Bitmap chromeSurface;
        internal float HoverAmount { get { return hoverAmount; } }
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint action, uint parameter, [MarshalAs(UnmanagedType.Bool)] ref bool value, uint flags);
        private static bool AllowsAnimation()
        {
            bool enabled = true;
            SystemParametersInfo(0x1042, 0, ref enabled, 0); // SPI_GETCLIENTAREAANIMATION
            return enabled;
        }
        internal ChromeButton(float uiScale)
        {
            scale = uiScale;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Opaque, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            AccessibleName = "СДЕЛАТЬ КРАСИВО";
            AccessibleDescription = "Установить DVS и Dante Controller, сохранить резервные копии, заменить встроенные файлы и предложить перезагрузку.";
            Text = AccessibleName;
            Font = new Font("Bahnschrift", 24 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            animation.Tick += (sender, args) => {
                float t = Math.Min(1f, (float)animationClock.Elapsed.TotalMilliseconds / 220f);
                float eased = 1f - (float)Math.Pow(1f - t, 3);
                hoverAmount = animationFrom + (animationTarget - animationFrom) * eased;
                Invalidate();
                if (t >= 1) animation.Stop();
            };
        }
        private void AnimateHover(float target)
        {
            if (!Enabled) return;
            animation.Stop();
            animationTarget = target;
            animationFrom = hoverAmount;
            if (SystemInformation.HighContrast || !AllowsAnimation())
            {
                hoverAmount = target;
                Invalidate();
                return;
            }
            animationClock.Restart();
            animation.Start();
        }
        internal void PreviewHover(float amount) { animation.Stop(); hoverAmount = Math.Max(0, Math.Min(1, amount)); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { AnimateHover(1); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { pressed = false; AnimateHover(0); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnMouseCaptureChanged(EventArgs e) { pressed = false; Invalidate(); base.OnMouseCaptureChanged(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { pressed = true; Invalidate(); } base.OnKeyDown(e); }
        protected override void OnKeyUp(KeyEventArgs e) { pressed = false; Invalidate(); base.OnKeyUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { if (!Enabled) { animation.Stop(); hoverAmount = 0; pressed = false; } Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (chromeSurface != null) { chromeSurface.Dispose(); chromeSurface = null; }
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Paint the actual parent surface at the button's coordinates. Unpainted
            // corner pixels must not retain a native default-button black rectangle.
            if (Parent == null) { e.Graphics.Clear(SystemColors.Control); return; }
            GraphicsState saved = e.Graphics.Save();
            e.Graphics.TranslateTransform(-Left, -Top);
            InvokePaintBackground(Parent, new PaintEventArgs(e.Graphics, Bounds));
            e.Graphics.Restore(saved);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            // Own the complete rectangular surface, including transparent corners.
            // A native Region has binary edge pixels and makes curved borders jagged.
            OnPaintBackground(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF rect = new RectangleF(scale, scale, Width - 2 * scale, Height - 2 * scale);
            using (GraphicsPath path = PaintKit.Round(rect, 12 * scale))
            {
                Color ink = SystemInformation.HighContrast ? SystemColors.ControlText : PaintKit.Ink;
                Color disabledInk = SystemInformation.HighContrast ? SystemColors.GrayText : PaintKit.Muted;
                Color edge = Color.FromArgb((int)(137 - 46 * hoverAmount), (int)(159 - 20 * hoverAmount), (int)(182 + 27 * hoverAmount));
                const int sampling = 3;
                if (chromeSurface == null) chromeSurface = new Bitmap(Width * sampling, Height * sampling, PixelFormat.Format32bppPArgb);
                using (Graphics chrome = Graphics.FromImage(chromeSurface))
                {
                    chrome.Clear(Color.Transparent);
                    chrome.ScaleTransform(sampling, sampling);
                    chrome.SmoothingMode = SmoothingMode.AntiAlias;
                    chrome.CompositingQuality = CompositingQuality.HighQuality;
                    chrome.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    if (SystemInformation.HighContrast)
                    {
                        using (var solid = new SolidBrush(SystemColors.Control)) chrome.FillPath(solid, path);
                    }
                    else
                    {
                        using (var brush = PaintKit.Hologram(rect, 23 - hoverAmount * 11)) chrome.FillPath(brush, path);
                        if (hoverAmount > 0)
                            using (var highlight = new LinearGradientBrush(rect, Color.FromArgb((int)(hoverAmount * 105), Color.White), Color.FromArgb((int)(hoverAmount * 28), 163, 220, 250), 90)) chrome.FillPath(highlight, path);
                        if (pressed) using (var shade = new SolidBrush(Color.FromArgb(25, 40, 70, 100))) chrome.FillPath(shade, path);
                        if (!Enabled) using (var wash = new SolidBrush(Color.FromArgb(135, 241, 245, 249))) chrome.FillPath(wash, path);
                    }
                    using (var pen = new Pen(SystemInformation.HighContrast ? SystemColors.ControlText : edge, scale)) chrome.DrawPath(pen, path);
                }
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using (var attributes = new ImageAttributes())
                {
                    attributes.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(chromeSurface, ClientRectangle, 0, 0, chromeSurface.Width, chromeSurface.Height, GraphicsUnit.Pixel, attributes);
                }
                var textRect = new Rectangle((int)(46 * scale), pressed ? (int)(2 * scale) : 0, Width - (int)(104 * scale), Height);
                TextRenderer.DrawText(g, Text, Font, textRect, Enabled ? ink : disabledInk, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                using (var pen = new Pen(Enabled ? ink : disabledInk, 1.8f * scale))
                {
                    float x = Width - 39 * scale, y = Height / 2f;
                    g.DrawLine(pen, x - 14 * scale, y, x + 4 * scale, y);
                    g.DrawLines(pen, new[] { new PointF(x - 2 * scale, y - 6 * scale), new PointF(x + 4 * scale, y), new PointF(x - 2 * scale, y + 6 * scale) });
                    float sx = 27 * scale, sy = Height / 2f - 7 * scale;
                    PointF[] shield = { new PointF(sx, sy), new PointF(sx + 10 * scale, sy - 3 * scale), new PointF(sx + 20 * scale, sy), new PointF(sx + 18 * scale, sy + 12 * scale), new PointF(sx + 10 * scale, sy + 18 * scale), new PointF(sx + 2 * scale, sy + 12 * scale) };
                    g.DrawPolygon(pen, shield);
                }
                if (Focused && ShowFocusCues)
                {
                    Rectangle focus = Rectangle.Inflate(ClientRectangle, -(int)(5 * scale), -(int)(5 * scale));
                    using (GraphicsPath focusPath = PaintKit.Round(focus, 8 * scale))
                    using (var focusPen = new Pen(SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(38, 91, 149), 2 * scale)) g.DrawPath(focusPen, focusPath);
                }
            }
        }
        protected override void Dispose(bool disposing) { if (disposing) { animation.Dispose(); if (chromeSurface != null) chromeSurface.Dispose(); } base.Dispose(disposing); }
    }

    internal sealed class MainForm : Form
    {
        internal const string AuthorUrl = "https://github.com/TapeStereo";
        private readonly float uiScale;
        private readonly ChromeButton action;
        private readonly LinkLabel repairOnly;
        private readonly Label status;
        private readonly Label detail;
        private readonly Label location;
        private readonly Label[] stepLabels = new Label[3];
        private bool busy;
        private bool completed;
        private int percent;
        private readonly string operationOnShow;
        private readonly ToolTip tips = new ToolTip();
        private readonly Bitmap brandLogo;
        private static readonly string[] steps = { "Установка DVS", "Установка Dante Controller", "Лечение от жадности" };

        internal MainForm(bool autoApply, float forcedScale) : this(autoApply ? "/apply" : "", forcedScale) { }
        internal MainForm(string autoOperation, float forcedScale)
        {
            operationOnShow = autoOperation;
            if (forcedScale > 0) uiScale = forcedScale;
            else
            {
                using (Graphics screen = Graphics.FromHwnd(IntPtr.Zero)) uiScale = screen.DpiX / 96f;
                Rectangle area = Screen.PrimaryScreen.WorkingArea;
                uiScale = Math.Min(uiScale, Math.Min((area.Width - 32) / 900f, (area.Height - 50) / 680f));
                uiScale = Math.Max(.75f, uiScale);
            }
            AutoScaleMode = AutoScaleMode.None;
            Text = "Dante PWND Pack · v1.1";
            AccessibleName = "Dante PWND Pack";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(900), S(680));
            BackColor = Color.FromArgb(237, 243, 250);
            DoubleBuffered = true;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            using (Stream logoStream = typeof(MainForm).Assembly.GetManifestResourceStream("DvsRepair.Logo.png"))
            {
                if (logoStream == null) throw new InvalidDataException("Встроенный логотип не найден.");
                using (Image loadedLogo = Image.FromStream(logoStream)) brandLogo = new Bitmap(loadedLogo);
            }

            AddLabel("DVS 4.4.1.3\nDante Controller 4.15.0.10", 44, 110, 552, 74, 24, FontStyle.Bold);
            AddLabel("Файлы сервиса и лицензии - внутри программы.\nИсходные файлы будут сохранены в backup.", 44, 199, 552, 66, 17, FontStyle.Regular).ForeColor = InkMuted;
            AddLabel("После выполнения обязательно перезагрузите ПК.", 44, 277, 552, 23, 13, FontStyle.Regular).ForeColor = InkMuted;
            AddLabel("DANTE PWND PACK", 44, 18, 230, 24, 13, FontStyle.Bold);
            var author = new LinkLabel {
                Text = "Developed by TapeStereo", AccessibleName = "Developed by TapeStereo",
                AccessibleDescription = "Открыть профиль TapeStereo на GitHub",
                Bounds = Box(500, 18, 356, 24), Font = new Font("Segoe UI", 13 * uiScale, FontStyle.Regular, GraphicsUnit.Pixel),
                BackColor = Color.Transparent, ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : PaintKit.Ink,
                LinkColor = SystemInformation.HighContrast ? SystemColors.HotTrack : Color.FromArgb(38, 91, 149),
                ActiveLinkColor = SystemInformation.HighContrast ? SystemColors.HotTrack : PaintKit.Teal,
                VisitedLinkColor = SystemInformation.HighContrast ? SystemColors.HotTrack : Color.FromArgb(38, 91, 149),
                LinkBehavior = LinkBehavior.HoverUnderline, TextAlign = ContentAlignment.MiddleRight, TabIndex = 2
            };
            author.Links.Clear();
            author.Links.Add("Developed by ".Length, "TapeStereo".Length, AuthorUrl);
            author.TextChanged += (sender, args) => author.AccessibleName = author.Text;
            author.LinkClicked += (sender, args) => {
                try { Process.Start(new ProcessStartInfo((string)args.Link.LinkData) { UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show(this, "Не удалось открыть GitHub в браузере.\r\n" + AuthorUrl + "\r\n\r\n" + ex.Message, "Dante PWND Pack · GitHub", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            };
            Controls.Add(author);
            AddLabel("ПАПКА УСТАНОВКИ · АВТОПОИСК", 44, 315, 650, 20, 11, FontStyle.Bold).ForeColor = InkMuted;
            location = AddLabel("Определяем папку Audinate…", 44, 341, 812, 28, 15, FontStyle.Regular);
            location.AutoEllipsis = true;
            int[] stepX = { 44, 318, 612 };
            int[] stepWidth = { 240, 260, 244 };
            for (int i = 0; i < steps.Length; i++)
                stepLabels[i] = AddLabel((i + 1).ToString("00") + "   " + steps[i], stepX[i], 400, stepWidth[i], 25, 13, FontStyle.Regular);

            action = new ChromeButton(uiScale) { Bounds = Box(44, 457, 812, 74), TabIndex = 0 };
            action.Click += async (sender, args) => await ApplyAsync(true);
            Controls.Add(action);
            AcceptButton = action;
            repairOnly = new LinkLabel {
                Text = "DVS уже установлена — только заменить файлы", AccessibleName = "DVS уже установлена — только заменить файлы",
                AccessibleDescription = "Сохранить backup и заменить файлы без запуска установщиков.",
                Bounds = Box(44, 537, 812, 36), Font = new Font("Segoe UI", 14 * uiScale, FontStyle.Regular, GraphicsUnit.Pixel),
                BackColor = Color.Transparent, LinkBehavior = LinkBehavior.HoverUnderline, TextAlign = ContentAlignment.MiddleCenter,
                LinkColor = SystemInformation.HighContrast ? SystemColors.HotTrack : Color.FromArgb(38, 91, 149),
                ActiveLinkColor = SystemInformation.HighContrast ? SystemColors.HotTrack : PaintKit.Teal, TabIndex = 1
            };
            repairOnly.TextChanged += (sender, args) => repairOnly.AccessibleName = repairOnly.Text;
            repairOnly.LinkClicked += async (sender, args) => await ApplyAsync(false);
            Controls.Add(repairOnly);
            status = AddLabel("Готово к запуску", 44, 587, 812, 25, 16, FontStyle.Bold);
            detail = AddLabel("Оба установщика внутри. Windows запросит права администратора.", 44, 616, 812, 23, 13, FontStyle.Regular);
            detail.ForeColor = InkMuted;
            AddLabel("v1.1   /   WINDOWS x64", 44, 654, 300, 18, 11, FontStyle.Regular).ForeColor = InkMuted;
            try { Installation install = Installation.Locate(); location.Text = install.DirectoryPath; location.ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : PaintKit.Teal; }
            catch { location.Text = "DVS не найдена — «Сделать красиво» установит её автоматически."; location.ForeColor = InkMuted; }
            tips.SetToolTip(location, location.Text);
            Shown += async (sender, args) => { if (operationOnShow == "/apply" || operationOnShow == "/setup") await ApplyAsync(operationOnShow == "/setup"); };
            FormClosing += (sender, args) => { if (busy) { args.Cancel = true; status.Text = "Дождитесь завершения текущей операции"; } };
        }

        private Color InkMuted { get { return SystemInformation.HighContrast ? SystemColors.WindowText : PaintKit.Muted; } }
        private int S(float value) { return (int)Math.Round(value * uiScale); }
        private Rectangle Box(float x, float y, float w, float h) { return new Rectangle(S(x), S(y), S(w), S(h)); }
        private Label AddLabel(string text, int x, int y, int w, int h, int size, FontStyle style)
        {
            var label = new Label { Text = text, Bounds = Box(x, y, w, h), Font = new Font("Segoe UI", size * uiScale, style, GraphicsUnit.Pixel), ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : PaintKit.Ink, BackColor = Color.Transparent, AutoSize = false, UseMnemonic = false, AccessibleName = text };
            label.TextChanged += (sender, args) => label.AccessibleName = label.Text;
            Controls.Add(label);
            return label;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.ScaleTransform(uiScale, uiScale);
            if (SystemInformation.HighContrast) { g.Clear(SystemColors.Window); return; }
            using (var background = new LinearGradientBrush(new Rectangle(0, 0, 900, 680), Color.FromArgb(243, 248, 252), Color.FromArgb(226, 234, 247), 115)) g.FillRectangle(background, 0, 0, 900, 680);
            using (var top = PaintKit.Hologram(new RectangleF(0, 0, 900, 58), 6)) g.FillRectangle(top, 0, 0, 900, 58);
            using (var pen = new Pen(Color.FromArgb(174, 190, 210), 1))
            {
                g.DrawLine(pen, 0, 58, 900, 58);
                g.DrawLine(pen, 44, 385, 856, 385);
            }
            DrawLogo(g);
            using (var baseLine = new Pen(Color.FromArgb(191, 204, 220), 3)) g.DrawLine(baseLine, 44, 440, 856, 440);
            if (percent > 0)
            {
                using (var progressLine = new Pen(completed ? PaintKit.Teal : Color.FromArgb(53, 104, 167), 3)) g.DrawLine(progressLine, 44, 440, 44 + 812 * percent / 100f, 440);
            }
        }

        private void DrawLogo(Graphics g)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(brandLogo, new Rectangle(612, 74, 252, 252));
        }

        private void UpdateProgress(RepairProgress value)
        {
            percent = value.Percent;
            status.Text = value.Title;
            detail.Text = value.Detail;
            status.AccessibleName = value.Title;
            detail.AccessibleName = value.Detail;
            for (int i = 0; i < stepLabels.Length; i++)
                stepLabels[i].ForeColor = !SystemInformation.HighContrast && ((i == 0 && percent >= 40) || (i == 1 && percent >= 70) || (i == 2 && percent == 100)) ? PaintKit.Teal : InkMuted;
            Invalidate();
        }

        private async Task ApplyAsync(bool fullSetup)
        {
            if (busy || completed) return;
            if (!RepairEngine.IsAdministrator())
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Application.ExecutablePath, fullSetup ? "/setup" : "/apply") { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory });
                    Close();
                }
                catch (Win32Exception ex)
                {
                    status.Text = ex.NativeErrorCode == 1223 ? "Запуск отменён" : "Не удалось запросить права администратора";
                    detail.Text = ex.NativeErrorCode == 1223 ? "Файлы не изменены. Можно нажать кнопку ещё раз." : ex.Message;
                }
                return;
            }
            Mutex operation = null;
            bool ownsMutex = false;
            SetupEngine setup = fullSetup ? new SetupEngine() : null;
            RepairEngine engine = fullSetup ? setup.Repairer : new RepairEngine();
            try
            {
                operation = new Mutex(false, @"Global\DVSRepair_v01_Operation");
                try { ownsMutex = operation.WaitOne(0); } catch (AbandonedMutexException) { ownsMutex = true; }
                if (!ownsMutex) { status.Text = "Операция уже выполняется в другом окне"; detail.Text = "Дождитесь завершения запущенного экземпляра программы."; return; }
                busy = true;
                status.ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : PaintKit.Ink;
                action.Enabled = false;
                repairOnly.Enabled = false;
                for (int i = 0; i < stepLabels.Length; i++) stepLabels[i].Text = (i + 1).ToString("00") + "   " + (fullSetup ? steps[i] : new[] { "Остановка DVS Manager", "Резервная копия", "Лечение от жадности" }[i]);
                UpdateProgress(new RepairProgress(1, "Подготовка", "Определяем установленную DVS."));
                var progress = new Progress<RepairProgress>(UpdateProgress);
                await Task.Run(() => {
                    if (fullSetup) setup.Run(progress);
                    else {
                        SetupInventory inventory = SetupInventory.Read();
                        SetupWorkflow.CheckCompatibleDvs(inventory.Dvs);
                        if (!inventory.Dvs.Found || inventory.Dvs.Version != InstallerBuildInfo.Dvs.Version)
                            throw new InvalidOperationException("Для замены файлов нужна DVS " + InstallerBuildInfo.Dvs.Version + ". Используйте «Сделать красиво» для установки.");
                        new SetupEngine().CheckDvsReady();
                        engine.Run(progress);
                    }
                });
                busy = false;
                completed = true;
                UpdateProgress(new RepairProgress(100, "Файлы заменены. Требуется перезагрузка", "Исходные файлы сохранены в backup. Путь — при наведении на эту строку."));
                tips.SetToolTip(detail, "Backup сервиса: " + engine.BackupPath + "\r\nBackup лицензии: " + engine.LicensingBackupPath + "\r\nЖурнал: " + engine.LogPath + (setup == null ? "" : "\r\nУстановка: " + setup.LogPath));
                try { location.Text = Installation.Locate().DirectoryPath; tips.SetToolTip(location, location.Text); } catch { }
                DialogResult restart = MessageBox.Show(this, "Файлы DVS заменены и проверены.\r\nРезервные копии сохранены:\r\n" + engine.BackupPath + "\r\n" + engine.LicensingBackupPath + "\r\n\r\nДля завершения обязательно перезагрузите ПК.\r\nПерезагрузить компьютер сейчас?\r\nПеред перезагрузкой сохраните открытые документы.", "Dante PWND Pack · Готово", MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2);
                if (restart == DialogResult.Yes)
                {
                    try
                    {
                        using (Process shutdown = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/r /t 0") { UseShellExecute = false, CreateNoWindow = true }))
                        {
                            if (shutdown.WaitForExit(5000) && shutdown.ExitCode != 0) throw new IOException("Windows отклонила запрос перезагрузки.");
                        }
                        detail.Text = "Windows выполняет перезагрузку.";
                    }
                    catch (Exception restartError)
                    {
                        detail.Text = "Для завершения обязательно перезагрузите ПК вручную.";
                        MessageBox.Show(this, "Файлы DVS заменены успешно. Автоматическая перезагрузка не выполнена:\r\n" + restartError.Message + "\r\n\r\nПерезагрузите компьютер вручную.", "Dante PWND Pack · Перезагрузка", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                else detail.Text = "Обязательно перезагрузите ПК. Исходные файлы сохранены в backup.";
            }
            catch (Exception ex)
            {
                busy = false;
                percent = 0;
                status.Text = "Выполнение прервано";
                status.ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : PaintKit.Error;
                detail.Text = "Наведите на эту строку, чтобы увидеть путь к журналу.";
                string log = (setup == null ? null : setup.LogPath) ?? engine.LogPath;
                tips.SetToolTip(detail, log ?? "Журнал ещё не создан: ошибка до начала операции.");
                MessageBox.Show(this, ex.Message + (fullSetup ? "\r\n\r\nУже установленные компоненты сохраняются. Можно повторить запуск комплекта." : "") + (log == null ? "" : "\r\n\r\nЖурнал:\r\n" + log), "Dante PWND Pack · Не удалось завершить", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                busy = false;
                action.Enabled = !completed;
                repairOnly.Enabled = !completed;
                if (ownsMutex) operation.ReleaseMutex();
                if (operation != null) operation.Dispose();
                Invalidate();
            }
        }

        internal void SetPreviewState(string state)
        {
            if (state == "progress") { busy = true; action.Enabled = false; repairOnly.Enabled = false; UpdateProgress(new RepairProgress(40, "Устанавливаем Dante Controller 4.15.0.10", "Идёт установка · прошло 01:23. Не выключайте ПК.")); }
            if (state == "success") { completed = true; action.Enabled = false; repairOnly.Enabled = false; UpdateProgress(new RepairProgress(100, "Файлы заменены. Требуется перезагрузка", "Обязательно перезагрузите ПК. Исходные файлы сохранены в backup.")); }
            if (state == "error") { status.Text = "Не удалось завершить. Исходные файлы восстановлены."; status.ForeColor = PaintKit.Error; detail.Text = "Причина ошибки и путь к резервной копии показаны в сообщении."; }
            if (state == "hover") action.PreviewHover(1);
            if (state == "hover-mid") action.PreviewHover(.5f);
        }

        internal void Snapshot(string output)
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-30000, -30000);
            ShowInTaskbar = false;
            Show();
            Application.DoEvents();
            Update();
            using (var bitmap = new Bitmap(Width, Height))
            {
                DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
                bitmap.Save(output, ImageFormat.Png);
            }
            Hide();
        }
        protected override void Dispose(bool disposing) { if (disposing) { tips.Dispose(); if (brandLogo != null) brandLogo.Dispose(); } base.Dispose(disposing); }
    }

    internal static class Program
    {
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                if (args.Length > 0 && args[0] == "/self-test") return SelfTests.Run(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "Dante-PWND-Pack-tests.txt"));
                if (args.Length > 1 && args[0] == "/render")
                {
                    float scale = args.Length > 2 ? Single.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 1;
                    if (scale < .5 || scale > 3) throw new ArgumentOutOfRangeException("scale");
                    using (var form = new MainForm(false, scale))
                    {
                        if (args.Length > 3) form.SetPreviewState(args[3]);
                        form.Snapshot(Path.GetFullPath(args[1]));
                    }
                    return 0;
                }
                Application.Run(new MainForm(args.Length > 0 ? args[0] : "", 0));
                return 0;
            }
            catch (Exception ex)
            {
                if (args.Length > 1 && (args[0] == "/self-test" || args[0] == "/render")) { File.WriteAllText(Path.GetFullPath(args[1]) + ".error.txt", ex.ToString()); return 1; }
                MessageBox.Show(ex.Message, "Dante PWND Pack", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
