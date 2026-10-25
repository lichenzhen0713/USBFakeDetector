using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UsbCheck
{
    internal sealed class MainForm : Form
    {
        private ListView _lv;
        private Label _lblSelection;
        private Button _btnRefresh;
        private Button _btnStart;
        private Button _btnStop;
        private Button _btnSave;
        private Button _btnOpenReport;
        private RadioButton _rbQuick;
        private RadioButton _rbFull;
        private CheckBox _cbAll;
        private CheckBox _cbSafe;
        private CheckBox _cbAck;
        private CheckBox _cbFixed;
        private ProgressBar _bar;
        private Label _lblStatus;
        private Label _lblModeWarning;
        private Label _lblVerdict;
        private TextBox _txtSummary;
        private TextBox _txtLog;

        private List<DiskInfo> _allDisks = new List<DiskInfo>();
        private CancellationTokenSource _cts;
        private DetectionResult _lastResult;
        private DiskInfo _lastDisk;
        private string _lastReport;
        private bool _running;
        private readonly bool _demo;
        private float _uiScale = 1f;
        private static readonly int[] ColumnBaseWidths = new int[] { 46, 64, 100, 78, 104, 52, 300, 160 };

        public MainForm() : this(false)
        {
        }

        public MainForm(bool demo)
        {
            _demo = demo;
            BuildUi();
            RefreshDisks();
        }

        // ---------------------------------- UI ----------------------------------

        private void BuildUi()
        {
            Text = AppInfo.Name + "  v" + AppInfo.Version;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            MinimumSize = new Size(760, 640);
            Size = new Size(820, 700);
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = Color.FromArgb(246, 247, 249);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(12, 10, 12, 10);
            root.ColumnCount = 1;
            root.RowCount = 4;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 200F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 142F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 136F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            Controls.Add(root);

            root.Controls.Add(BuildDeviceGroup(), 0, 0);
            root.Controls.Add(BuildModeGroup(), 0, 1);
            root.Controls.Add(BuildActionGroup(), 0, 2);
            root.Controls.Add(BuildContent(), 0, 3);

            FormClosing += OnFormClosing;
            AcceptButton = _btnStart;
        }

        private GroupBox BuildDeviceGroup()
        {
            GroupBox group = new GroupBox();
            group.Text = "① 选择目标U盘";
            group.Dock = DockStyle.Fill;
            group.Padding = new Padding(10, 6, 10, 4);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 3;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));
            group.Controls.Add(layout);

            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Fill;
            bar.WrapContents = false;
            bar.Margin = new Padding(0);
            bar.Padding = new Padding(0, 2, 0, 0);

            _btnRefresh = new Button();
            _btnRefresh.Text = "刷新";
            _btnRefresh.Size = new Size(76, 26);
            _btnRefresh.Margin = new Padding(0, 0, 12, 0);
            _btnRefresh.Click += delegate { RefreshDisks(); };
            bar.Controls.Add(_btnRefresh);

            _cbAll = new CheckBox();
            _cbAll.Text = "显示所有磁盘（危险）";
            _cbAll.AutoSize = true;
            _cbAll.Margin = new Padding(0, 6, 0, 0);
            _cbAll.CheckedChanged += delegate { OnShowAllChanged(); };
            bar.Controls.Add(_cbAll);

            Label hint = new Label();
            hint.Text = "默认仅 USB 磁盘";
            hint.AutoSize = true;
            hint.ForeColor = Color.Gray;
            hint.Margin = new Padding(18, 7, 0, 0);
            bar.Controls.Add(hint);

            layout.Controls.Add(bar, 0, 0);

            _lv = new ListView();
            _lv.Dock = DockStyle.Fill;
            _lv.View = View.Details;
            _lv.FullRowSelect = true;
            _lv.HideSelection = false;
            _lv.MultiSelect = false;
            _lv.GridLines = true;
            _lv.BorderStyle = BorderStyle.FixedSingle;
            _lv.Columns.Add("磁盘", 46);
            _lv.Columns.Add("盘符", 64);
            _lv.Columns.Add("卷标", 100);
            _lv.Columns.Add("文件系统", 78);
            _lv.Columns.Add("声称容量", 104);
            _lv.Columns.Add("接口", 52);
            _lv.Columns.Add("型号", 300);
            _lv.Columns.Add("序列号", 160);
            _lv.SelectedIndexChanged += delegate { OnSelectionChanged(); };
            _lv.Resize += delegate { LayoutListColumns(); };
            _lv.DoubleClick += delegate
            {
                if (_btnStart.Enabled) StartTest();
            };
            layout.Controls.Add(_lv, 0, 1);

            _lblSelection = new Label();
            _lblSelection.Dock = DockStyle.Fill;
            _lblSelection.TextAlign = ContentAlignment.MiddleLeft;
            _lblSelection.ForeColor = Color.DimGray;
            _lblSelection.Text = "正在读取磁盘列表 ...";
            layout.Controls.Add(_lblSelection, 0, 2);

            return group;
        }

        private GroupBox BuildModeGroup()
        {
            GroupBox group = new GroupBox();
            group.Text = "② 检测方式";
            group.Dock = DockStyle.Fill;
            group.Padding = new Padding(10, 6, 10, 4);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 2;
            layout.RowCount = 4;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            group.Controls.Add(layout);

            _rbQuick = new RadioButton();
            _rbQuick.Text = "快速扫描（采样 + 别名探测）";
            _rbQuick.AutoSize = true;
            _rbQuick.Checked = true;
            _rbQuick.Dock = DockStyle.Fill;
            layout.Controls.Add(_rbQuick, 0, 0);

            Label quickDesc = new Label();
            quickDesc.Text = "约 1–3 分钟。能发现多数常见的扩容盘，但属于抽样检查，存在漏检可能。";
            quickDesc.Dock = DockStyle.Fill;
            quickDesc.TextAlign = ContentAlignment.MiddleLeft;
            quickDesc.ForeColor = Color.Gray;
            layout.Controls.Add(quickDesc, 1, 0);

            _rbFull = new RadioButton();
            _rbFull.Text = "完整校验（写满并逐块校验）";
            _rbFull.AutoSize = true;
            _rbFull.Dock = DockStyle.Fill;
            layout.Controls.Add(_rbFull, 0, 1);

            Label fullDesc = new Label();
            fullDesc.Text = "最可靠。耗时取决于容量和U盘速度；若发现扩容会提前终止。";
            fullDesc.Dock = DockStyle.Fill;
            fullDesc.TextAlign = ContentAlignment.MiddleLeft;
            fullDesc.ForeColor = Color.Gray;
            layout.Controls.Add(fullDesc, 1, 1);

            _cbSafe = new CheckBox();
            _cbSafe.Text = "安全模式（保留文件，快速抽样）";
            _cbSafe.AutoSize = true;
            _cbSafe.Dock = DockStyle.Fill;
            _cbSafe.CheckedChanged += delegate { OnSafeChanged(); };
            layout.Controls.Add(_cbSafe, 0, 2);

            Label safeDesc = new Label();
            safeDesc.Text = "只读文件系统空闲位图，通常 1–3 秒；不创建/删除文件，不覆盖原有文件。";
            safeDesc.Dock = DockStyle.Fill;
            safeDesc.TextAlign = ContentAlignment.MiddleLeft;
            safeDesc.ForeColor = Color.Gray;
            layout.Controls.Add(safeDesc, 1, 2);

            _lblModeWarning = new Label();
            _lblModeWarning.Text = "⚠ 检测会覆盖整块磁盘（包括分区表和数据），无法恢复；检测后需要重新格式化U盘。";
            _lblModeWarning.Dock = DockStyle.Fill;
            _lblModeWarning.TextAlign = ContentAlignment.MiddleLeft;
            _lblModeWarning.ForeColor = Color.Firebrick;
            _lblModeWarning.Font = new Font(Font, FontStyle.Bold);
            layout.Controls.Add(_lblModeWarning, 0, 3);
            layout.SetColumnSpan(_lblModeWarning, 2);

            return group;
        }

        private GroupBox BuildActionGroup()
        {
            GroupBox group = new GroupBox();
            group.Text = "③ 开始检测";
            group.Dock = DockStyle.Fill;
            group.Padding = new Padding(10, 6, 10, 4);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 4;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20F));
            group.Controls.Add(layout);

            FlowLayoutPanel checks = new FlowLayoutPanel();
            checks.Dock = DockStyle.Fill;
            checks.WrapContents = false;
            checks.Margin = new Padding(0);

            _cbAck = new CheckBox();
            _cbAck.Text = "我已确认：U盘中的数据可以清除，并已自行备份";
            _cbAck.AutoSize = true;
            _cbAck.ForeColor = Color.FromArgb(150, 30, 30);
            _cbAck.Margin = new Padding(0, 4, 24, 0);
            checks.Controls.Add(_cbAck);

            _cbFixed = new CheckBox();
            _cbFixed.Text = "允许检测非USB磁盘（内置硬盘）";
            _cbFixed.AutoSize = true;
            _cbFixed.Visible = false;
            _cbFixed.Margin = new Padding(0, 4, 0, 0);
            checks.Controls.Add(_cbFixed);

            layout.Controls.Add(checks, 0, 0);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.WrapContents = false;
            buttons.Margin = new Padding(0);

            _btnStart = new Button();
            _btnStart.Text = "开始检测";
            _btnStart.Size = new Size(118, 32);
            _btnStart.Font = new Font(Font, FontStyle.Bold);
            _btnStart.Margin = new Padding(0, 3, 10, 0);
            _btnStart.Click += delegate { StartTest(); };
            buttons.Controls.Add(_btnStart);

            _btnStop = new Button();
            _btnStop.Text = "停止";
            _btnStop.Size = new Size(76, 32);
            _btnStop.Enabled = false;
            _btnStop.Margin = new Padding(0, 3, 10, 0);
            _btnStop.Click += delegate
            {
                if (_cts != null) _cts.Cancel();
                Log("正在停止检测 ...");
            };
            buttons.Controls.Add(_btnStop);

            _btnSave = new Button();
            _btnSave.Text = "保存报告";
            _btnSave.Size = new Size(94, 32);
            _btnSave.Enabled = false;
            _btnSave.Margin = new Padding(0, 3, 10, 0);
            _btnSave.Click += delegate { SaveReport(); };
            buttons.Controls.Add(_btnSave);

            _btnOpenReport = new Button();
            _btnOpenReport.Text = "打开报告";
            _btnOpenReport.Size = new Size(94, 32);
            _btnOpenReport.Enabled = false;
            _btnOpenReport.Margin = new Padding(0, 3, 0, 0);
            _btnOpenReport.Click += delegate { OpenReport(); };
            buttons.Controls.Add(_btnOpenReport);

            layout.Controls.Add(buttons, 0, 1);

            _bar = new ProgressBar();
            _bar.Dock = DockStyle.Fill;
            _bar.Minimum = 0;
            _bar.Maximum = 100;
            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Margin = new Padding(0, 2, 0, 0);
            layout.Controls.Add(_bar, 0, 2);

            _lblStatus = new Label();
            _lblStatus.Dock = DockStyle.Fill;
            _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            _lblStatus.ForeColor = Color.DimGray;
            _lblStatus.Text = "就绪。请选择U盘并勾选确认后开始。";
            layout.Controls.Add(_lblStatus, 0, 3);

            return group;
        }

        private TableLayoutPanel BuildContent()
        {
            TableLayoutPanel content = new TableLayoutPanel();
            content.Dock = DockStyle.Fill;
            content.ColumnCount = 2;
            content.RowCount = 1;
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F));
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            GroupBox logGroup = new GroupBox();
            logGroup.Text = "检测日志";
            logGroup.Dock = DockStyle.Fill;
            logGroup.Padding = new Padding(8, 6, 8, 8);
            logGroup.Margin = new Padding(0, 0, 6, 0);

            _txtLog = new TextBox();
            _txtLog.Dock = DockStyle.Fill;
            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.ScrollBars = ScrollBars.Both;
            _txtLog.WordWrap = false;
            _txtLog.BackColor = Color.White;
            _txtLog.Font = new Font("Microsoft YaHei UI", 8.5F);
            logGroup.Controls.Add(_txtLog);
            content.Controls.Add(logGroup, 0, 0);

            GroupBox resultGroup = new GroupBox();
            resultGroup.Text = "检测结果";
            resultGroup.Dock = DockStyle.Fill;
            resultGroup.Padding = new Padding(10, 8, 10, 8);
            resultGroup.Margin = new Padding(6, 0, 0, 0);

            TableLayoutPanel resultLayout = new TableLayoutPanel();
            resultLayout.Dock = DockStyle.Fill;
            resultLayout.ColumnCount = 1;
            resultLayout.RowCount = 2;
            resultLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));
            resultLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            _lblVerdict = new Label();
            _lblVerdict.Dock = DockStyle.Fill;
            _lblVerdict.TextAlign = ContentAlignment.MiddleLeft;
            _lblVerdict.Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold);
            _lblVerdict.ForeColor = Color.Gray;
            _lblVerdict.Text = "尚未检测";
            resultLayout.Controls.Add(_lblVerdict, 0, 0);

            _txtSummary = new TextBox();
            _txtSummary.Dock = DockStyle.Fill;
            _txtSummary.Multiline = true;
            _txtSummary.ReadOnly = true;
            _txtSummary.WordWrap = true;
            _txtSummary.ScrollBars = ScrollBars.Vertical;
            _txtSummary.BorderStyle = BorderStyle.None;
            _txtSummary.BackColor = SystemColors.Control;
            _txtSummary.ForeColor = Color.FromArgb(50, 50, 50);
            _txtSummary.Text = "选择磁盘并开始检测后，这里会显示结论、推算容量和证据。";
            resultLayout.Controls.Add(_txtSummary, 0, 1);

            resultGroup.Controls.Add(resultLayout);
            content.Controls.Add(resultGroup, 1, 0);

            return content;
        }

        // ---------------------------------- 事件 ----------------------------------

        private void RefreshDisks()
        {
            if (_running) return;
            Cursor = Cursors.WaitCursor;
            try
            {
                _allDisks = DiskInventory.Enumerate();
            }
            catch (Exception ex)
            {
                _allDisks = new List<DiskInfo>();
                Log("读取磁盘列表失败：" + ex.Message);
                MessageBox.Show(this, "读取磁盘列表失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }

            ApplyFilter();
            Log("设备列表已刷新：共 " + _allDisks.Count.ToString() + " 个磁盘。");
        }

        private void ApplyFilter()
        {
            _lv.BeginUpdate();
            _lv.Items.Clear();
            int shown = 0;
            foreach (DiskInfo disk in _allDisks)
            {
                if (disk.Size <= 0) continue; // 无介质（空读卡器/控制器掉线）不显示
                if (!_cbAll.Checked && !disk.IsUsb) continue;
                ListViewItem item = new ListViewItem(disk.Index.ToString());
                item.SubItems.Add(disk.Letters);
                item.SubItems.Add(disk.Labels);
                item.SubItems.Add(disk.FileSystems);
                item.SubItems.Add(Fmt.Size(disk.Size));
                item.SubItems.Add(disk.InterfaceType);
                item.SubItems.Add(disk.Model);
                item.SubItems.Add(disk.SerialNumber);
                item.Tag = disk;
                if (!disk.IsUsb) item.ForeColor = Color.DimGray;
                _lv.Items.Add(item);
                shown++;
            }
            _lv.EndUpdate();

            if (shown == 0)
                _lblSelection.Text = "未发现USB磁盘。请插入U盘后点击“刷新设备”。";
            else
                _lblSelection.Text = "已列出 " + shown.ToString() + " 个磁盘，请选择要检测的U盘。";

            OnSelectionChanged();
        }

        private void OnShowAllChanged()
        {
            _cbFixed.Visible = _cbAll.Checked;
            if (!_cbAll.Checked) _cbFixed.Checked = false;
            ApplyFilter();
        }

        private void OnSafeChanged()
        {
            bool safe = _cbSafe.Checked;

            _rbQuick.Enabled = !_running && !safe;
            _rbFull.Enabled = !_running && !safe;
            _lblModeWarning.Text = safe
                ? "⚠ 安全模式：只写入文件系统标记为空闲的区域，快速抽样判断容量，不改动原有文件；扩容盘仍可能损坏数据。"
                : "⚠ 检测会覆盖整块磁盘（包括分区表和数据），无法恢复；检测后需要重新格式化U盘。";
            _cbAck.Text = safe
                ? "我已了解：安全模式只写空闲区，但假盘仍可能损坏数据"
                : "我已确认：U盘中的数据可以清除，并已自行备份";

            UpdateStartState();
        }

        private void UpdateStartState()
        {
            DiskInfo disk = SelectedDisk();
            _btnStart.Enabled = !_running && !_demo && disk != null && !disk.IsSystem;
        }

        private DiskInfo SelectedDisk()
        {
            if (_lv.SelectedItems.Count == 0) return null;
            return _lv.SelectedItems[0].Tag as DiskInfo;
        }

        private void OnSelectionChanged()
        {
            DiskInfo disk = SelectedDisk();
            if (disk == null)
            {
                _lblSelection.Text = _allDisks.Count == 0
                    ? "未发现USB磁盘。请插入U盘后点击“刷新设备”。"
                    : "请选择一个磁盘。";
                _btnStart.Enabled = false;
                return;
            }

            string extra = disk.IsSystem ? "   【系统盘，禁止检测】" : "";
            _lblSelection.Text = "已选择：磁盘 " + disk.Index.ToString() + "   " + disk.Letters +
                "   " + disk.Model + "   " + Fmt.Size(disk.Size) + extra;
            _btnStart.Enabled = !_running && !_demo && !disk.IsSystem;
        }

        private void StartTest()
        {
            if (_running) return;

            DiskInfo disk = SelectedDisk();
            if (disk == null)
            {
                MessageBox.Show(this, "请先在列表中选择要检测的U盘。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (disk.IsSystem)
            {
                MessageBox.Show(this, "不能检测包含系统分区的磁盘。", "禁止操作", MessageBoxButtons.OK, MessageBoxIcon.Stop);
                return;
            }
            if (!disk.IsUsb && !_cbFixed.Checked)
            {
                MessageBox.Show(this, "所选磁盘不是 USB 接口设备。\r\n\r\n如果确定要检测内置硬盘，请勾选“允许检测非USB磁盘”。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            bool safe = _cbSafe.Checked;
            bool full = safe || _rbFull.Checked;

            if (!_cbAck.Checked)
            {
                MessageBox.Show(this, safe
                        ? "请先勾选“我已了解：安全模式只写空闲区，但假盘仍可能损坏数据”这一项。"
                        : "请先勾选“我已确认：U盘中的数据可以清除”这一项。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            VolumeInfo targetVolume = null;

            if (safe)
            {
                targetVolume = PickTestVolume(disk);
                if (targetVolume == null)
                {
                    MessageBox.Show(this,
                        "所选磁盘没有可用的已格式化卷，无法使用安全模式。\r\n请先格式化U盘，或取消“安全模式”后使用整盘检测。",
                        "无法使用安全模式", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (targetVolume.FreeSpace < 32L * 1024 * 1024)
                {
                    MessageBox.Show(this,
                        "卷 " + targetVolume.Letter + " 的可用空间不足（" + Fmt.Size(targetVolume.FreeSpace) + "），无法进行安全模式检测。",
                        "空间不足", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            else if (!RawDisk.IsWritable(disk.Index))
            {
                Log("警告：系统报告该磁盘为只读，仍将尝试写入（如失败请检查U盘写保护开关）。");
            }

            string estimate = safe
                ? "预计耗时：几秒到 1 分钟"
                : (full ? EstimateText(disk.Size) : "预计耗时：约 1–3 分钟");
            string message;

            if (safe)
            {
                message =
                    "安全模式（保留原有文件）：\r\n\r\n" +
                    "  目标卷：  " + targetVolume.Letter + "（空闲 " + Fmt.Size(targetVolume.FreeSpace) + "）\r\n" +
                    "  检测方式：只向文件系统标记为空闲的区域写入 " + Fmt.Size(524288) + " 测试标记\r\n" +
                    "  " + estimate + "\r\n\r\n" +
                    "不会创建/删除文件，也不会修改原有文件；测试期间该卷会临时卸载，结束后自动恢复。\r\n\r\n" +
                    "注意：如果这是扩容盘，写入仍可能损坏原有数据，请先备份重要数据。\r\n\r\n" +
                    "确定继续吗？";
            }
            else
            {
                message =
                    "即将对下列磁盘进行写入测试：\r\n\r\n" +
                    "  磁盘编号：" + disk.Index.ToString() + "\r\n" +
                    "  盘符：    " + disk.Letters + "\r\n" +
                    "  型号：    " + disk.Model + "\r\n" +
                    "  容量：    " + Fmt.Size(disk.Size) + "（" + Fmt.SizeGiB(disk.Size) + "）\r\n" +
                    "  检测方式：" + (full ? "完整校验" : "快速扫描") + "\r\n" +
                    "  " + estimate + "\r\n\r\n" +
                    "警告：U盘上的全部数据、分区表都将被覆盖，无法恢复！\r\n" +
                    "如果这是扩容盘，原有文件可能早已损坏。\r\n\r\n" +
                    "确定继续吗？";
            }

            if (MessageBox.Show(this, message, safe ? "操作确认" : "危险操作确认", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            StartWorker(disk, full, safe, targetVolume);
        }

        private VolumeInfo PickTestVolume(DiskInfo disk)
        {
            VolumeInfo best = null;
            foreach (VolumeInfo volume in disk.Volumes)
            {
                if (volume.Letter.Length != 2 || volume.Letter[1] != ':') continue;
                try
                {
                    DriveInfo info = new DriveInfo(volume.Letter + "\\");
                    if (!info.IsReady) continue;
                    volume.FreeSpace = info.AvailableFreeSpace;
                    if (volume.FreeSpace < 16L * 1024 * 1024) continue;
                    if (best == null || volume.FreeSpace > best.FreeSpace) best = volume;
                }
                catch
                {
                }
            }
            return best;
        }

        private void StartWorker(DiskInfo disk, bool full, bool safe, VolumeInfo volume)
        {
            _lastResult = null;
            _lastDisk = disk;
            _lastReport = null;
            _btnSave.Enabled = false;
            _btnOpenReport.Enabled = false;
            _txtLog.Clear();
            _bar.Value = 0;
            _lblVerdict.Text = "检测中 ...";
            _lblVerdict.ForeColor = Color.SteelBlue;
            _txtSummary.Text = "正在检测，请勿拔出U盘或关闭程序。";
            _lblStatus.Text = "准备中 ...";
            SetRunning(true);

            Log("========== 开始检测 ==========");
            Log("磁盘 " + disk.Index.ToString() + "（" + disk.Letters + "，" + disk.Model + "），" +
                (safe ? "安全模式（保留文件）" : (full ? "完整校验" : "快速扫描")));

            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            Progress<ProgressInfo> progress = new Progress<ProgressInfo>(OnProgress);

            Task.Run(delegate
            {
                DetectionResult result = null;
                string error = null;
                try
                {
                    if (safe)
                    {
                        Log("安全模式：卷 " + volume.Letter + "，只写入文件系统空闲区。");
                        result = SafeVolumeScanner.Scan(volume.Letter, disk.Size,
                            delegate(string msg) { Log(msg); }, progress, token);
                    }
                    else
                    {
                        DetectionOptions options = new DetectionOptions();
                        options.Full = full;
                        options.SampleCount = 160;

                        using (VolumeGuard guard = new VolumeGuard(disk.VolumeLetters, delegate(string msg) { Log(msg); }))
                        {
                            result = Detector.Run(
                                delegate(bool writable)
                                {
                                    RawDisk raw = new RawDisk(disk.Index, writable, disk.Size);
                                    if (writable)
                                    {
                                        Log("磁盘容量读取方式：" + raw.LengthSource + "，容量 " + Fmt.Size(raw.Length) + "。");
                                        if (raw.LengthSource.IndexOf("备用", StringComparison.Ordinal) >= 0)
                                            Log("警告：无法从设备读取容量，改用系统报告值，检测结果可能不准确。");
                                    }
                                    return raw;
                                },
                                disk.Size,
                                options,
                                delegate(string msg) { Log(msg); },
                                progress,
                                token);
                        }
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                try
                {
                    BeginInvoke(new Action<DetectionResult, string>(OnTestFinished), result, error);
                }
                catch
                {
                    // 窗口已关闭
                }
            });
        }

        private void OnTestFinished(DetectionResult result, string error)
        {
            SetRunning(false);
            if (_cts != null)
            {
                _cts.Dispose();
                _cts = null;
            }

            if (error != null)
            {
                _lblVerdict.Text = "检测失败";
                _lblVerdict.ForeColor = Color.Firebrick;
                _txtSummary.Text = error;
                _lblStatus.Text = "检测失败。";
                Log("检测失败：" + error);
                MessageBox.Show(this, "检测失败：\r\n\r\n" + error, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _lastResult = result;
            ShowResult(result);

            if (_demo)
            {
                _lblStatus.Text = "演示完成：" + result.VerdictText + "（模拟设备，未写入真实磁盘）。";
                Log("========== 演示结束：" + result.VerdictText + " ==========");
                return;
            }

            try
            {
                _lastReport = ReportWriter.Save(result, _lastDisk);
                _btnOpenReport.Enabled = true;
                Log("检测报告已保存：" + _lastReport);
            }
            catch (Exception ex)
            {
                Log("保存报告失败：" + ex.Message);
            }

            _btnSave.Enabled = true;
            _lblStatus.Text = "检测完成：" + result.VerdictText + "，用时 " + Fmt.Duration(result.Elapsed.TotalSeconds) + "。";
            Log("========== 检测结束：" + result.VerdictText + " ==========");

            RefreshDisks();
        }

        private void ShowResult(DetectionResult result)
        {
            string title;
            Color color;

            switch (result.Verdict)
            {
                case DetectionVerdict.Genuine:
                    title = "✔ 正常：未发现扩容迹象";
                    color = Color.SeaGreen;
                    break;
                case DetectionVerdict.Fake:
                    title = "✘ 疑似扩容盘";
                    color = Color.Firebrick;
                    break;
                case DetectionVerdict.Damaged:
                    title = "⚠ 设备异常：数据无法正确读回";
                    color = Color.DarkOrange;
                    break;
                case DetectionVerdict.Cancelled:
                    title = "检测已取消";
                    color = Color.DimGray;
                    break;
                case DetectionVerdict.Failed:
                    title = "检测失败";
                    color = Color.Firebrick;
                    break;
                default:
                    title = "未知结果";
                    color = Color.Gray;
                    break;
            }

            _lblVerdict.Text = title;
            _lblVerdict.ForeColor = color;

            StringBuilder sb = new StringBuilder();
            if (result.Verdict == DetectionVerdict.Fake)
            {
                sb.Append("结论：实际容量小于标称容量，属于扩容盘，继续使用会丢失数据。");
                if (result.EstimatedCapacity.HasValue)
                    sb.Append("实际可用容量约 " + Fmt.Size(result.EstimatedCapacity.Value) +
                        "（" + Fmt.SizeGiB(result.EstimatedCapacity.Value) + "）。");
                sb.AppendLine();
                sb.AppendLine("建议：立即停止使用，联系商家退货，不要存放重要数据。");
                sb.AppendLine();
            }
            else if (result.Verdict == DetectionVerdict.Damaged)
            {
                sb.AppendLine("结论：写入的数据无法在标称容量内正确读回，可能是扩容盘或闪存/主控故障。");
                sb.AppendLine("建议：换一块U盘复测，并避免在该盘保存重要数据。");
                sb.AppendLine();
            }
            else if (result.Verdict == DetectionVerdict.Genuine)
            {
                sb.AppendLine("结论：本次检测未发现容量虚标。");
                if (!result.Mode.Contains("完整"))
                    sb.AppendLine("注意：快速扫描为抽样检查，如需完全确认请执行“完整校验”。");
                sb.AppendLine();
            }
            else if (result.Verdict == DetectionVerdict.Cancelled)
            {
                sb.AppendLine("检测被用户取消，结果不完整。");
                sb.AppendLine();
            }
            else if (result.Verdict == DetectionVerdict.Failed)
            {
                sb.AppendLine("检测未能完成，请根据下方错误信息排查后重试。");
                sb.AppendLine();
            }

            sb.AppendLine("检测方式：" + result.Mode);
            sb.AppendLine("声称容量：" + Fmt.Size(result.ClaimedBytes) + "（" + Fmt.SizeGiB(result.ClaimedBytes) + "）");
            if (result.EstimatedCapacity.HasValue)
                sb.AppendLine("实际可用容量：约 " + Fmt.Size(result.EstimatedCapacity.Value) +
                    "（" + Fmt.SizeGiB(result.EstimatedCapacity.Value) + "）");
            sb.AppendLine("写入数据：" + Fmt.Size(result.BytesWritten) + "    读回数据：" + Fmt.Size(result.BytesRead));
            sb.AppendLine("检测用时：" + Fmt.Duration(result.Elapsed.TotalSeconds));
            if (result.FirstBadOffset >= 0)
                sb.AppendLine("首个异常位置：" + Fmt.Size(result.FirstBadOffset));
            if (result.Error != null) sb.AppendLine("错误：" + result.Error);
            if (!_demo)
                if (result.Mode != null && result.Mode.IndexOf("安全模式", StringComparison.Ordinal) >= 0)
                    sb.AppendLine("提示：安全模式只写入空闲区，原有文件未被主动修改。");
                else if (result.Mode != null && result.Mode.IndexOf("文件模式", StringComparison.Ordinal) >= 0)
                    sb.AppendLine("提示：临时测试文件已删除，原有文件未被主动修改。");
                else
                    sb.AppendLine("提示：检测数据已覆盖分区表，请重新格式化U盘后再使用。");

            _txtSummary.Text = sb.ToString();

            foreach (string line in result.Evidence) Log("证据：" + line);
            foreach (string line in result.Warnings) Log("提示：" + line);
        }

        private void OnProgress(ProgressInfo info)
        {
            if (info == null) return;
            _bar.Value = Math.Max(0, Math.Min(100, info.Percent));

            string text = info.Phase + "  " + info.Percent.ToString() + "%";
            if (info.Speed > 0) text += "  ·  " + Fmt.Size((long)info.Speed) + "/s";
            if (!string.IsNullOrEmpty(info.Extra)) text += "  ·  " + info.Extra;
            _lblStatus.Text = text;
        }

        private void SetRunning(bool running)
        {
            _running = running;
            DiskInfo disk = SelectedDisk();
            _btnStart.Enabled = !running && !_demo && disk != null && !disk.IsSystem;
            _btnStop.Enabled = running;
            _btnRefresh.Enabled = !running;
            _cbAll.Enabled = !running;
            _cbAck.Enabled = !running;
            _cbFixed.Enabled = !running;
            _cbSafe.Enabled = !running;
            _rbQuick.Enabled = !running && !_cbSafe.Checked;
            _rbFull.Enabled = !running && !_cbSafe.Checked;
            _lv.Enabled = !running;
        }

        private string EstimateText(long bytes)
        {
            double bestSeconds = bytes * 2.0 / (80.0 * 1000 * 1000);
            double worstSeconds = bytes * 2.0 / (12.0 * 1000 * 1000);
            return "预计耗时：" + Fmt.Duration(bestSeconds) + " 至 " + Fmt.Duration(worstSeconds) + "（取决于U盘速度）";
        }

        private void SaveReport()
        {
            if (_lastResult == null) return;
            SaveFileDialog dialog = new SaveFileDialog();
            dialog.Title = "保存检测报告";
            dialog.Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
            dialog.FileName = "U盘检测报告_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";
            dialog.InitialDirectory = ReportWriter.ReportFolder();
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                File.WriteAllText(dialog.FileName, ReportWriter.Build(_lastResult, _lastDisk), new UTF8Encoding(true));
                _lastReport = dialog.FileName;
                _btnOpenReport.Enabled = true;
                Log("报告已保存：" + dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存报告失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OpenReport()
        {
            if (string.IsNullOrEmpty(_lastReport) || !File.Exists(_lastReport))
            {
                MessageBox.Show(this, "报告文件不存在。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(_lastReport) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法打开报告：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_running) return;
            if (MessageBox.Show(this, "检测正在进行中，确定要停止并退出吗？\r\n强制退出可能导致U盘数据状态不完整。",
                    "确认退出", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            if (_cts != null) _cts.Cancel();
            Thread.Sleep(1500);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ApplyDpiScale();
        }

        /// <summary>按系统 DPI 等比缩放所有控件，使 150% 等缩放下界面清晰且不裁切。</summary>
        private void ApplyDpiScale()
        {
            try
            {
                using (Graphics graphics = CreateGraphics())
                    _uiScale = graphics.DpiX / 96f;
            }
            catch
            {
                _uiScale = 1f;
            }

            if (_uiScale <= 1.01f) return;

            SuspendLayout();
            Scale(new SizeF(_uiScale, _uiScale));
            foreach (ColumnHeader column in _lv.Columns)
                column.Width = (int)Math.Round(column.Width * _uiScale);
            MinimumSize = new Size(
                (int)Math.Round(760 * _uiScale),
                (int)Math.Round(640 * _uiScale));
            ResumeLayout(true);
            LayoutListColumns();

            Log("界面缩放：" + (_uiScale * 100).ToString("0") + "%（DPI " +
                Math.Round(96 * _uiScale).ToString() + "）");
        }

        /// <summary>列表列宽随窗口宽度自适应：变窄时等比压缩，变宽时把多余宽度给“型号”。</summary>
        private void LayoutListColumns()
        {
            if (_lv == null || _lv.Columns.Count != ColumnBaseWidths.Length) return;

            int available = _lv.ClientSize.Width - 2;
            if (available < 120) return;

            float scale = _uiScale <= 0f ? 1f : _uiScale;
            int count = ColumnBaseWidths.Length;
            int[] widths = new int[count];
            int total = 0;

            for (int i = 0; i < count; i++)
            {
                widths[i] = Math.Max(24, (int)Math.Round(ColumnBaseWidths[i] * scale));
                total += widths[i];
            }

            if (total < available)
            {
                widths[6] += available - total;
            }
            else if (total > available)
            {
                double factor = (double)available / total;
                int sum = 0;
                for (int i = 0; i < count; i++)
                {
                    widths[i] = Math.Max(24, (int)Math.Floor(widths[i] * factor));
                    sum += widths[i];
                }
                widths[6] += available - sum;
                if (widths[6] < 24) widths[6] = 24;
            }

            _lv.BeginUpdate();
            for (int i = 0; i < count; i++) _lv.Columns[i].Width = widths[i];
            _lv.EndUpdate();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Rectangle area = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
            if (Width > area.Width) Width = area.Width;
            if (Height > area.Height) Height = area.Height;
            LayoutListColumns();

            if (_demo)
            {
                BeginInvoke(new Action(RunDemo));
            }
        }

        /// <summary>演示模式：用内存中的模拟扩容盘跑一遍完整流程，不访问真实磁盘。</summary>
        private void RunDemo()
        {
            if (_running) return;

            _lastResult = null;
            _lastDisk = null;
            _lastReport = null;
            _btnSave.Enabled = false;
            _btnOpenReport.Enabled = false;
            _txtLog.Clear();
            _bar.Value = 0;
            _lblVerdict.Text = "演示检测中 ...";
            _lblVerdict.ForeColor = Color.SteelBlue;
            _txtSummary.Text = "正在模拟检测一块“标称 64 MB、实际 8 MB”的扩容盘（不会写入真实磁盘）。";
            _lblStatus.Text = "演示模式 ...";
            SetRunning(true);
            Log("========== 演示模式（模拟设备，不写真实磁盘） ==========");

            DetectionOptions options = new DetectionOptions();
            options.Full = false;
            options.SampleCount = 96;

            Progress<ProgressInfo> progress = new Progress<ProgressInfo>(OnProgress);
            Task.Run(delegate
            {
                SimDisk sim = new SimDisk(64L << 20, 8L << 20, SimMode.Wrap);
                DetectionResult result = Detector.Run(sim.Open, sim.ClaimedBytes, options,
                    delegate(string msg) { Log(msg); }, progress, CancellationToken.None);
                try
                {
                    BeginInvoke(new Action<DetectionResult, string>(OnTestFinished), result, null);
                }
                catch
                {
                }
            });
        }

        private void Log(string message)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(Log), message); }
                catch { }
                return;
            }
            if (_txtLog.TextLength > 300000) _txtLog.Clear();
            _txtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine);
        }
    }
}

















