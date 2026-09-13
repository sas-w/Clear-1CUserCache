using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("Очистка кэша 1С")]
[assembly: AssemblyDescription("Очистка кэша Local и Roaming текущего пользователя")]
[assembly: AssemblyVersion("1.0.5.0")]
[assembly: AssemblyFileVersion("1.0.5.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace OneCCacheCleaner
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                using (var mutex = new Mutex(false, @"Local\OneCCacheCleaner-" + identity.User.Value))
                {
                    bool acquired;
                    try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) { MessageBox.Show("Утилита уже открыта в этом сеансе.", "Очистка кэша 1С"); return; }
                    try { Application.Run(new MainWindow(identity.Name)); }
                    finally { mutex.ReleaseMutex(); }
                }
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Не удалось запустить утилиту", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }

    internal sealed class ScanResult
    {
        public string[] Roots;
        public List<string> Candidates;
        public List<string> Blockers;
    }

    internal sealed class MainWindow : Form
    {
        private readonly Label status = new Label();
        private readonly TextBox journal = new TextBox();
        private readonly ListView folders = new ListView();
        private readonly Button scan = new Button();
        private readonly Button clean = new Button();
        private readonly Button stop = new Button();
        private readonly ProgressBar progress = new ProgressBar();
        private CancellationTokenSource cancellation;
        private bool busy;
        private readonly TableLayoutPanel layout = new TableLayoutPanel();
        private readonly TableLayoutPanel details = new TableLayoutPanel();
        private readonly LinkLabel detailsToggle = new LinkLabel();
        private bool detailsVisible;

        public MainWindow(string userName)
        {
            Text = "Очистка кэша 1С";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(570, 330);
            MinimumSize = SizeFromClientSize(ClientSize);
            Font = new Font("Segoe UI", 10);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.FromArgb(246, 248, 251);
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(20);
            layout.ColumnCount = 1;
            layout.RowCount = 7;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            Controls.Add(layout);
            layout.Controls.Add(new Label { Text = "Очистка кэша 1С", Font = new Font("Segoe UI", 22, FontStyle.Bold), AutoSize = true }, 0, 0);
            layout.Controls.Add(new Label { Text = "Пользователь: " + userName, Dock = DockStyle.Fill, AutoEllipsis = true }, 0, 1);
            status.Dock = DockStyle.Fill;
            status.Font = new Font("Segoe UI", 11, FontStyle.Bold);
            status.Text = "Проверяем процессы и каталоги…";
            layout.Controls.Add(status, 0, 2);
            status.TextAlign = ContentAlignment.MiddleLeft;
            details.Dock = DockStyle.Fill;
            details.Margin = Padding.Empty;
            details.ColumnCount = 1;
            details.RowCount = 3;
            details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            details.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            details.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
            details.Visible = false;
            layout.Controls.Add(details, 0, 6);
            folders.Dock = DockStyle.Fill;
            folders.View = View.Details;
            folders.FullRowSelect = true;
            folders.GridLines = true;
            folders.Columns.Add("Каталоги кэша", 800);
            folders.Resize += delegate { folders.Columns[0].Width = Math.Max(100, folders.ClientSize.Width - 24); };
            details.Controls.Add(folders, 0, 0);
            details.Controls.Add(new Label { Text = "Журнал", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft }, 0, 1);
            journal.Dock = DockStyle.Fill;
            journal.Multiline = true;
            journal.ReadOnly = true;
            journal.ScrollBars = ScrollBars.Both;
            journal.WordWrap = false;
            journal.BackColor = Color.White;
            journal.Font = new Font("Consolas", 9);
            details.Controls.Add(journal, 0, 2);
            progress.Dock = DockStyle.Fill;
            progress.Margin = new Padding(3, 7, 3, 3);
            layout.Controls.Add(progress, 0, 3);
            // One shared grid keeps both rows aligned at every window width.
            var buttons = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2,
                Margin = Padding.Empty, Padding = Padding.Empty
            };
            for (int column = 0; column < 3; column++)
                buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
            buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            ConfigureButton(clean, "Очистить кэш");
            ConfigureButton(scan, "Проверить снова");
            ConfigureButton(stop, "Остановить");
            detailsToggle.Text = "Подробности";
            detailsToggle.Dock = DockStyle.Fill;
            detailsToggle.Margin = new Padding(3, 6, 3, 6);
            detailsToggle.TextAlign = ContentAlignment.MiddleCenter;
            var about = new Button();
            ConfigureButton(about, "О программе");
            stop.Enabled = false;
            clean.Enabled = false;
            buttons.Controls.Add(stop, 0, 0);
            buttons.Controls.Add(scan, 1, 0);
            buttons.Controls.Add(clean, 2, 0);
            buttons.Controls.Add(detailsToggle, 0, 1);
            buttons.Controls.Add(about, 2, 1);
            layout.Controls.Add(buttons, 0, 4);
            layout.SetRowSpan(buttons, 2);
            detailsToggle.LinkClicked += delegate { ToggleDetails(); };
            about.Click += delegate {
                string version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
                MessageBox.Show(this, "Очистка кэша 1С\r\nВерсия " + version +
                    "\r\n\r\nСероветников Андрей Сергеевич\r\nООО Запад-Восток Сервис\r\n\r\n2026", 
                    "О программе", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            scan.Click += async delegate { await Run(false); };
            clean.Click += async delegate { await Run(true); };
            stop.Click += delegate { if (cancellation != null) { cancellation.Cancel(); stop.Enabled = false; status.Text = "Останавливаем операцию…"; } };
            Shown += async delegate { await Run(false); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (!busy) return;
                e.Cancel = true;
                if (cancellation != null) cancellation.Cancel();
                status.Text = "Останавливаем операцию. Затем можно закрыть окно.";
            };
        }

        private void ToggleDetails()
        {
            detailsVisible = !detailsVisible;
            float scale = DeviceDpi / 96f;
            SuspendLayout();
            layout.SuspendLayout();
            details.Visible = detailsVisible;
            layout.RowStyles[6].Height = detailsVisible ? 320 * scale : 0;
            detailsToggle.Text = detailsVisible ? "Скрыть подробности" : "Подробности";
            var target = new Size((int)((detailsVisible ? 850 : 570) * scale),
                                  (int)((detailsVisible ? 650 : 330) * scale));
            MinimumSize = SizeFromClientSize(target);
            ClientSize = target;
            layout.ResumeLayout(true);
            ResumeLayout(true);
            // Keep the enlarged window within the current monitor's work area.
            var area = Screen.FromControl(this).WorkingArea;
            Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)),
                                 Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
        }

        private static void ConfigureButton(Button button, string text)
        {
            button.Text = text;
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(3, 6, 3, 6);
            button.AutoSize = false;
            button.TextAlign = ContentAlignment.MiddleCenter;
            button.BackColor = SystemColors.Control;
            button.ForeColor = SystemColors.ControlText;
            button.FlatStyle = FlatStyle.Standard;
            button.UseVisualStyleBackColor = true;
            button.Cursor = Cursors.Hand;
        }

        private void Log(string message) { journal.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + "\r\n"); }

        private async Task Run(bool delete)
        {
            if (busy) return;
            busy = true;
            scan.Enabled = clean.Enabled = false;
            stop.Enabled = true;
            cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            progress.Style = ProgressBarStyle.Marquee;
            bool ready = false;
            try
            {
                status.ForeColor = Color.FromArgb(30, 50, 75);
                status.Text = "Проверяем процессы 1С и каталоги кэша…";
                var snapshot = await Task.Run(() => {
                    var blockers = ProcessGuard.Blocking();
                    token.ThrowIfCancellationRequested();
                    var roots = CacheEngine.UserRoots();
                    return new ScanResult { Roots = roots, Blockers = blockers, Candidates = CacheEngine.FindCandidates(roots) };
                });
                token.ThrowIfCancellationRequested();
                folders.Items.Clear();
                foreach (string path in snapshot.Candidates) folders.Items.Add(path);
                Log("Проверка: найдено каталогов — " + snapshot.Candidates.Count + ".");
                if (snapshot.Blockers.Count > 0)
                {
                    status.ForeColor = Color.FromArgb(156, 83, 0);
                    status.Text = "Закройте все окна 1С";
                    Log(String.Join("\r\n", snapshot.Blockers));
                    return;
                }
                if (snapshot.Candidates.Count == 0) { status.Text = "Кэш не найден. Очищать нечего."; return; }
                ready = true;
                status.Text = "1С закрыта. Можно очистить кэш.";
                if (!delete) return;
                if (MessageBox.Show(this, "Удалить найденные каталоги кэша: " + snapshot.Candidates.Count + "?\r\n\r\nНе запускайте 1С до завершения. Файлы удаляются без Корзины.\r\nПервый запуск 1С после очистки может занять больше времени.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                ready = false;
                status.Text = "Очищаем кэш… Не запускайте 1С до завершения.";
                progress.Style = ProgressBarStyle.Continuous;
                progress.Maximum = snapshot.Candidates.Count;
                progress.Value = 0;
                var messages = new Progress<string>(Log);
                var steps = new Progress<int>(value => progress.Value = Math.Min(value, progress.Maximum));
                var result = await Task.Run(() => CacheEngine.Clean(snapshot.Roots, snapshot.Candidates, ProcessGuard.Blocking,
                    text => ((IProgress<string>)messages).Report(text), value => ((IProgress<int>)steps).Report(value), token));
                string summary = "Удалено каталогов: " + result.Deleted + " из " + snapshot.Candidates.Count + ".";
                if (result.Stopped || result.Failed > 0)
                {
                    status.ForeColor = Color.FromArgb(156, 83, 0);
                    status.Text = "Очистка выполнена не полностью.\r\nОткройте «Подробности» или повторите проверку.";
                }
                else
                {
                    status.ForeColor = Color.FromArgb(28, 117, 65);
                    status.Text = "Кэш очищен. Можно запускать 1С.";
                    folders.Items.Clear();
                }
                Log(summary);
            }
            catch (OperationCanceledException) { status.Text = "Операция остановлена. Для повтора нажмите «Проверить снова»."; Log("Операция отменена."); ready = false; }
            catch (Exception ex) { status.ForeColor = Color.Firebrick; status.Text = "Не удалось выполнить операцию. Откройте «Подробности»."; Log(ex.Message); ready = false; }
            finally
            {
                busy = false;
                scan.Enabled = true;
                clean.Enabled = ready;
                stop.Enabled = false;
                progress.Style = ProgressBarStyle.Continuous;
                cancellation.Dispose();
                cancellation = null;
            }
        }
    }
}
