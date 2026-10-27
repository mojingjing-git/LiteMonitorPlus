using System;
using System.Drawing;
using System.Windows.Forms;
using LiteMonitor.src.Core;

namespace LiteMonitor.ThemeEditor
{
    /// <summary>TM02：生成市场投稿资料前填写稳定元数据。</summary>
    internal sealed class ThemeSubmissionDialog : Form
    {
        private readonly TextBox _id = new();
        private readonly TextBox _name = new();
        private readonly TextBox _nameEn = new();
        private readonly TextBox _description = new();
        private readonly TextBox _descriptionEn = new();
        private readonly TextBox _author = new();
        private readonly TextBox _marketVersion = new();
        private readonly TextBox _license = new();

        public ThemeSubmissionMetadata Metadata { get; private set; }

        public ThemeSubmissionDialog(string themeName)
        {
            Metadata = ThemeExportService.CreateDefaultMetadata(themeName);
            Text = "市场投稿信息";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(620, 520);
            Font = new Font("Segoe UI", 9F);

            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(14),
                ColumnCount = 2,
                RowCount = 9
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 8; i++) table.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 3 || i == 4 ? 64 : 34));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            AddRow(table, 0, "投稿 ID", _id, Metadata.Id);
            AddRow(table, 1, "名称", _name, Metadata.Name);
            AddRow(table, 2, "英文名称", _nameEn, Metadata.NameEn);
            AddRow(table, 3, "描述", _description, Metadata.Description, multiline: true);
            AddRow(table, 4, "英文描述", _descriptionEn, Metadata.DescriptionEn, multiline: true);
            AddRow(table, 5, "作者", _author, Metadata.Author);
            AddRow(table, 6, "发布版本", _marketVersion, Metadata.MarketVersion);
            AddRow(table, 7, "许可证", _license, Metadata.License);

            var hint = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = SystemColors.GrayText,
                Text = "发布版本为 major.minor.patch；主题格式 version 仍由主题文件决定。",
                TextAlign = ContentAlignment.MiddleLeft,
                AutoSize = false
            };
            table.Controls.Add(hint, 1, 8);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 46,
                Padding = new Padding(0, 8, 14, 8)
            };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 88, Height = 28 };
            var ok = new Button { Text = "生成资料", Width = 104, Height = 28 };
            ok.Click += (_, _) => Confirm();
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            Controls.Add(table);
            Controls.Add(buttons);
            CancelButton = cancel;
            AcceptButton = ok;
        }

        private static void AddRow(TableLayoutPanel table, int row, string label, TextBox box, string value, bool multiline = false)
        {
            table.Controls.Add(new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, row);
            box.Text = value ?? string.Empty;
            box.Dock = DockStyle.Fill;
            box.Multiline = multiline;
            if (multiline) box.ScrollBars = ScrollBars.Vertical;
            table.Controls.Add(box, 1, row);
        }

        private void Confirm()
        {
            var metadata = new ThemeSubmissionMetadata
            {
                Id = _id.Text.Trim(),
                Name = _name.Text.Trim(),
                NameEn = _nameEn.Text.Trim(),
                Description = _description.Text.Trim(),
                DescriptionEn = _descriptionEn.Text.Trim(),
                Author = _author.Text.Trim(),
                MarketVersion = _marketVersion.Text.Trim(),
                License = _license.Text.Trim()
            };
            if (!ThemeExportService.TryValidateSubmissionMetadata(metadata, out string error))
            {
                MessageBox.Show(this, error, "投稿信息不完整", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Metadata = metadata;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
