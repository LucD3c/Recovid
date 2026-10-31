using System;
using System.Drawing;
using System.Windows.Forms;

namespace ClipRecorder
{
    internal sealed class SettingsForm : Form
    {
        private static readonly string[] SizeLabels = { "Igual al área elegida (recomendado)", "1920 × 1080 (Full HD)", "1280 × 720 (HD)", "854 × 480" };
        private static readonly int[,] SizeValues = { { 0, 0 }, { 1920, 1080 }, { 1280, 720 }, { 854, 480 } };
        private static readonly int[] FpsValues = { 15, 24, 30, 60 };

        private readonly Settings _settings;
        private readonly string _baseDir;
        private readonly NumericUpDown _duration = new NumericUpDown { Minimum = 1, Maximum = 3600, Width = 90 };
        private readonly ComboBox _quality = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        private readonly ComboBox _fps = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        private readonly ComboBox _size = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        private readonly ComboBox _tracking = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        private readonly CheckBox _systemAudio = new CheckBox { Text = "Grabar el audio del sistema", AutoSize = true };
        private readonly CheckBox _mic = new CheckBox { Text = "Grabar el micrófono", AutoSize = true };
        private readonly CheckBox _gpu = new CheckBox { Text = "Codificar con la GPU (más liviano, pero tarda más en arrancar)", AutoSize = true };
        private readonly CheckBox _autoStart = new CheckBox { Text = "Iniciar automáticamente con Windows", AutoSize = true };
        private readonly TextBox _hotkey = new TextBox { ReadOnly = true, Width = 260, BackColor = SystemColors.Window };
        private readonly TextBox _folder = new TextBox { Width = 260 };
        private string _hotkeyText;

        public SettingsForm(Settings settings, string baseDir)
        {
            _settings = settings;
            _baseDir = baseDir;

            Text = "ClipRecorder - Configuración";
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            ShowInTaskbar = true;
            TopMost = true;
            Icon = IconFactory.CreateIcon(32, false);

            var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(14) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            AddRow(grid, "Duración (segundos):", _duration);
            AddRow(grid, "Calidad:", _quality);
            AddRow(grid, "Cuadros por segundo:", _fps);
            AddRow(grid, "Tamaño del video:", _size);
            AddRow(grid, "Seguimiento por defecto:", _tracking);
            AddRow(grid, "Atajo de teclado:", _hotkey);

            var folderPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            var browse = new Button { Text = "...", Width = 34, Height = _folder.Height + 2 };
            browse.Click += delegate { BrowseFolder(); };
            folderPanel.Controls.Add(_folder);
            folderPanel.Controls.Add(browse);
            AddRow(grid, "Carpeta de videos:", folderPanel);

            foreach (var cb in new Control[] { _systemAudio, _mic, _gpu, _autoStart })
            {
                grid.Controls.Add(new Label { AutoSize = true });
                grid.Controls.Add(cb);
            }

            var hint = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(460, 0),
                ForeColor = SystemColors.GrayText,
                Text = "Para cambiar el atajo, hacé clic en su casilla y presioná la combinación nueva (por ejemplo Ctrl+Alt+Shift+R)."
            };
            grid.Controls.Add(new Label { AutoSize = true });
            grid.Controls.Add(hint);

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
            var save = new Button { Text = "Guardar", AutoSize = true, DialogResult = DialogResult.None };
            var cancel = new Button { Text = "Cancelar", AutoSize = true, DialogResult = DialogResult.Cancel };
            save.Click += delegate { Save(); };
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);
            grid.Controls.Add(new Label { AutoSize = true });
            grid.Controls.Add(buttons);

            Controls.Add(grid);
            AcceptButton = save;
            CancelButton = cancel;

            _quality.Items.AddRange(new object[] { "Baja (archivo chico)", "Media", "Alta (recomendada)", "Máxima (archivo grande)" });
            foreach (int f in FpsValues) _fps.Items.Add(f + " fps");
            _size.Items.AddRange(SizeLabels);
            _tracking.Items.AddRange(new object[] { "Ninguno (área fija)", "Seguir el mouse", "Seguir un objeto" });

            _duration.Value = settings.DurationSeconds;
            _quality.SelectedIndex = Math.Max(0, Array.FindIndex(Settings.QualityNames, q => string.Equals(q, settings.Quality, StringComparison.OrdinalIgnoreCase)));
            int fpsIndex = Array.IndexOf(FpsValues, settings.Fps);
            _fps.SelectedIndex = fpsIndex >= 0 ? fpsIndex : 2;
            _size.SelectedIndex = 0;
            for (int i = 0; i < SizeLabels.Length; i++)
                if (SizeValues[i, 0] == settings.OutputWidth && SizeValues[i, 1] == settings.OutputHeight) _size.SelectedIndex = i;
            _tracking.SelectedIndex = (int)settings.TrackingValue;
            _systemAudio.Checked = settings.RecordSystemAudio;
            _mic.Checked = settings.RecordMicrophone;
            _gpu.Checked = settings.HardwareEncoder;
            _autoStart.Checked = Shortcuts.IsAutoStartEnabled();
            _hotkeyText = settings.Hotkey;
            _hotkey.Text = _hotkeyText;
            _folder.Text = settings.ResolveOutputFolder(baseDir);

            _hotkey.KeyDown += OnHotkeyKeyDown;
        }

        private static void AddRow(TableLayoutPanel grid, string label, Control control)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 10, 3) });
            grid.Controls.Add(control);
        }

        private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
            Keys key = e.KeyCode;
            if (key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu || key == Keys.LWin || key == Keys.RWin) return;
            bool isFunctionKey = key >= Keys.F1 && key <= Keys.F24;
            if (e.Modifiers == Keys.None && !isFunctionKey) return;
            _hotkeyText = HotkeyText.FromKeys(e.Modifiers, key);
            _hotkey.Text = _hotkeyText;
        }

        private void BrowseFolder()
        {
            using (var dialog = new FolderBrowserDialog { Description = "Elegí la carpeta donde se guardan los videos", SelectedPath = _folder.Text })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK) _folder.Text = dialog.SelectedPath;
            }
        }

        private void Save()
        {
            uint mods, vk;
            if (!HotkeyText.TryParse(_hotkeyText, out mods, out vk))
            {
                MessageBox.Show(this, "El atajo de teclado no es válido.", "ClipRecorder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _settings.DurationSeconds = (int)_duration.Value;
            _settings.Quality = Settings.QualityNames[Math.Max(0, _quality.SelectedIndex)];
            _settings.Fps = FpsValues[Math.Max(0, _fps.SelectedIndex)];
            _settings.OutputWidth = SizeValues[_size.SelectedIndex, 0];
            _settings.OutputHeight = SizeValues[_size.SelectedIndex, 1];
            _settings.Tracking = ((TrackingMode)_tracking.SelectedIndex).ToString();
            _settings.RecordSystemAudio = _systemAudio.Checked;
            _settings.RecordMicrophone = _mic.Checked;
            _settings.HardwareEncoder = _gpu.Checked;
            _settings.AutoStart = _autoStart.Checked;
            _settings.Hotkey = _hotkeyText;
            string folder = _folder.Text.Trim().TrimEnd('\\');
            string defaultFolder = System.IO.Path.Combine(_baseDir, "Recordings");
            // Keep the default relative so the project folder can be moved or copied.
            _settings.OutputFolder = string.Equals(folder, defaultFolder, StringComparison.OrdinalIgnoreCase) ? "Recordings" : folder;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
