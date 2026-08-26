using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace DreamClubKoreanPatcher
{
    internal sealed class MainForm : Form
    {
        private readonly string[] stepNames =
        {
            "ISO 확인",
            "작업 폴더 준비",
            "XISO 추출",
            "XEX 확인",
            "전체 한국어 패치 적용",
            "ISO 재패킹"
        };

        private readonly Label[] stateLabels;
        private readonly Panel dropPanel;
        private readonly Label isoStatusLabel;
        private readonly Label xexStatusLabel;
        private readonly Button startButton;
        private readonly ProgressBar progressBar;
        private readonly TextBox logBox;
        private readonly BackgroundWorker worker;

        private string isoPath;
        private string xexToolPath;

        public MainForm()
        {
            Font = new Font("맑은 고딕", 9F, FontStyle.Regular, GraphicsUnit.Point);
            Text = "DreamClubKoreanPatcher";
            BackColor = Color.FromArgb(248, 249, 252);
            ClientSize = new Size(960, 540);
            MinimumSize = new Size(820, 500);
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(18, 16, 18, 16);
            root.ColumnCount = 2;
            root.RowCount = 3;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 68F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 32F));
            Controls.Add(root);

            dropPanel = BuildDropPanel(out isoStatusLabel, out xexStatusLabel);
            root.Controls.Add(dropPanel, 0, 0);

            Panel progressPanel = new Panel();
            progressPanel.Dock = DockStyle.Fill;
            progressPanel.Padding = new Padding(28, 5, 4, 0);
            root.Controls.Add(progressPanel, 1, 0);

            Label heading = new Label();
            heading.AutoSize = true;
            heading.Font = new Font(Font, FontStyle.Bold);
            heading.Text = "진행 상태";
            heading.Location = new Point(26, 9);
            progressPanel.Controls.Add(heading);

            TableLayoutPanel steps = new TableLayoutPanel();
            steps.Location = new Point(24, 50);
            steps.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            steps.Width = progressPanel.Width - 48;
            steps.Height = 240;
            steps.ColumnCount = 3;
            steps.RowCount = stepNames.Length;
            steps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58F));
            steps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24F));
            steps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18F));
            for (int i = 0; i < stepNames.Length; ++i)
            {
                steps.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            }
            progressPanel.Controls.Add(steps);
            progressPanel.Resize += delegate { steps.Width = Math.Max(300, progressPanel.ClientSize.Width - 48); };

            stateLabels = new Label[stepNames.Length];
            for (int i = 0; i < stepNames.Length; ++i)
            {
                Label name = NewStepLabel(stepNames[i]);
                steps.Controls.Add(name, 0, i);
                Label state = NewStepLabel("대기");
                state.ForeColor = Color.FromArgb(72, 103, 169);
                stateLabels[i] = state;
                steps.Controls.Add(state, 1, i);
            }

            startButton = new Button();
            startButton.Text = "패치 시작";
            startButton.Size = new Size(138, 56);
            startButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            startButton.Location = new Point(progressPanel.ClientSize.Width - 154, 132);
            startButton.FlatStyle = FlatStyle.System;
            startButton.Enabled = false;
            startButton.Click += StartButtonClick;
            progressPanel.Controls.Add(startButton);
            startButton.BringToFront();
            progressPanel.Resize += delegate
            {
                startButton.Location = new Point(
                    Math.Max(360, progressPanel.ClientSize.Width - 154), 132);
            };

            progressBar = new ProgressBar();
            progressBar.Dock = DockStyle.Fill;
            progressBar.Style = ProgressBarStyle.Continuous;
            root.SetColumnSpan(progressBar, 2);
            root.Controls.Add(progressBar, 0, 1);

            logBox = new TextBox();
            logBox.Dock = DockStyle.Fill;
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.BackColor = Color.White;
            logBox.BorderStyle = BorderStyle.FixedSingle;
            logBox.Text = "대기 중" + Environment.NewLine;
            root.SetColumnSpan(logBox, 2);
            root.Controls.Add(logBox, 0, 2);

            DragEnter += FilesDragEnter;
            DragDrop += FilesDragDrop;
            dropPanel.DragEnter += FilesDragEnter;
            dropPanel.DragDrop += FilesDragDrop;
            AttachDropEvents(dropPanel);

            worker = new BackgroundWorker();
            worker.DoWork += WorkerDoWork;
            worker.RunWorkerCompleted += WorkerCompleted;
        }

        private Panel BuildDropPanel(out Label isoLabel, out Label xexLabel)
        {
            Panel panel = new Panel();
            panel.Dock = DockStyle.Fill;
            panel.Margin = new Padding(0, 0, 0, 10);
            panel.BackColor = Color.White;
            panel.BorderStyle = BorderStyle.FixedSingle;
            panel.AllowDrop = true;
            panel.Cursor = Cursors.Hand;
            panel.Click += SelectFilesClick;

            Label instruction = new Label();
            instruction.AutoSize = false;
            instruction.TextAlign = ContentAlignment.MiddleCenter;
            instruction.Font = new Font(Font, FontStyle.Bold);
            instruction.ForeColor = Color.FromArgb(29, 61, 122);
            instruction.Text = "ISO 및 필수 파일을 여기에" + Environment.NewLine +
                "드래그 드롭하거나 클릭해서 선택";
            instruction.Dock = DockStyle.Top;
            instruction.Height = 112;
            instruction.Padding = new Padding(0, 48, 0, 0);
            instruction.Click += SelectFilesClick;
            panel.Controls.Add(instruction);

            isoLabel = new Label();
            isoLabel.AutoEllipsis = true;
            isoLabel.ForeColor = Color.FromArgb(221, 57, 47);
            isoLabel.Text = "× (필수) 정품 게임 ISO";
            isoLabel.SetBounds(34, 156, 300, 28);
            isoLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            isoLabel.Click += SelectFilesClick;
            panel.Controls.Add(isoLabel);

            xexLabel = new Label();
            xexLabel.AutoEllipsis = true;
            xexLabel.ForeColor = Color.FromArgb(221, 57, 47);
            xexLabel.Text = "× (필수) xextool.exe 6.3";
            xexLabel.SetBounds(34, 198, 300, 28);
            xexLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            xexLabel.Click += SelectFilesClick;
            panel.Controls.Add(xexLabel);
            return panel;
        }

        private static Label NewStepLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.ForeColor = Color.FromArgb(72, 103, 169);
            return label;
        }

        private void AttachDropEvents(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                control.AllowDrop = true;
                control.DragEnter += FilesDragEnter;
                control.DragDrop += FilesDragDrop;
                if (control.HasChildren) AttachDropEvents(control);
            }
        }

        private void SelectFilesClick(object sender, EventArgs e)
        {
            if (worker.IsBusy) return;
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "게임 ISO와 xextool.exe 선택";
                dialog.Filter = "필수 파일 (*.iso;*.exe)|*.iso;*.exe|모든 파일 (*.*)|*.*";
                dialog.Multiselect = true;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    AcceptFiles(dialog.FileNames);
                }
            }
        }

        private void FilesDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void FilesDragDrop(object sender, DragEventArgs e)
        {
            if (worker.IsBusy) return;
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null) AcceptFiles(files);
        }

        private void AcceptFiles(string[] paths)
        {
            foreach (string path in paths)
            {
                if (!File.Exists(path)) continue;
                string extension = Path.GetExtension(path);
                if (String.Equals(extension, ".iso", StringComparison.OrdinalIgnoreCase))
                {
                    isoPath = Path.GetFullPath(path);
                }
                else if (String.Equals(Path.GetFileName(path), "xextool.exe", StringComparison.OrdinalIgnoreCase))
                {
                    xexToolPath = Path.GetFullPath(path);
                }
            }
            RefreshFileState();
        }

        private void RefreshFileState()
        {
            bool hasIso = !String.IsNullOrEmpty(isoPath) && File.Exists(isoPath);
            bool hasXex = !String.IsNullOrEmpty(xexToolPath) && File.Exists(xexToolPath);
            isoStatusLabel.ForeColor = hasIso ? Color.FromArgb(31, 139, 76) : Color.FromArgb(221, 57, 47);
            xexStatusLabel.ForeColor = hasXex ? Color.FromArgb(31, 139, 76) : Color.FromArgb(221, 57, 47);
            isoStatusLabel.Text = hasIso ? "✓ ISO: " + Path.GetFileName(isoPath) : "× (필수) 정품 게임 ISO";
            xexStatusLabel.Text = hasXex ? "✓ XEX: " + Path.GetFileName(xexToolPath) : "× (필수) xextool.exe 6.3";
            startButton.Enabled = hasIso && hasXex && !worker.IsBusy;
        }

        private void StartButtonClick(object sender, EventArgs e)
        {
            startButton.Enabled = false;
            dropPanel.Enabled = false;
            progressBar.Value = 0;
            logBox.Clear();
            for (int i = 0; i < stateLabels.Length; ++i) stateLabels[i].Text = "대기";
            worker.RunWorkerAsync(new[] { isoPath, xexToolPath });
        }

        private void WorkerDoWork(object sender, DoWorkEventArgs e)
        {
            string[] arguments = (string[])e.Argument;
            PatchRunner runner = new PatchRunner(AppDomain.CurrentDomain.BaseDirectory);
            runner.StepChanged += delegate(int index, string state)
            {
                BeginInvoke((MethodInvoker)delegate { stateLabels[index].Text = state; });
            };
            runner.LogReceived += delegate(string line)
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    logBox.AppendText(line + Environment.NewLine);
                });
            };
            runner.ProgressChanged += delegate(int value)
            {
                BeginInvoke((MethodInvoker)delegate { progressBar.Value = value; });
            };
            e.Result = runner.Run(arguments[0], arguments[1]);
        }

        private void WorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            dropPanel.Enabled = true;
            RefreshFileState();
            if (e.Error != null)
            {
                logBox.AppendText("실패: " + e.Error.Message + Environment.NewLine);
                MessageBox.Show(this, e.Error.Message, "패치 실패",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            string outputPath = Convert.ToString(e.Result);
            logBox.AppendText("완료: " + outputPath + Environment.NewLine);
            MessageBox.Show(this, "한국어 패치 ISO를 만들었습니다.\n\n" + outputPath,
                "패치 완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
