using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GTWM.ModelConverter
{
    internal sealed class MainForm : Form
    {
        private static readonly Color Back = Color.FromArgb(245, 245, 247);
        private static readonly Color Card = Color.White;
        private static readonly Color Ink = Color.FromArgb(29, 29, 31);
        private static readonly Color Muted = Color.FromArgb(110, 110, 115);
        private static readonly Color Accent = Color.FromArgb(10, 132, 255);
        private static readonly Color Good = Color.FromArgb(40, 167, 69);
        private static readonly Color Bad = Color.FromArgb(215, 58, 73);

        private readonly Settings _settings = Settings.Load();
        private readonly Label _dropTitle;
        private readonly Label _dropHint;
        private readonly Panel _drop;
        private readonly TextBox _name;
        private readonly CheckBox _turn;
        private readonly Label _templateLabel;
        private readonly LinkLabel _templateLink;
        private readonly Label _outputLabel;
        private readonly PictureBox _preview;
        private readonly Label _status;
        private readonly TextBox _log;
        private readonly Button _openFolder;
        private readonly Button _again;
        private string _source;
        private string _templatePath;
        private string _outputDir;
        private bool _busy;

        public MainForm(string initialFile)
        {
            Text = "GTWM Model Converter";
            Font = new Font("Segoe UI", 9.75f);
            BackColor = Back;
            ForeColor = Ink;
            ClientSize = new Size(1040, 680);
            MinimumSize = new Size(900, 600);
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
            }

            var title = new Label { Text = "Model Converter", Font = new Font("Segoe UI Semibold", 20f), AutoSize = true, Location = new Point(28, 20) };
            var subtitle = new Label { Text = "Turn any 3D model into a Get To Work character. Rigging, scaling and posing are automatic.", ForeColor = Muted, AutoSize = true, Location = new Point(31, 62) };
            Controls.Add(title);
            Controls.Add(subtitle);

            // Left column.
            _drop = new RoundedPanel { Location = new Point(28, 100), Size = new Size(420, 210), BackColor = Card, Cursor = Cursors.Hand, Dashed = true };
            _dropTitle = new Label { Text = "Drop a model here", Font = new Font("Segoe UI Semibold", 14f), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Top, Height = 120, Padding = new Padding(0, 40, 0, 0) };
            _dropHint = new Label { Text = "or click to browse\n.fbx  .obj  .glb  .gltf  .dae  .blend  .pmx  .3ds  and more", ForeColor = Muted, TextAlign = ContentAlignment.TopCenter, Dock = DockStyle.Fill };
            _drop.Controls.Add(_dropHint);
            _drop.Controls.Add(_dropTitle);
            foreach (Control c in new Control[] { _drop, _dropTitle, _dropHint })
            {
                c.Click += (s, e) => Browse();
                c.AllowDrop = true;
                c.DragEnter += OnDragEnter;
                c.DragDrop += OnDragDrop;
            }
            Controls.Add(_drop);

            var options = new RoundedPanel { Location = new Point(28, 326), Size = new Size(420, 132), BackColor = Card };
            options.Controls.Add(new Label { Text = "Name", Location = new Point(18, 20), AutoSize = true });
            _name = new TextBox { Location = new Point(100, 16), Width = 300, BorderStyle = BorderStyle.FixedSingle };
            options.Controls.Add(_name);
            _turn = new CheckBox { Text = "Turn around 180° (if it faces backwards)", Location = new Point(18, 56), AutoSize = true };
            _turn.CheckedChanged += (s, e) =>
            {
                if (_source != null && !_busy)
                {
                    StartConvert(_source);
                }
            };
            options.Controls.Add(_turn);
            options.Controls.Add(new Label { Text = "Rigged humanoids (Mixamo, VRoid, Unreal, Unity, Blender) come out best.\nOther models are rigged by proximity.", ForeColor = Muted, Location = new Point(18, 86), AutoSize = true, Font = new Font("Segoe UI", 8.5f) });
            Controls.Add(options);

            var paths = new RoundedPanel { Location = new Point(28, 474), Size = new Size(420, 120), BackColor = Card };
            paths.Controls.Add(new Label { Text = "Game skeleton", Font = new Font("Segoe UI Semibold", 9.75f), Location = new Point(18, 14), AutoSize = true });
            _templateLabel = new Label { Location = new Point(18, 36), Size = new Size(384, 34), ForeColor = Muted, Font = new Font("Segoe UI", 8.5f) };
            paths.Controls.Add(_templateLabel);
            _templateLink = new LinkLabel { Text = "Choose game folder…", Location = new Point(18, 70), AutoSize = true, LinkColor = Accent };
            _templateLink.LinkClicked += (s, e) => ChooseGameFolder();
            paths.Controls.Add(_templateLink);
            _outputLabel = new Label { Location = new Point(18, 94), Size = new Size(384, 20), ForeColor = Muted, Font = new Font("Segoe UI", 8.5f) };
            paths.Controls.Add(_outputLabel);
            Controls.Add(paths);

            _openFolder = new Button { Text = "Open models folder", Location = new Point(28, 610), Size = new Size(200, 36), FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 9.75f) };
            _openFolder.FlatAppearance.BorderSize = 0;
            _openFolder.Click += (s, e) => OpenFolder(_outputDir);
            Controls.Add(_openFolder);
            _again = new Button { Text = "Convert another", Location = new Point(240, 610), Size = new Size(208, 36), FlatStyle = FlatStyle.Flat, BackColor = Card };
            _again.FlatAppearance.BorderColor = Color.FromArgb(210, 210, 215);
            _again.Click += (s, e) => Browse();
            Controls.Add(_again);

            // Right column.
            var right = new RoundedPanel { Location = new Point(468, 100), Size = new Size(544, 546), BackColor = Card, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            _status = new Label { Text = "Waiting for a model", Font = new Font("Segoe UI Semibold", 11f), Location = new Point(18, 14), Size = new Size(508, 24), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            right.Controls.Add(_status);
            _preview = new PictureBox { Location = new Point(18, 44), Size = new Size(508, 330), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(250, 250, 252), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            right.Controls.Add(_preview);
            _log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Card, ForeColor = Muted, Location = new Point(18, 384), Size = new Size(508, 146), Font = new Font("Segoe UI", 8.75f), Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            right.Controls.Add(_log);
            Controls.Add(right);

            FindTemplate();
            if (initialFile != null)
            {
                Shown += (s, e) => StartConvert(initialFile);
            }
        }

        // ───────────────────────────── template / folders ─────────────────────────────

        private void FindTemplate()
        {
            _templatePath = null;
            _outputDir = null;
            var gameDirs = new List<string>();
            if (!string.IsNullOrEmpty(_settings.GameDir))
            {
                gameDirs.Add(_settings.GameDir);
            }
            // Shipped inside the game folder (GTWM/tools): walk up from the exe.
            for (string d = Path.GetDirectoryName(Application.ExecutablePath); d != null; d = Path.GetDirectoryName(d))
            {
                gameDirs.Add(d);
            }
            foreach (string dir in gameDirs.Distinct())
            {
                string gtwm = Path.Combine(dir, "GTWM");
                if (!Directory.Exists(gtwm))
                {
                    continue;
                }
                _outputDir = Path.Combine(gtwm, "models");
                string t = Path.Combine(gtwm, "templates", "character_template.glb");
                if (File.Exists(t))
                {
                    _templatePath = t;
                    break;
                }
            }
            if (_templatePath != null)
            {
                _templateLabel.Text = "Found: " + _templatePath;
                _templateLabel.ForeColor = Muted;
            }
            else if (_outputDir != null)
            {
                _templateLabel.Text = "Not created yet. Start the game with GTWM and load into a level once; it's made automatically.";
                _templateLabel.ForeColor = Bad;
            }
            else
            {
                _templateLabel.Text = "Game not found. Choose the folder that contains Get To Work.exe.";
                _templateLabel.ForeColor = Bad;
            }
            _outputLabel.Text = _outputDir != null ? "Saves to: " + _outputDir : "Saves next to the source model.";
            _openFolder.Enabled = _outputDir != null;
        }

        private void ChooseGameFolder()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Choose the Get To Work game folder (the one with Get To Work.exe)" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }
                _settings.GameDir = dlg.SelectedPath;
                _settings.Save();
                FindTemplate();
            }
        }

        // ───────────────────────────── converting ─────────────────────────────

        private void Browse()
        {
            if (_busy)
            {
                return;
            }
            using (var dlg = new OpenFileDialog
            {
                Title = "Choose a 3D model",
                Filter = "3D models|*.fbx;*.obj;*.glb;*.gltf;*.dae;*.blend;*.pmx;*.3ds;*.ply;*.stl;*.x;*.ms3d;*.md5mesh;*.smd;*.lwo;*.ase;*.off;*.vrm|All files|*.*",
            })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    StartConvert(dlg.FileName);
                }
            }
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) && !_busy ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                StartConvert(files[0]);
            }
        }

        private void StartConvert(string file)
        {
            if (_busy)
            {
                return;
            }
            FindTemplate();
            if (_templatePath == null)
            {
                SetStatus("The game skeleton is missing (see bottom left).", Bad);
                return;
            }
            if (_source != file)
            {
                _name.Text = Clean(Path.GetFileNameWithoutExtension(file));
            }
            _source = file;
            string name = Clean(_name.Text);
            if (name.Length == 0)
            {
                name = "model";
            }
            var options = new ConvertOptions { TemplatePath = _templatePath, Turn180 = _turn.Checked, Name = name };
            string outDir = _outputDir ?? Path.GetDirectoryName(file);
            _busy = true;
            _dropTitle.Text = Path.GetFileName(file);
            SetStatus("Converting…", Muted);
            _log.Text = "";
            Task.Run(() =>
            {
                try
                {
                    ConvertResult r = Converter.Convert(file, options);
                    Directory.CreateDirectory(outDir);
                    string outPath = Path.Combine(outDir, name + ".glb");
                    File.WriteAllBytes(outPath, r.Glb);
                    Bitmap preview = Preview.Render(r, 1016, 660);
                    BeginInvoke((Action)(() =>
                    {
                        _preview.Image?.Dispose();
                        _preview.Image = preview;
                        _log.Lines = r.Log.Prepend(r.Mode + ".").ToArray();
                        SetStatus($"Saved {name}.glb. In game, press F9 and pick it.", Good);
                        _busy = false;
                    }));
                }
                catch (Exception ex)
                {
                    BeginInvoke((Action)(() =>
                    {
                        SetStatus("Couldn't convert this model.", Bad);
                        _log.Text = ex.Message + (ex is InvalidDataException ? "" : Environment.NewLine + ex);
                        _busy = false;
                    }));
                }
            });
        }

        private void SetStatus(string text, Color color)
        {
            _status.Text = text;
            _status.ForeColor = color;
        }

        private static string Clean(string s) => new string((s ?? "").Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_').ToArray()).Trim();

        private static void OpenFolder(string dir)
        {
            if (dir == null)
            {
                return;
            }
            Directory.CreateDirectory(dir);
            Process.Start("explorer.exe", "\"" + dir + "\"");
        }
    }

    /// <summary>Remembers the game folder between runs.</summary>
    internal sealed class Settings
    {
        private static readonly string PathOnDisk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GTWM", "converter.cfg");

        public string GameDir;

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                foreach (string line in File.ReadAllLines(PathOnDisk))
                {
                    if (line.StartsWith("game_dir="))
                    {
                        s.GameDir = line.Substring(9).Trim();
                    }
                }
            }
            catch
            {
            }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathOnDisk));
                File.WriteAllText(PathOnDisk, "game_dir=" + GameDir + Environment.NewLine);
            }
            catch
            {
            }
        }
    }

    internal sealed class RoundedPanel : Panel
    {
        public bool Dashed { get; set; }

        public RoundedPanel()
        {
            DoubleBuffered = true;
            Padding = new Padding(2);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? SystemColors.Control);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = Round(new Rectangle(0, 0, Width - 1, Height - 1), 14))
            using (var fill = new SolidBrush(BackColor))
            using (var pen = new Pen(Dashed ? Color.FromArgb(10, 132, 255) : Color.FromArgb(225, 225, 230), Dashed ? 1.6f : 1f) { DashStyle = Dashed ? DashStyle.Dash : DashStyle.Solid })
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(pen, path);
            }
        }

        private static GraphicsPath Round(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>Software-rendered front and side views: the converted model over a ghost of the game character.</summary>
    internal static class Preview
    {
        public static Bitmap Render(ConvertResult r, int width, int height)
        {
            var bmp = new Bitmap(width, height);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.FromArgb(250, 250, 252));
                Template t = r.Template;
                Vector3 min = Vector3.Min(t.Min, r.Vertices.Aggregate(new Vector3(float.MaxValue), Vector3.Min));
                Vector3 max = Vector3.Max(t.Max, r.Vertices.Aggregate(new Vector3(float.MinValue), Vector3.Max));
                float extent = Math.Max(max.Y - min.Y, Math.Max(max.X - min.X, max.Z - min.Z)) * 1.1f;
                int half = width / 2;
                using (var label = new SolidBrush(Color.FromArgb(140, 140, 145)))
                using (var font = new Font("Segoe UI", 11f))
                {
                    DrawView(g, r, new Rectangle(0, 30, half, height - 40), min, max, extent, front: true);
                    DrawView(g, r, new Rectangle(half, 30, half, height - 40), min, max, extent, front: false);
                    g.DrawString("Front", font, label, 16, 8);
                    g.DrawString("Side", font, label, half + 16, 8);
                }
            }
            return bmp;
        }

        private static void DrawView(Graphics g, ConvertResult r, Rectangle area, Vector3 min, Vector3 max, float extent, bool front)
        {
            float scale = area.Height / extent;
            Vector3 centre = (min + max) / 2f;
            PointF Project(Vector3 v)
            {
                float h = front ? v.X - centre.X : v.Z - centre.Z;
                return new PointF(area.X + area.Width / 2f + h * scale, area.Y + area.Height / 2f - (v.Y - centre.Y) * scale);
            }
            float Depth(Vector3 v) => front ? v.Z : -v.X;
            Vector3 light = Vector3.Normalize(front ? new Vector3(0.4f, 0.6f, 1f) : new Vector3(-1f, 0.6f, 0.4f));

            Template t = r.Template;
            using (var ghost = new SolidBrush(Color.FromArgb(28, 0, 0, 0)))
            {
                for (int i = 0; i + 2 < t.BodyTriangles.Count; i += 3)
                {
                    g.FillPolygon(ghost, new[] { Project(t.BodyVertices[t.BodyTriangles[i]]), Project(t.BodyVertices[t.BodyTriangles[i + 1]]), Project(t.BodyVertices[t.BodyTriangles[i + 2]]) });
                }
            }

            var tris = new List<(float depth, PointF[] pts, float shade)>(r.Triangles.Count / 3);
            for (int i = 0; i + 2 < r.Triangles.Count; i += 3)
            {
                Vector3 a = r.Vertices[r.Triangles[i]], b = r.Vertices[r.Triangles[i + 1]], c = r.Vertices[r.Triangles[i + 2]];
                Vector3 n = Vector3.Cross(b - a, c - a);
                if (n.LengthSquared() < 1e-14f)
                {
                    continue;
                }
                n = Vector3.Normalize(n);
                float shade = 0.35f + 0.65f * Math.Abs(Vector3.Dot(n, light));
                tris.Add(((Depth(a) + Depth(b) + Depth(c)) / 3f, new[] { Project(a), Project(b), Project(c) }, shade));
            }
            foreach (var tri in tris.OrderBy(x => x.depth))
            {
                int v = (int)(255 * tri.shade);
                using (var brush = new SolidBrush(Color.FromArgb(255, (int)(v * 0.35f), (int)(v * 0.62f), v)))
                {
                    g.FillPolygon(brush, tri.pts);
                }
            }

            using (var bone = new Pen(Color.FromArgb(200, 255, 149, 0), 2f))
            using (var dot = new SolidBrush(Color.FromArgb(230, 255, 149, 0)))
            {
                foreach (int j in t.Joints)
                {
                    int p = t.Parent[j];
                    if (p >= 0 && Array.BinarySearch(t.Joints, p) >= 0)
                    {
                        g.DrawLine(bone, Project(t.Position(p)), Project(t.Position(j)));
                    }
                    PointF q = Project(t.Position(j));
                    g.FillEllipse(dot, q.X - 2.5f, q.Y - 2.5f, 5f, 5f);
                }
            }
        }
    }
}
