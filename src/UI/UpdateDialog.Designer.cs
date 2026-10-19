using System.Drawing;
using System.Windows.Forms;

namespace LiteMonitor
{
    partial class UpdateDialog
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        private System.Windows.Forms.Label lblVersion;
        private System.Windows.Forms.RichTextBox rtbChangelog;
        private System.Windows.Forms.ProgressBar progress;
        private System.Windows.Forms.Button btnUpdate;
        private System.Windows.Forms.Button btnCancel;
        // [新增] 状态标签：显示下载速度和进度
        private System.Windows.Forms.Label lblStatus;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            lblVersion = new Label();
            rtbChangelog = new RichTextBox();
            progress = new ProgressBar();
            btnUpdate = new Button();
            btnCancel = new Button();
            lblStatus = new Label();
            label1 = new Label();
            SuspendLayout();
            // 
            // lblVersion
            // 
            lblVersion.AutoSize = true;
            lblVersion.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
            lblVersion.Location = new Point(79, 52);
            lblVersion.Name = "lblVersion";
            lblVersion.Size = new Size(186, 22);
            lblVersion.TabIndex = 0;
            lblVersion.Text = "⚡️LiteMonitor++_v1.0.6";
            lblVersion.TextAlign = ContentAlignment.MiddleCenter;
            // 
            // rtbChangelog
            // 
            rtbChangelog.BackColor = SystemColors.ControlLight;
            rtbChangelog.BorderStyle = BorderStyle.None;
            rtbChangelog.Font = new Font("Microsoft YaHei UI", 10F);
            rtbChangelog.Location = new Point(23, 91);
            rtbChangelog.Name = "rtbChangelog";
            rtbChangelog.ReadOnly = true;
            rtbChangelog.Size = new Size(319, 121);
            rtbChangelog.TabIndex = 1;
            rtbChangelog.Text = "";
            // 
            // progress
            // 
            progress.Location = new Point(23, 245);
            progress.Name = "progress";
            progress.Size = new Size(300, 22);
            progress.TabIndex = 2;
            // 
            // btnUpdate
            // 
            btnUpdate.Location = new Point(82, 285);
            btnUpdate.Name = "btnUpdate";
            btnUpdate.Size = new Size(80, 30);
            btnUpdate.TabIndex = 3;
            btnUpdate.Text = "立即更新";
            btnUpdate.UseVisualStyleBackColor = true;
            btnUpdate.Click += btnUpdate_Click;
            // 
            // btnCancel
            // 
            btnCancel.Location = new Point(185, 285);
            btnCancel.Name = "btnCancel";
            btnCancel.Size = new Size(80, 30);
            btnCancel.TabIndex = 4;
            btnCancel.Text = "取消";
            btnCancel.UseVisualStyleBackColor = true;
            btnCancel.Click += btnCancel_Click;
            // 
            // lblStatus
            // 
            lblStatus.AutoSize = true;
            lblStatus.Font = new Font("Microsoft YaHei UI", 9F);
            lblStatus.ForeColor = SystemColors.GrayText;
            lblStatus.Location = new Point(23, 225);
            lblStatus.Name = "lblStatus";
            lblStatus.Size = new Size(56, 17);
            lblStatus.TabIndex = 5;
            lblStatus.Text = "准备就绪";
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
            label1.Location = new Point(122, 18);
            label1.Name = "label1";
            label1.Size = new Size(106, 22);
            label1.TabIndex = 6;
            label1.Text = "发现新版本！";
            label1.TextAlign = ContentAlignment.TopCenter;
            // 
            // UpdateDialog
            // 
            AutoScaleDimensions = new SizeF(7F, 17F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(348, 335);
            Controls.Add(label1);
            Controls.Add(lblStatus);
            Controls.Add(btnCancel);
            Controls.Add(btnUpdate);
            Controls.Add(progress);
            Controls.Add(rtbChangelog);
            Controls.Add(lblVersion);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "UpdateDialog";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "⚡️LiteMonitor++";
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Label label1;
    }
}
