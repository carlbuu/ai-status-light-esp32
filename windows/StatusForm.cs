using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace CodexStatusLight
{
    internal sealed class StatusForm : Form
    {
        private static readonly Color Background = Color.FromArgb(244, 247, 251);
        private static readonly Color Card = Color.White;
        private static readonly Color Primary = Color.FromArgb(44, 101, 242);
        private static readonly Color TextMain = Color.FromArgb(31, 41, 55);
        private static readonly Color TextMuted = Color.FromArgb(107, 114, 128);
        private static readonly Color Success = Color.FromArgb(22, 163, 74);
        private static readonly Color Warning = Color.FromArgb(217, 119, 6);
        private static readonly Color Danger = Color.FromArgb(220, 38, 38);

        private readonly BridgeApplicationContext bridge;
        private readonly ComboBox portCombo = new ComboBox();
        private readonly Label connectionValue = ValueLabel();
        private readonly Label firmwareValue = ValueLabel();
        private readonly Label codexValue = ValueLabel();
        private readonly Label displayValue = ValueLabel();
        private readonly Label statusDot = new Label();
        private readonly Label heroStatus = new Label();
        private readonly Button displayButton = ActionButton("关闭显示", Color.FromArgb(75, 85, 99));
        private readonly TrackBar brightnessTrack = new TrackBar();
        private readonly Label brightnessLabel = new Label();
        private readonly Label reviewLabel = new Label();
        private readonly Button acknowledgeReviewsButton = GhostButton("全部标记已检查");
        private readonly Button installButton = ActionButton("应用并配置", Primary);
        private readonly CheckBox startupCheck = new CheckBox();
        private readonly RadioButton codexRadio = new RadioButton();
        private readonly RadioButton cursorRadio = new RadioButton();
        private readonly Label platformStatusLabel = new Label();
        private readonly Timer refreshTimer = new Timer();
        private readonly Timer brightnessApplyTimer = new Timer();
        private readonly TableLayoutPanel root = new TableLayoutPanel();
        private readonly TableLayoutPanel statusGrid = new TableLayoutPanel();
        private readonly TableLayoutPanel testGrid = new TableLayoutPanel();
        private readonly TableLayoutPanel settingsLayout = new TableLayoutPanel();
        private readonly Dictionary<Control, Font> baseFonts = new Dictionary<Control, Font>();
        private readonly Dictionary<Control, Size> baseControlSizes = new Dictionary<Control, Size>();
        private Control[] statusTiles;
        private Control settingsContent;
        private Control settingsActions;
        private DateTime nextPortRefresh = DateTime.MinValue;
        private DateTime nextStartupRefresh = DateTime.MinValue;
        private bool updatingStartup;
        private bool compactLayout;
        private float currentScale;

        internal int StatusColumnCountForTest { get { return statusGrid.ColumnCount; } }
        internal int TestColumnCountForTest { get { return testGrid.ColumnCount; } }
        internal int TestRowCountForTest { get { return testGrid.RowCount; } }
        internal float UiScaleForTest { get { return currentScale; } }

        internal StatusForm(BridgeApplicationContext context)
        {
            bridge = context;
            Text = "AI 工作状态指示灯";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(620, 580);
            Size = new Size(820, 820);
            BackColor = Background;
            Font = new Font("Microsoft YaHei UI", 9F);
            Icon = SystemIcons.Application;
            AutoScaleMode = AutoScaleMode.Dpi;
            DoubleBuffered = true;

            root.Dock = DockStyle.Fill;
            root.AutoScroll = true;
            root.ColumnCount = 1;
            root.RowCount = 5;
            root.Padding = new Padding(22, 0, 22, 18);
            root.BackColor = Background;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            root.Controls.Add(BuildHeader(), 0, 0);
            root.Controls.Add(BuildConnectionCard(), 0, 1);
            root.Controls.Add(BuildStatusCard(), 0, 2);
            root.Controls.Add(BuildControlCard(), 0, 3);
            root.Controls.Add(BuildSettingsCard(), 0, 4);

            FormClosing += delegate(object sender, FormClosingEventArgs args)
            {
                if (args.CloseReason == CloseReason.UserClosing)
                {
                    args.Cancel = true;
                    Hide();
                }
            };

            refreshTimer.Interval = 700;
            refreshTimer.Tick += delegate { RefreshUi(); };
            refreshTimer.Start();
            brightnessApplyTimer.Interval = 180;
            brightnessApplyTimer.Tick += delegate
            {
                brightnessApplyTimer.Stop();
                bridge.SetBrightnessFromUi(brightnessTrack.Value);
            };
            RefreshPorts();
            RefreshUi();

            CaptureResponsiveBaselines(this);
            Resize += delegate { ApplyResponsiveLayout(); };
            ApplyResponsiveLayout();
        }

        private Control BuildHeader()
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Background,
                ColumnCount = 2,
                RowCount = 1
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var heading = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(2, 20, 0, 0)
            };
            var title = new Label
            {
                Text = "AI Status Light",
                Font = new Font("Microsoft YaHei UI", 19F, FontStyle.Bold),
                ForeColor = TextMain,
                AutoSize = true,
                Margin = new Padding(0)
            };
            var subtitle = new Label
            {
                Text = "在 Codex 与 Cursor 之间选择一个平台驱动实体指示灯",
                Font = new Font("Microsoft YaHei UI", 9.5F),
                ForeColor = TextMuted,
                AutoSize = true,
                Margin = new Padding(2, 7, 0, 0)
            };
            heading.Controls.Add(title);
            heading.Controls.Add(subtitle);

            var liveStatus = new FlowLayoutPanel
            {
                AutoSize = true,
                Anchor = AnchorStyles.Right,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(12, 34, 2, 0)
            };
            statusDot.Size = new Size(12, 12);
            statusDot.BackColor = TextMuted;
            statusDot.Margin = new Padding(0, 5, 8, 0);
            heroStatus.AutoSize = true;
            heroStatus.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            heroStatus.ForeColor = TextMain;
            heroStatus.Text = "正在启动";
            heroStatus.Margin = new Padding(0);
            liveStatus.Controls.Add(statusDot);
            liveStatus.Controls.Add(heroStatus);

            panel.Controls.Add(heading, 0, 0);
            panel.Controls.Add(liveStatus, 1, 0);
            return panel;
        }

        private Control BuildConnectionCard()
        {
            var card = NewCard("设备连接");
            var flow = NewFlow();
            flow.Controls.Add(Caption("串口"));
            portCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            portCombo.Width = 170;
            portCombo.Height = 32;
            flow.Controls.Add(portCombo);

            var refresh = GhostButton("刷新端口");
            refresh.Click += delegate { RefreshPorts(); };
            flow.Controls.Add(refresh);

            var connect = ActionButton("连接设备", Primary);
            connect.Click += delegate
            {
                string value = portCombo.SelectedItem as string;
                bridge.ConnectFromUi(value == "自动扫描" ? "AUTO" : value);
            };
            flow.Controls.Add(connect);

            var disconnect = GhostButton("断开连接");
            disconnect.Click += delegate { bridge.DisconnectFromUi(); };
            flow.Controls.Add(disconnect);
            card.Controls.Add(flow);
            return card;
        }

        private Control BuildStatusCard()
        {
            var card = NewCard("实时状态");
            statusGrid.Dock = DockStyle.Fill;
            statusGrid.ColumnCount = 4;
            statusGrid.RowCount = 1;
            statusGrid.AutoSize = true;
            for (int i = 0; i < 4; ++i)
                statusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));

            statusTiles = new[]
            {
                StatusTile("设备", connectionValue),
                StatusTile("固件", firmwareValue),
                StatusTile("AI 状态", codexValue),
                StatusTile("显示", displayValue)
            };
            for (int i = 0; i < statusTiles.Length; ++i)
                statusGrid.Controls.Add(statusTiles[i], i, 0);
            card.Controls.Add(statusGrid);
            return card;
        }

        private Control BuildControlCard()
        {
            var card = NewCard("灯光测试");
            var content = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 1,
                RowCount = 3
            };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            testGrid.Dock = DockStyle.Fill;
            testGrid.AutoSize = true;
            testGrid.ColumnCount = 4;
            testGrid.RowCount = 2;
            for (int i = 0; i < 4; ++i)
                testGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            for (int i = 0; i < 2; ++i)
                testGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            AddTest(testGrid, "1 个任务", "THINKING", Warning);
            AddTest(testGrid, "2 个任务", "WORKING2", Warning);
            AddTest(testGrid, "3+ 个任务", "WORKING3", Warning);
            AddTest(testGrid, "请求权限", "PERMISSION", Success);
            AddTest(testGrid, "已完成", "COMPLETE", Success);
            AddTest(testGrid, "报错", "ERROR", Danger);
            AddTest(testGrid, "全部熄灭", "OFF", Color.FromArgb(75, 85, 99));
            displayButton.Click += delegate
            {
                StatusSnapshot snapshot = bridge.Snapshot();
                bridge.SetDisplayFromUi(!snapshot.DisplayEnabled);
            };
            testGrid.Controls.Add(displayButton);

            StatusSnapshot initialSnapshot = bridge.Snapshot();
            var brightnessRow = NewFlow();
            brightnessRow.Margin = new Padding(0, 9, 0, 0);
            brightnessLabel.Text = "亮度 " + initialSnapshot.BrightnessPercent + "%";
            brightnessLabel.AutoSize = true;
            brightnessLabel.ForeColor = TextMain;
            brightnessLabel.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            brightnessLabel.Padding = new Padding(0, 9, 8, 0);
            brightnessTrack.Minimum = 5;
            brightnessTrack.Maximum = 100;
            brightnessTrack.TickFrequency = 5;
            brightnessTrack.SmallChange = 5;
            brightnessTrack.LargeChange = 10;
            brightnessTrack.Width = 300;
            brightnessTrack.Value = Math.Max(
                brightnessTrack.Minimum,
                Math.Min(brightnessTrack.Maximum, initialSnapshot.BrightnessPercent));
            brightnessTrack.Scroll += delegate
            {
                brightnessLabel.Text = "亮度 " + brightnessTrack.Value + "%";
                brightnessApplyTimer.Stop();
                brightnessApplyTimer.Start();
            };
            brightnessRow.Controls.Add(brightnessLabel);
            brightnessRow.Controls.Add(brightnessTrack);

            var reviewRow = NewFlow();
            reviewRow.Margin = new Padding(0, 9, 0, 0);
            reviewLabel.AutoSize = true;
            reviewLabel.ForeColor = TextMuted;
            reviewLabel.Padding = new Padding(0, 9, 8, 0);
            acknowledgeReviewsButton.Width = 130;
            acknowledgeReviewsButton.Click += delegate
            {
                bridge.MarkAllReviewsCheckedFromUi();
                RefreshUi();
            };
            reviewRow.Controls.Add(reviewLabel);
            reviewRow.Controls.Add(acknowledgeReviewsButton);

            content.Controls.Add(testGrid, 0, 0);
            content.Controls.Add(brightnessRow, 0, 1);
            content.Controls.Add(reviewRow, 0, 2);
            card.Controls.Add(content);
            return card;
        }

        private Control BuildSettingsCard()
        {
            var card = NewCard("应用设置");
            card.Dock = DockStyle.Top;
            settingsLayout.Dock = DockStyle.Fill;
            settingsLayout.ColumnCount = 2;
            settingsLayout.RowCount = 1;
            settingsLayout.AutoSize = true;
            settingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            settingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var settings = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown };
            settings.WrapContents = false;
            settings.Controls.Add(new Label
            {
                Text = "选择 AI 平台",
                AutoSize = true,
                ForeColor = TextMain,
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
                Margin = new Padding(3, 0, 3, 3)
            });

            StatusSnapshot initialSnapshot = bridge.Snapshot();
            var platformChoices = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0)
            };
            codexRadio.Text = "Codex";
            codexRadio.AutoSize = true;
            codexRadio.ForeColor = TextMain;
            codexRadio.Checked = !string.Equals(
                initialSnapshot.Platform,
                IntegrationManager.CursorPlatform,
                StringComparison.OrdinalIgnoreCase);
            cursorRadio.Text = "Cursor";
            cursorRadio.AutoSize = true;
            cursorRadio.ForeColor = TextMain;
            cursorRadio.Checked = !codexRadio.Checked;
            platformChoices.Controls.Add(codexRadio);
            platformChoices.Controls.Add(cursorRadio);
            settings.Controls.Add(platformChoices);

            platformStatusLabel.Text = initialSnapshot.IntegrationConfigured
                ? initialSnapshot.Platform + " 已配置"
                : initialSnapshot.Platform + " 尚未配置";
            platformStatusLabel.ForeColor = initialSnapshot.IntegrationConfigured ? Success : Warning;
            platformStatusLabel.AutoSize = true;
            platformStatusLabel.Margin = new Padding(3, 2, 3, 9);
            settings.Controls.Add(platformStatusLabel);

            startupCheck.Text = "开机自动启动";
            startupCheck.AutoSize = true;
            startupCheck.ForeColor = TextMain;
            startupCheck.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            // The portable/one-click executable can manage the installed copy too.
            startupCheck.Enabled = true;
            startupCheck.Checked = Program.IsStartupEnabled();
            startupCheck.CheckedChanged += delegate
            {
                if (updatingStartup) return;
                try
                {
                    Program.SetStartupEnabled(startupCheck.Checked);
                    updatingStartup = true;
                    startupCheck.Checked = Program.IsStartupEnabled();
                    updatingStartup = false;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "设置失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    updatingStartup = true;
                    startupCheck.Checked = !startupCheck.Checked;
                    updatingStartup = false;
                }
            };
            settings.Controls.Add(startupCheck);
            settings.Controls.Add(new Label
            {
                Text = Program.IsInstalled() ? "关闭窗口后程序仍在托盘运行" : "安装后可设置开机自启动",
                ForeColor = TextMuted,
                AutoSize = true,
                Margin = new Padding(3, 5, 3, 0)
            });
            settingsContent = settings;
            settingsLayout.Controls.Add(settingsContent, 0, 0);

            var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            installButton.Text = "应用并配置";
            installButton.Enabled = true;
            installButton.Click += delegate
            {
                try
                {
                    string platform = cursorRadio.Checked
                        ? IntegrationManager.CursorPlatform
                        : IntegrationManager.CodexPlatform;
                    bool restarted = bridge.InstallFromUi(startupCheck.Checked, platform);
                    if (restarted) return;
                    RefreshUi();
                    MessageBox.Show(
                        platform + " 已配置完成。",
                        "平台切换成功",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "配置失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            actions.Controls.Add(installButton);
            var exit = GhostButton("退出程序");
            exit.Click += delegate { bridge.ExitFromUi(); };
            actions.Controls.Add(exit);
            settingsActions = actions;
            settingsLayout.Controls.Add(settingsActions, 1, 0);
            card.Controls.Add(settingsLayout);
            return card;
        }

        private void RefreshUi()
        {
            StatusSnapshot s = bridge.Snapshot();
            connectionValue.Text = s.Connected ? s.ConnectedPort : (s.ConnectionEnabled ? "扫描中" : "已断开");
            connectionValue.ForeColor = s.Connected ? Success : Danger;
            firmwareValue.Text = s.FirmwareVersion;
            codexValue.Text = StateText(s.CodexState, s.ActiveTaskCount, s.PendingReviewCount);
            displayValue.Text = !s.DisplayEnabled
                ? "手动关闭"
                : (s.CodexState == "Idle" ? "自动熄灭" : "开启");
            displayButton.Text = s.DisplayEnabled ? "关闭显示" : "恢复显示";
            reviewLabel.Text = s.PendingReviewCount > 0
                ? "待检查项目 " + s.PendingReviewCount + " 个；点击带黄点项目后自动清除"
                : "没有待检查项目；无运行任务时自动熄灯";
            acknowledgeReviewsButton.Enabled = s.PendingReviewCount > 0;
            int brightness = Math.Max(
                brightnessTrack.Minimum,
                Math.Min(brightnessTrack.Maximum, s.BrightnessPercent));
            if (!brightnessTrack.Capture && !brightnessApplyTimer.Enabled &&
                brightnessTrack.Value != brightness)
                brightnessTrack.Value = brightness;
            brightnessLabel.Text = "亮度 " + brightnessTrack.Value + "%" +
                (s.Connected && !s.BrightnessSupported ? "（请升级固件）" : "");
            heroStatus.Text = s.Platform + " · " +
                (s.Connected ? "设备在线" : (s.ConnectionEnabled ? "正在连接" : "连接已断开"));
            statusDot.BackColor = s.Connected ? Success : (s.ConnectionEnabled ? Warning : Danger);
            platformStatusLabel.Text = s.IntegrationConfigured
                ? s.Platform + " 已配置"
                : s.Platform + " 尚未配置";
            platformStatusLabel.ForeColor = s.IntegrationConfigured ? Success : Warning;

            if (DateTime.UtcNow >= nextPortRefresh)
            {
                nextPortRefresh = DateTime.UtcNow.AddSeconds(3);
                RefreshPorts(true);
            }

            if (DateTime.UtcNow >= nextStartupRefresh)
            {
                nextStartupRefresh = DateTime.UtcNow.AddSeconds(3);
                updatingStartup = true;
                startupCheck.Checked = Program.IsStartupEnabled();
                updatingStartup = false;
            }
        }

        internal void PrepareForExit()
        {
            refreshTimer.Stop();
            brightnessApplyTimer.Stop();
            Hide();
        }

        private void CaptureResponsiveBaselines(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                if (control is Label || control is Button ||
                    control is ComboBox || control is CheckBox || control is RadioButton)
                    baseFonts[control] = control.Font;
                if (control is Button || control is ComboBox || control is TrackBar ||
                    object.ReferenceEquals(control, statusDot))
                    baseControlSizes[control] = control.Size;
                if (control.HasChildren)
                    CaptureResponsiveBaselines(control);
            }
        }

        private void ApplyResponsiveLayout()
        {
            bool useCompactLayout = ClientSize.Width < 720;
            if (useCompactLayout != compactLayout)
            {
                compactLayout = useCompactLayout;
                ConfigureStatusGrid(useCompactLayout);
                ConfigureTestGrid(useCompactLayout);
                ConfigureSettingsLayout(useCompactLayout);
            }

            float scale = Math.Max(0.88F, Math.Min(1.25F, ClientSize.Width / 804F));
            scale = (float)Math.Round(scale * 20F) / 20F;
            if (Math.Abs(scale - currentScale) < 0.01F) return;
            currentScale = scale;

            SuspendLayout();
            root.Padding = new Padding(ScaleValue(22, scale), 0, ScaleValue(22, scale), ScaleValue(18, scale));
            root.RowStyles[0].Height = ScaleValue(108, scale);
            foreach (KeyValuePair<Control, Font> pair in baseFonts)
            {
                if (pair.Key.IsDisposed) continue;
                Font previousFont = pair.Key.Font;
                pair.Key.Font = new Font(
                    pair.Value.FontFamily,
                    Math.Max(7F, pair.Value.SizeInPoints * scale),
                    pair.Value.Style,
                    GraphicsUnit.Point);
                if (!object.ReferenceEquals(previousFont, pair.Value))
                    previousFont.Dispose();
            }
            foreach (KeyValuePair<Control, Size> pair in baseControlSizes)
            {
                if (pair.Key.IsDisposed) continue;
                pair.Key.Size = new Size(
                    ScaleValue(pair.Value.Width, scale),
                    ScaleValue(pair.Value.Height, scale));
            }
            ResumeLayout(true);
        }

        private void ConfigureStatusGrid(bool compact)
        {
            statusGrid.SuspendLayout();
            statusGrid.ColumnStyles.Clear();
            statusGrid.RowStyles.Clear();
            statusGrid.ColumnCount = compact ? 2 : 4;
            statusGrid.RowCount = compact ? 2 : 1;
            for (int i = 0; i < statusGrid.ColumnCount; ++i)
                statusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / statusGrid.ColumnCount));
            for (int i = 0; i < statusGrid.RowCount; ++i)
                statusGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            for (int i = 0; i < statusTiles.Length; ++i)
                statusGrid.SetCellPosition(statusTiles[i],
                    new TableLayoutPanelCellPosition(i % statusGrid.ColumnCount, i / statusGrid.ColumnCount));
            statusGrid.ResumeLayout(true);
        }

        private void ConfigureSettingsLayout(bool compact)
        {
            settingsLayout.SuspendLayout();
            settingsLayout.ColumnStyles.Clear();
            settingsLayout.RowStyles.Clear();
            settingsLayout.ColumnCount = compact ? 1 : 2;
            settingsLayout.RowCount = compact ? 2 : 1;
            if (compact)
            {
                settingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                settingsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                settingsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                settingsLayout.SetCellPosition(settingsContent, new TableLayoutPanelCellPosition(0, 0));
                settingsLayout.SetCellPosition(settingsActions, new TableLayoutPanelCellPosition(0, 1));
            }
            else
            {
                settingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                settingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                settingsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                settingsLayout.SetCellPosition(settingsContent, new TableLayoutPanelCellPosition(0, 0));
                settingsLayout.SetCellPosition(settingsActions, new TableLayoutPanelCellPosition(1, 0));
            }
            settingsLayout.ResumeLayout(true);
        }

        private void ConfigureTestGrid(bool compact)
        {
            testGrid.SuspendLayout();
            testGrid.ColumnStyles.Clear();
            testGrid.RowStyles.Clear();
            testGrid.ColumnCount = compact ? 2 : 4;
            testGrid.RowCount = compact ? 4 : 2;
            for (int i = 0; i < testGrid.ColumnCount; ++i)
                testGrid.ColumnStyles.Add(
                    new ColumnStyle(SizeType.Percent, 100F / testGrid.ColumnCount));
            for (int i = 0; i < testGrid.RowCount; ++i)
                testGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            for (int i = 0; i < testGrid.Controls.Count; ++i)
                testGrid.SetCellPosition(
                    testGrid.Controls[i],
                    new TableLayoutPanelCellPosition(
                        i % testGrid.ColumnCount,
                        i / testGrid.ColumnCount));
            testGrid.ResumeLayout(true);
        }

        private static int ScaleValue(int value, float scale)
        {
            return Math.Max(1, (int)Math.Round(value * scale));
        }

        private void AddTest(Control parent, string text, string command, Color color)
        {
            var button = ActionButton(text, color);
            button.Click += delegate
            {
                try { bridge.SendTestCommand(command); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "发送失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            parent.Controls.Add(button);
        }

        private void RefreshPorts() { RefreshPorts(true); }

        private void RefreshPorts(bool preserve)
        {
            string selected = preserve ? portCombo.SelectedItem as string : null;
            if (string.IsNullOrEmpty(selected)) selected = "自动扫描";
            portCombo.Items.Clear();
            portCombo.Items.Add("自动扫描");
            foreach (string port in bridge.AvailablePorts()) portCombo.Items.Add(port);
            int index = portCombo.Items.IndexOf(selected);
            portCombo.SelectedIndex = index >= 0 ? index : 0;
        }

        private static string StateText(string state, int activeTaskCount, int pendingReviewCount)
        {
            string countText = Math.Max(1, activeTaskCount) + " 个任务";
            if (state == "Working") return "运行中 · " + countText;
            if (state == "Waiting") return "请求权限 · " + countText;
            if (state == "Error") return "报错";
            if (state == "Review") return "待检查 · " + Math.Max(1, pendingReviewCount) + " 个项目";
            return "空闲";
        }

        private static Panel NewCard(string title)
        {
            var panel = new Panel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                BackColor = Card,
                Padding = new Padding(18, 42, 18, 16),
                Margin = new Padding(0, 0, 0, 14)
            };
            panel.Controls.Add(new Label
            {
                Text = title,
                Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold),
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(18, 13)
            });
            return panel;
        }

        private static FlowLayoutPanel NewFlow()
        {
            return new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true
            };
        }

        private static Control StatusTile(string caption, Label value)
        {
            var panel = new Panel { Height = 62, Dock = DockStyle.Fill, Margin = new Padding(4) };
            panel.Controls.Add(new Label { Text = caption, ForeColor = TextMuted, AutoSize = true, Location = new Point(4, 4) });
            value.Location = new Point(4, 29);
            panel.Controls.Add(value);
            return panel;
        }

        private static Label ValueLabel()
        {
            return new Label
            {
                Text = "-",
                ForeColor = TextMain,
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
                AutoSize = true
            };
        }

        private static Label Caption(string text)
        {
            return new Label { Text = text, ForeColor = TextMuted, AutoSize = true, Padding = new Padding(0, 9, 3, 0) };
        }

        private static Button ActionButton(string text, Color color)
        {
            return new Button
            {
                Text = text,
                Width = 105,
                Height = 34,
                FlatStyle = FlatStyle.Flat,
                BackColor = color,
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Margin = new Padding(5),
                FlatAppearance = { BorderSize = 0 }
            };
        }

        private static Button GhostButton(string text)
        {
            var button = ActionButton(text, Card);
            button.ForeColor = TextMain;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(209, 213, 219);
            return button;
        }
    }
}
