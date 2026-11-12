using System.Windows.Forms;

namespace RangeManager.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 &&
            (args[0].Equals("--self-test", StringComparison.OrdinalIgnoreCase) ||
             args[0].Equals("--self-test-all", StringComparison.OrdinalIgnoreCase) ||
             args[0].Equals("--launch-smoke", StringComparison.OrdinalIgnoreCase)))
        {
            return SelfTest.Run(args);
        }

        ApplicationConfiguration.Initialize();

        if (args.Length > 0 && args[0].Equals("--verify-palette", StringComparison.OrdinalIgnoreCase))
            return PaletteVerification.Run(args.Length > 1 ? args[1] : "palette-verification.json");

        if (args.Length > 0 && args[0].Equals("--verify-v2", StringComparison.OrdinalIgnoreCase))
            return RangeVerification.Run(args);

        if (args.Length >= 3 && args[0].Equals("--verify-ui-v2", StringComparison.OrdinalIgnoreCase))
            return RangeUiVerification.Run(args[1], args[2]);

        if (args.Length >= 4 && args[0].Equals("--ui-shot", StringComparison.OrdinalIgnoreCase))
            return UiShot(args[1], int.TryParse(args[2], out var page) ? page : 0, args[3],
                args.Length > 4 && int.TryParse(args[4], out int width) ? width : 1500,
                args.Length > 5 && int.TryParse(args[5], out int height) ? height : 900,
                args.Length > 6 && int.TryParse(args[6], out int id) ? id : null,
                args.Length > 7 && float.TryParse(args[7], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float fontScale) ? fontScale : 1f);

        if (args.Length >= 3 && args[0].Equals("--ui-edit-test", StringComparison.OrdinalIgnoreCase))
            return UiEditTest(args[1], args[2]);

        Application.Run(new MainForm());
        return 0;
    }

    /// <summary>
    /// Opens an independent EXE/E5 copy and verifies the real two-page editor.
    /// </summary>
    private static int UiEditTest(string root, string output)
    {
        string temp = Path.Combine(Path.GetTempPath(), "range-ui-edit-" + Guid.NewGuid().ToString("N"));
        var log = new System.Text.StringBuilder();
        bool ok = false;
        static string Hash(string path) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        string realExe = Path.Combine(root, "Ekd5.exe");
        string beforeExe = Hash(realExe);
        var sourceHashes = new Dictionary<string, string>();
        try
        {
            Directory.CreateDirectory(temp);
            File.Copy(realExe, Path.Combine(temp, "Ekd5.exe"));
            Directory.CreateDirectory(Path.Combine(temp, "E5"));
            foreach (string name in new[] { "Hitarea.e5", "Effarea.e5", "Spalet.e5" })
            {
                string source = Path.Combine(root, "E5", name);
                if (!File.Exists(source)) source = Path.Combine(root, name);
                if (!File.Exists(source)) continue;
                sourceHashes[source] = Hash(source);
                File.Copy(source, Path.Combine(temp, "E5", name));
            }

            using var form = new MainForm(openDefaultBase: false) { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
            form.OpenBaseForTest(temp);
            form.Show();
            Application.DoEvents();
            bool applied = form.RunShapeEditSmoke(1, "1,0; 2,0; 3,0; 4,0");
            bool saved = form.SaveForTest();
            string debug = form.LastEditDebug;
            bool tabsOk = form.TabsForTest.TabPages.Count == 3 && form.TabsForTest.TabPages[0].Text == "攻击范围" &&
                form.TabsForTest.TabPages[1].Text == "穿透范围" && form.TabsForTest.TabPages[2].Text == "拓展范围";
            form.CloseForTest();
            Application.DoEvents();

            using var session = new RangeEditSession(temp);
            var shape = session.Get(new RangeKey(RangeKind.Attack, 1));
            var actualCells = shape.Definition.Groups[0].Generators;
            var expected = new List<CellOffset> { new(1, 0), new(2, 0), new(3, 0), new(4, 0) };
            bool cellsOk = ShapeGeometry.ExpandMirror(actualCells).ToHashSet().SetEquals(ShapeGeometry.ExpandMirror(expected));
            string afterExe = Hash(realExe);
            bool realUntouched = beforeExe == afterExe && sourceHashes.All(pair => Hash(pair.Key) == pair.Value);
            ok = applied && saved && cellsOk && tabsOk && realUntouched;
            log.AppendLine($"applied={applied} saved={saved} threeTabs={tabsOk} cells={shape.Definition.Groups[0].Text}");
            log.AppendLine($"realBaseUntouched={realUntouched} exe={afterExe[..12]}…");
            log.AppendLine("debug " + debug);
            log.AppendLine(ok ? "UI_EDIT_TEST_OK" : "UI_EDIT_TEST_FAILED");
        }
        catch (Exception ex)
        {
            log.AppendLine("EXCEPTION " + ex);
            log.AppendLine("UI_EDIT_TEST_FAILED");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, log.ToString(), new System.Text.UTF8Encoding(false));
        return ok ? 0 : 1;
    }

    private static int UiShot(string root, int page, string output, int width, int height, int? id, float fontScale)
    {
        try
        {
            using var form = new MainForm(openDefaultBase: false) { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
            form.Size = new Size(Math.Max(1100, width), Math.Max(650, height));
            using var font = new Font(form.Font.FontFamily, form.Font.SizeInPoints * Math.Clamp(fontScale, 0.75f, 2f));
            form.Font = font;
            form.OpenBaseForTest(root);
            form.Show();
            Application.DoEvents();
            form.SelectPageForTest(page);
            if (id.HasValue && page < 2)
                (page == 1 ? form.PierceEditorForTest : form.AttackEditorForTest).SelectRangeForTest(id.Value);
            Application.DoEvents();
            Thread.Sleep(250);
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
            var editor = page switch
            {
                1 => form.PierceEditorForTest,
                2 => null,
                _ => form.AttackEditorForTest
            };
            File.WriteAllText(output + ".metadata.json", System.Text.Json.JsonSerializer.Serialize(new
            {
                form.DeviceDpi, form.Width, form.Height, FontScale = fontScale,
                GroupCount = editor?.GroupSelectorForTest.Items.Count,
                GroupIndex = editor?.GroupSelectorForTest.SelectedIndex,
                GroupText = editor?.GroupSelectorForTest.Text,
                GridColumns = editor?.PreviewForTest.Columns,
                GridRows = editor?.PreviewForTest.Rows,
                GridPixels = editor?.PreviewForTest.CellPixelSize,
                CanvasSize = editor?.PreviewForTest.Size,
                ViewportSize = editor?.PreviewForTest.Parent?.ClientSize,
                ExtensionRows = page == 2 ? form.ExtensionEditorForTest.GridForTest.Rows.Count : 0
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            form.CloseForTest();
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(output + ".error.txt", ex.ToString());
            return 1;
        }
    }
}
