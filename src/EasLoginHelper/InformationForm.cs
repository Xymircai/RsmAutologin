// SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;

namespace EasLoginHelper;

public sealed class InformationForm : Form
{
    public InformationForm(UiLayoutStore? layouts = null, string? title = null, string? report = null)
    {
        Text = title ?? "使用说明、源码与许可";
        UiTheme.ConfigureWindow(this, new Size(780, 600), new Size(360, 260), true);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(16) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var source = new LinkLabel { Text = "v" + Branding.Version + " 发行版源码（MPL 2.0）", AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
        source.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(EmbeddedAssets.SourceUrl) { UseShellExecute = true }); root.Controls.Add(source, 0, 0);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        void Tab(string name, string text)
        {
            var tab = new TabPage(name); tab.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
                ScrollBars = ScrollBars.Vertical, WordWrap = true, BackColor = Color.White, Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n") }); tabs.TabPages.Add(tab);
        }
        if (report is not null) Tab("自检报告", report);
        Tab("使用说明", EmbeddedAssets.Text("EasLoginHelper.Help.txt")); Tab("许可", EmbeddedAssets.Licenses()); root.Controls.Add(tabs, 0, 1);
        var export = UiTheme.Button("导出许可文本", () =>
        {
            using var save = new SaveFileDialog { FileName = "RsmAutologin-LICENSES.txt", Filter = "文本文件 (*.txt)|*.txt" };
            if (save.ShowDialog(this) == DialogResult.OK)
                try { File.WriteAllText(save.FileName, "Source: " + EmbeddedAssets.SourceUrl + Environment.NewLine + EmbeddedAssets.Licenses()); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { MessageBox.Show(this, "无法写入所选位置，请选择其他文件夹。", "导出许可"); }
        });
        var close = UiTheme.Button("关闭", Close); close.DialogResult = DialogResult.Cancel;
        root.Controls.Add(UiTheme.DialogActions(export, close), 0, 2); Controls.Add(root); CancelButton = close;
        (layouts ?? new UiLayoutStore()).Attach(this, "information");
    }
}
