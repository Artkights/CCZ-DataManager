using System.Data;
using System.Globalization;
using System.Text;
using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Core.Profiles;

namespace DataManager.Tests;

/// <summary>Real physical write/reopen/undo coverage; source projects are never mutated.</summary>
internal static class FieldWritebackMatrixTests
{
    private sealed record Sample(string Directory, string Kind = "真实基底", string? Profile = null, string? Variant = null);
    private sealed record Entry(string Kind, string Sample, string Profile, string Variant, string Table, string Field,
        string Id, string Status, string Before, string After, string File, string Offset, string Size, string Detail);
    private sealed record Edit(int Row, TableFieldDefinition Field, object Value, Entry Report);
    private static readonly HashSet<string> PhysicalNames = BuildPhysicalNames();
    private static readonly Sample[] Samples =
    [
        new("洗隋"),
        new("加强版6.5未加密版"), new("曹操传6.6-V7.16 解压密码：66"), new("新改曹操傳6.6修正版"), new("清儿吕布传"),
        new("复刻版6.6山贼"), new("复刻版6.6山贼 国庆"), new("厚黑6.6测（7.9水杯版）"),
        new("烈焰同盟6.6x(新修版)黄线完结+红线5关"), new("曹操传6.6-V10.1 【解压密码：66】/复刻版6.6"),
        new("曹操传6.6-V7.16 解压密码：66", "合成旧山贼布局：沿用CSV回归的标准6.6文件+强制布局fixture", "ccz-6.6", "66-shanzhai-legacy")
    ];

    public static void Run(string basePath, bool only64 = false)
    {
        var repository = Directory.GetParent(Directory.GetParent(Path.GetFullPath(basePath))!.FullName)!.FullName;
        var baseRoot = Path.Combine(repository, "基底");
        var output = Path.Combine(repository, "data管理器", "验证", only64 ? "6.4适配-20261022" : "写回完善", "字段写回矩阵.csv");
        var entries = new List<Entry>();
        var failures = new List<string>();
        foreach (var sample in Samples.Where(sample => !only64 || sample.Directory == "洗隋"))
        {
            var source = Path.Combine(baseRoot, sample.Directory.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(source))
            {
                entries.Add(new(sample.Kind, sample.Directory, sample.Profile ?? "", sample.Variant ?? "", "*", "*", "", "样本缺失未测", "", "", "", "", "", source));
                failures.Add("缺少预期样本：" + sample.Directory);
                continue;
            }
            var root = CreatePhysicalCopy(source);
            try { RunSample(sample, source, root, entries, failures); }
            catch (Exception ex)
            {
                failures.Add(sample.Directory + "：" + ex.Message);
                entries.Add(new(sample.Kind, sample.Directory, "", "", "*", "*", "", "样本失败", "", "", "", "", "", ex.ToString()));
            }
            finally
            {
                WriteReport(output, entries);
                DeleteTemporaryCopy(root);
            }
        }
        WriteReport(output, entries);
        Console.WriteLine($"FIELD_WRITEBACK_MATRIX_REPORT={output}");
        Console.WriteLine($"通过字段边界={entries.Count(e => e.Status == "通过")}；明确未测/不适用={entries.Count(e => e.Status != "通过" && !e.Status.Contains("失败", StringComparison.Ordinal))}；失败={failures.Count}");
        if (failures.Count > 0) throw new InvalidOperationException("字段写回矩阵失败：" + string.Join("；", failures));
    }

    private static void RunSample(Sample sample, string source, string root, List<Entry> report, List<string> failures)
    {
        var sourceFiles = PhysicalFiles(source).ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        var initial = new ProjectWorkspace(); initial.Open(root, sample.Profile, sample.Variant);
        var profile = initial.ActiveProfile.ProfileId; var variant = initial.ActiveProfile.VariantId;
        foreach (var definition in initial.Definitions.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            if (!initial.Documents.TryGetValue(definition.Key, out var initialDocument) || !definition.IsWriteSupported)
            {
                var validation = new BinaryTableReader().Validate(root, definition);
                var status = !validation.FileExists ? "物理文件缺失未测"
                    : definition.Capability == TableCapability.Unavailable ? "缺消费证据未测" : !validation.HasEnoughBytes ? "长度不足未测"
                    : !definition.IsWriteSupported ? "只读不适用" : "加载失败";
                report.Add(NewEntry(sample, profile, variant, definition, "*", "", status, "", "", validation.Message));
                if (status == "加载失败") failures.Add(sample.Directory + "/" + definition.Key + " 无预期加载文档");
                continue;
            }
            var workspace = new ProjectWorkspace(); workspace.Open(root, profile, variant);
            var document = workspace.GetDocument(definition.Key);
            var before = PhysicalFiles(root).ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
            var allowed = new Dictionary<string, bool[]>(StringComparer.OrdinalIgnoreCase);
            var edits = new List<Edit>();
            try
            {
                if (document.Data.ExtendedProperties["TextDecodeWarnings"] is IEnumerable<string> warnings)
                    foreach (var warning in warnings)
                        report.Add(NewEntry(sample, profile, variant, definition, "*", "", "存量编码警告", "", "", warning));
                foreach (var rowIndex in new[] { 0, document.Data.Rows.Count - 1 }.Distinct())
                foreach (var field in definition.Fields.Where(f => f.Policy == FieldPolicy.Editable && f.Size > 0))
                {
                    var old = document.Data.Rows[rowIndex][field.Name];
                    if (!TryCandidate(workspace, document, rowIndex, field, out var value, out var detail))
                    {
                        report.Add(NewEntry(sample, profile, variant, definition, field.Name,
                            (definition.BeginId + rowIndex).ToString(CultureInfo.InvariantCulture), "受约束无替代值未测", Text(old), "", detail, field));
                        continue;
                    }
                    var rowOffset = checked((int)(definition.DataOffset + (long)rowIndex * definition.RowSize));
                    var codec = new RecordCodecRegistry().Get(definition.RecordCodecKey);
                    var fileName = Path.GetFileName(document.FilePath);
                    if (!allowed.TryGetValue(fileName, out var mask)) allowed[fileName] = mask = new bool[before[fileName].Length];
                    foreach (var patch in codec.EncodeChangedField(before[fileName].AsSpan(rowOffset, definition.RowSize), field, value))
                        Array.Fill(mask, true, checked(rowOffset + patch.Offset), patch.Bytes.Length);
                    var entry = NewEntry(sample, profile, variant, definition, field.Name,
                        (definition.BeginId + rowIndex).ToString(CultureInfo.InvariantCulture), "通过", Text(old), Text(value), "保存+新工作区复读+文件长度+授权patch外字节+一次Undo恢复", field);
                    edits.Add(new(rowIndex, field, value!, entry));
                    document.Data.Rows[rowIndex][field.Name] = value!;
                }
                if (edits.Count == 0) continue;
                var result = workspace.Save();
                if (!result.Saved || result.ChangedFields != edits.Count)
                    throw new InvalidOperationException($"整表保存失败/变更数量错误（期望{edits.Count}，实际{result.ChangedFields}）：" + string.Join("；", result.Messages));
                AssertFileChanges(root, before, allowed);
                var reopened = new ProjectWorkspace(); reopened.Open(root, profile, variant);
                var reread = reopened.GetDocument(definition.Key);
                foreach (var edit in edits)
                    if (Text(reread.Data.Rows[edit.Row][edit.Field.Name]) != Text(edit.Value))
                        throw new InvalidDataException($"新工作区复读值不一致：ID={definition.BeginId + edit.Row}/{edit.Field.Name}");
                var undo = reopened.UndoSaved();
                if (!undo.Success) throw new InvalidOperationException("一次Undo失败：" + undo.Message);
                foreach (var (name, bytes) in before)
                    if (!File.ReadAllBytes(ProjectFileLocator.Resolve(root, name)).AsSpan().SequenceEqual(bytes))
                        throw new InvalidDataException("Undo未恢复原始完整文件：" + name);
                report.AddRange(edits.Select(e => e.Report));
                Console.WriteLine($"矩阵 {sample.Directory} [{variant}] {definition.Key}：{edits.Count} 字段边界通过");
            }
            catch (Exception ex)
            {
                failures.Add(sample.Directory + "/" + definition.Key + "：" + ex.Message);
                if (edits.Count > 0) report.AddRange(edits.Select(e => e.Report with { Status = "失败", Detail = ex.Message }));
                else report.Add(NewEntry(sample, profile, variant, definition, "*", "", "失败", "", "", ex.Message));
                // Continue testing other tables from a proved copy of the same
                // original data, never from a partly failed/modified candidate.
                foreach (var (name, bytes) in before) File.WriteAllBytes(ProjectFileLocator.Resolve(root, name), bytes);
            }
        }
        foreach (var (name, bytes) in sourceFiles)
            if (!File.ReadAllBytes(ProjectFileLocator.Resolve(source, name)).AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException("验收误写源基底：" + name);
    }

    private static bool TryCandidate(ProjectWorkspace workspace, TableDocument document, int row, TableFieldDefinition field,
        out object? value, out string detail)
    {
        var old = document.Data.Rows[row][field.Name];
        var candidates = new List<object>();
        if (field.Kind == FieldKind.FixedString) candidates.AddRange(new object[] { "W", "X", "" });
        else if (field.Kind == FieldKind.RawBytes)
        {
            var bytes = Convert.FromHexString(Text(old)); bytes[0] ^= 1;
            candidates.Add(Convert.ToHexString(bytes));
        }
        else
        {
            var number = Convert.ToInt64(old, CultureInfo.InvariantCulture);
            if (document.Definition.Key == "shops" && field.ReferenceTable == "items")
            {
                candidates.Add(255);
                foreach (var key in new[] { "items-low", "items-high" }.Where(workspace.Documents.ContainsKey))
                    candidates.AddRange(workspace.GetDocument(key).Data.Rows.Cast<DataRow>().Select(r => (object)r["ID"]));
            }
            else if (document.Definition.Key == "shops" && field.ReferenceTable == "persons")
                candidates.AddRange(new object[] { 65535, 0, 1 });
            else if (document.Definition.RecordCodecKey == "item-66-15-v1" && field.Name == "宝物类型" && IsMarker(document, row))
                candidates.AddRange(new object[] { 2, 3 });
            else
            {
                if (number < field.Maximum) candidates.Add(number + 1);
                candidates.Add(field.Minimum);
                if (field.Minimum < field.Maximum) candidates.Add(field.Minimum + 1);
                candidates.Add(field.Maximum);
                candidates.AddRange(new object[] { 0, 1, 255, 65535 });
            }
        }
        var rejections = new List<string>();
        foreach (var candidate in candidates.Distinct())
        {
            if (Text(candidate) == Text(old)) continue;
            try
            {
                workspace.ValidateCellValue(document, row, field.Name, candidate);
                value = candidate; detail = ""; return true;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or OverflowException or FormatException)
            { if (rejections.Count < 4) rejections.Add(ex.Message); }
        }
        value = null; detail = string.Join("；", rejections); return false;
    }

    private static bool IsMarker(TableDocument document, int row)
    {
        var offset = checked((int)(document.Definition.DataOffset + (long)row * document.Definition.RowSize));
        return document.OriginalFileBytes[offset + 0x11] >= 26 && document.OriginalFileBytes[offset + 0x12] is 2 or 3;
    }

    private static void AssertFileChanges(string root, IReadOnlyDictionary<string, byte[]> before, IReadOnlyDictionary<string, bool[]> allowed)
    {
        foreach (var (name, bytes) in before)
        {
            var after = File.ReadAllBytes(ProjectFileLocator.Resolve(root, name));
            if (after.Length != bytes.Length) throw new InvalidDataException("文件长度改变：" + name);
            allowed.TryGetValue(name, out var mask);
            for (var i = 0; i < bytes.Length; i++)
                if (bytes[i] != after[i] && (mask == null || !mask[i]))
                    throw new InvalidDataException($"修改超出授权patch：{name}+0x{i:X}");
        }
    }

    private static IEnumerable<string> PhysicalFiles(string root)
        => Directory.EnumerateFiles(root).Where(path => PhysicalNames.Contains(Path.GetFileName(path))).ToArray();

    private static HashSet<string> BuildPhysicalNames()
    {
        var registry = new ProfileRegistry();
        return registry.Profiles.SelectMany(p => p.Variants.Select(v => registry.Resolve(p.Id, v.Id)))
            .SelectMany(p => p.Tables).Select(t => t.FileName).Append("Ekd5.exe").ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string CreatePhysicalCopy(string source)
    {
        var root = Path.Combine(Path.GetTempPath(), "DataManagerFieldMatrix_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (var path in PhysicalFiles(source)) File.Copy(path, Path.Combine(root, Path.GetFileName(path)));
        foreach (var relative in new[] { "System.ini", "RS/System.ini", "B形象指定器/6.6x形象指定器/System.ini", "B形象指定器/形象指定器6.5/System.ini" })
        {
            var sourcePath = Path.Combine(source, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(sourcePath)) continue;
            var target = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(sourcePath, target);
        }
        return root;
    }

    private static void DeleteTemporaryCopy(string root)
    {
        var resolved = Path.GetFullPath(root);
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("DataManagerFieldMatrix_", StringComparison.Ordinal))
            throw new InvalidOperationException("拒绝清理非本次矩阵临时副本。");
        Directory.Delete(resolved, true);
    }

    private static Entry NewEntry(Sample sample, string profile, string variant, TableDefinition table, string fieldName,
        string id, string status, string before, string after, string detail, TableFieldDefinition? field = null)
        => new(sample.Kind, sample.Directory, profile, variant, table.Key, fieldName, id, status, before, after,
            table.FileName, field == null ? "" : $"0x{table.DataOffset + (long)(int.Parse(id, CultureInfo.InvariantCulture) - table.BeginId) * table.RowSize + field.Offset:X}",
            field?.Size.ToString(CultureInfo.InvariantCulture) ?? "", detail);

    private static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    private static void WriteReport(string path, IReadOnlyList<Entry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = new StringBuilder("样本性质,样本,Profile,Variant,表,字段,ID,状态,原值,新值,物理文件,偏移,字段槽宽,验收或未测说明\r\n");
        foreach (var entry in entries)
            text.AppendLine(string.Join(',', new[] { entry.Kind, entry.Sample, entry.Profile, entry.Variant, entry.Table, entry.Field, entry.Id,
                entry.Status, entry.Before, entry.After, entry.File, entry.Offset, entry.Size, entry.Detail }.Select(v => "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")));
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
    }
}
