using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace RevitEnscapeCloud.Launcher
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    public class MainForm : Form
    {
        private Label lblTitle;
        private Label lblSubtitle;
        private Label lblStatus;
        private Button btnLaunchViewer;
        private Button btnInstallRevit;
        private Button btnOpenFolder;
        private Button btnUninstall;
        private Panel cardStatus;
        private Label lblRevitDetect;

        public MainForm()
        {
            InitializeComponent();
            CheckRevitStatus();
        }

        private void InitializeComponent()
        {
            this.Text = "EnscapeCloud RT — Visualizador & Gestor Add-in Revit 2026";
            this.Size = new Size(540, 480);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.BackColor = Color.FromArgb(17, 24, 39); // Gray 900
            this.ForeColor = Color.White;

            lblTitle = new Label
            {
                Text = "☁ EnscapeCloud RT",
                Font = new Font("Segoe UI", 18, FontStyle.Bold),
                ForeColor = Color.FromArgb(52, 211, 153), // Emerald 400
                Location = new Point(24, 18),
                AutoSize = true
            };

            lblSubtitle = new Label
            {
                Text = "Visualizador Fotorrealista de Nubes de Puntos (.LAS / .RCS) & Plugin Revit 2026",
                Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
                ForeColor = Color.FromArgb(156, 163, 175), // Gray 400
                Location = new Point(26, 52),
                Size = new Size(480, 22)
            };

            cardStatus = new Panel
            {
                Location = new Point(24, 82),
                Size = new Size(476, 75),
                BackColor = Color.FromArgb(31, 41, 55), // Gray 800
                BorderStyle = BorderStyle.None
            };

            lblRevitDetect = new Label
            {
                Location = new Point(16, 12),
                Size = new Size(440, 22),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(110, 231, 183)
            };

            lblStatus = new Label
            {
                Location = new Point(16, 38),
                Size = new Size(440, 26),
                Font = new Font("Segoe UI", 9.0f, FontStyle.Regular),
                ForeColor = Color.FromArgb(209, 213, 219)
            };

            cardStatus.Controls.Add(lblRevitDetect);
            cardStatus.Controls.Add(lblStatus);

            btnLaunchViewer = CreateButton("🌐 Iniciar Visualizador 3D Standalone (Cargar .LAS / .RCS)", new Point(24, 175), Color.FromArgb(5, 150, 105), Color.White);
            btnLaunchViewer.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            btnLaunchViewer.Height = 50;
            btnLaunchViewer.Click += (s, e) => LaunchStandaloneViewer();

            btnInstallRevit = CreateButton("⚡ Instalar / Actualizar Add-in en Revit 2026", new Point(24, 238), Color.FromArgb(37, 99, 235), Color.White);
            btnInstallRevit.Font = new Font("Segoe UI", 10.0f, FontStyle.Bold);
            btnInstallRevit.Height = 44;
            btnInstallRevit.Click += (s, e) => InstallAddin();

            btnOpenFolder = CreateButton("📂 Abrir Carpeta de Add-ins de Revit", new Point(24, 292), Color.FromArgb(55, 65, 81), Color.FromArgb(229, 231, 235));
            btnOpenFolder.Height = 38;
            btnOpenFolder.Click += (s, e) => OpenAddinsFolder();

            btnUninstall = CreateButton("✕ Desinstalar Plugin de Revit 2026", new Point(24, 340), Color.FromArgb(31, 41, 55), Color.FromArgb(248, 113, 113));
            btnUninstall.Height = 38;
            btnUninstall.Click += (s, e) => UninstallAddin();

            var lblFooter = new Label
            {
                Text = "Desarrollado con Three.js, WebGL2 & Autodesk Revit API 2026 | DinamicLux",
                Font = new Font("Segoe UI", 8.0f),
                ForeColor = Color.FromArgb(107, 114, 128),
                Location = new Point(24, 400),
                AutoSize = true
            };

            this.Controls.Add(lblTitle);
            this.Controls.Add(lblSubtitle);
            this.Controls.Add(cardStatus);
            this.Controls.Add(btnLaunchViewer);
            this.Controls.Add(btnInstallRevit);
            this.Controls.Add(btnOpenFolder);
            this.Controls.Add(btnUninstall);
            this.Controls.Add(lblFooter);
        }

        private Button CreateButton(string text, Point loc, Color backCol, Color textCol)
        {
            var btn = new Button
            {
                Text = text,
                Location = loc,
                Size = new Size(476, 40),
                FlatStyle = FlatStyle.Flat,
                BackColor = backCol,
                ForeColor = textCol,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Regular)
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }

        private void CheckRevitStatus()
        {
            string revitProg = @"C:\Program Files\Autodesk\Revit 2026\Revit.exe";
            bool hasRevit = File.Exists(revitProg);

            string userAddins = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Autodesk\Revit\Addins\2026\RevitEnscapeCloud.addin");
            bool isInstalled = File.Exists(userAddins);

            if (hasRevit)
            {
                lblRevitDetect.Text = "✓ Autodesk Revit 2026 detectado en el sistema.";
                lblRevitDetect.ForeColor = Color.FromArgb(52, 211, 153);
            }
            else
            {
                lblRevitDetect.Text = "ℹ Autodesk Revit 2026 no encontrado en ruta estándar.";
                lblRevitDetect.ForeColor = Color.FromArgb(251, 191, 36);
            }

            if (isInstalled)
            {
                lblStatus.Text = "Estado Add-in: INSTALADO y listo en Revit 2026.";
                lblStatus.ForeColor = Color.FromArgb(110, 231, 183);
            }
            else
            {
                lblStatus.Text = "Estado Add-in: No instalado en Revit 2026. Pulsa el botón para instalarlo.";
                lblStatus.ForeColor = Color.FromArgb(209, 213, 219);
            }
        }

        private string FindHtmlViewerPath()
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates = new string[]
            {
                Path.Combine(appDir, @"WebViewer\index.html"),
                Path.Combine(appDir, @"src\WebViewer\index.html"),
                Path.Combine(appDir, @"dist\WebViewer\index.html"),
                @"C:\Users\durre\.gemini\antigravity\scratch\RevitEnscapeCloud\src\WebViewer\index.html",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Autodesk\Revit\Addins\2026\WebViewer\index.html")
            };

            foreach (var c in candidates)
            {
                if (File.Exists(c)) return Path.GetFullPath(c);
            }
            return null;
        }

        private void LaunchStandaloneViewer()
        {
            string htmlPath = FindHtmlViewerPath();
            if (string.IsNullOrEmpty(htmlPath) || !File.Exists(htmlPath))
            {
                MessageBox.Show("No se encontró el archivo WebViewer\\index.html.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                string fileUri = "file:///" + htmlPath.Replace('\\', '/');
                string edgePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
                if (!File.Exists(edgePath))
                {
                    edgePath = @"C:\Program Files\Microsoft\Edge\Application\msedge.exe";
                }

                if (File.Exists(edgePath))
                {
                    // Launch as standalone window with GPU acceleration enabled
                    Process.Start(edgePath, string.Format("--app=\"{0}\" --enable-gpu-rasterization --ignore-gpu-blocklist", fileUri));
                }
                else
                {
                    Process.Start(new ProcessStartInfo(fileUri) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error al abrir el visualizador:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void InstallAddin()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string userAddinsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Autodesk\Revit\Addins\2026");
                Directory.CreateDirectory(userAddinsDir);

                // Find latest compiled DLL
                string dllPath = null;
                string[] possibleDlls = new string[]
                {
                    Path.Combine(baseDir, "RevitEnscapeCloud.dll"),
                    Path.Combine(baseDir, @"dist\RevitEnscapeCloud.dll"),
                    @"C:\Users\durre\.gemini\antigravity\scratch\RevitEnscapeCloud\dist\RevitEnscapeCloud.dll"
                };

                foreach (var p in possibleDlls)
                {
                    if (File.Exists(p)) { dllPath = p; break; }
                }

                if (dllPath == null)
                {
                    // Search latest dist_*
                    string projRoot = @"C:\Users\durre\.gemini\antigravity\scratch\RevitEnscapeCloud";
                    if (Directory.Exists(projRoot))
                    {
                        var dirs = Directory.GetDirectories(projRoot, "dist*");
                        Array.Sort(dirs);
                        if (dirs.Length > 0)
                        {
                            string candidate = Path.Combine(dirs[dirs.Length - 1], "RevitEnscapeCloud.dll");
                            if (File.Exists(candidate)) dllPath = candidate;
                        }
                    }
                }

                if (string.IsNullOrEmpty(dllPath) || !File.Exists(dllPath))
                {
                    MessageBox.Show("No se encontró RevitEnscapeCloud.dll para instalar.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                string targetDll = Path.Combine(userAddinsDir, "RevitEnscapeCloud.dll");
                try
                {
                    File.Copy(dllPath, targetDll, true);
                }
                catch
                {
                    // If Revit is currently locking the target DLL, use source path in assembly
                    targetDll = dllPath;
                }

                // Copy WebViewer files to user addins directory
                string htmlPath = FindHtmlViewerPath();
                if (!string.IsNullOrEmpty(htmlPath) && File.Exists(htmlPath))
                {
                    string srcWvDir = Path.GetDirectoryName(htmlPath);
                    string targetWvDir = Path.Combine(userAddinsDir, "WebViewer");
                    Directory.CreateDirectory(targetWvDir);
                    foreach (var f in Directory.GetFiles(srcWvDir))
                    {
                        File.Copy(f, Path.Combine(targetWvDir, Path.GetFileName(f)), true);
                    }
                }

                // Write .addin manifest
                string addinXml = string.Format(@"<?xml version=""1.0"" encoding=""utf-8"" standalone=""no""?>
<RevitAddIns>
  <AddIn Type=""Application"">
    <Name>EnscapeCloud RT — Renderizador + Nube de Puntos (Revit 2026)</Name>
    <Assembly>{0}</Assembly>
    <AddInId>8D94A1E0-5F3C-4B98-9D42-7A1E2B3C4D5E</AddInId>
    <FullClassName>RevitEnscapeCloud.App</FullClassName>
    <VendorId>ENSCAPECLOUD</VendorId>
    <VendorDescription>EnscapeCloud Real-Time Renderer with Point Cloud Density Control for Revit 2026</VendorDescription>
  </AddIn>
</RevitAddIns>", targetDll);

                string manifestPath = Path.Combine(userAddinsDir, "RevitEnscapeCloud.addin");
                File.WriteAllText(manifestPath, addinXml, System.Text.Encoding.UTF8);

                CheckRevitStatus();
                MessageBox.Show("✓ ¡Add-in instalado y registrado exitosamente en Revit 2026!\n\nRuta: " + manifestPath,
                                "Instalación Exitosa", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error al instalar el add-in:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OpenAddinsFolder()
        {
            string userAddinsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Autodesk\Revit\Addins\2026");
            Directory.CreateDirectory(userAddinsDir);
            Process.Start("explorer.exe", userAddinsDir);
        }

        private void UninstallAddin()
        {
            try
            {
                string manifestPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Autodesk\Revit\Addins\2026\RevitEnscapeCloud.addin");
                if (File.Exists(manifestPath))
                {
                    File.Delete(manifestPath);
                    CheckRevitStatus();
                    MessageBox.Show("El add-in ha sido desinstalado de Revit 2026.", "Desinstalación", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show("El archivo de manifiesto del add-in no estaba instalado.", "Información", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error al desinstalar:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
