using System.Text;

namespace ImageManager.Core;

public sealed class StrategyRowService : IStrategyRowService
{
    private const int DataOffset = 45572;
    private const int RowSize = 97;
    private const int RowCount = 144;
    private const int NameLength = 11;
    private const int IconOffsetInRow = 16;
    private const int MeffOffset = 668000;
    private const int McallOffset = 668144;

    static StrategyRowService() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public IReadOnlyList<StrategyRow> Read(ProjectProfile project)
    {
        var dataPath = project.TryGetResource("Data.e5") ?? throw new FileNotFoundException("缺少 Data.e5。");
        var exePath = project.TryGetResource("Ekd5.exe") ?? throw new FileNotFoundException("缺少 Ekd5.exe。");
        var data = File.ReadAllBytes(dataPath);
        var executable = File.ReadAllBytes(exePath);
        if (data.Length < DataOffset + RowCount * RowSize) throw new InvalidDataException("Data.e5 策略表长度不足。");
        if (executable.Length < Math.Max(MeffOffset, McallOffset) + RowCount) throw new InvalidDataException("Ekd5.exe 策略动画表长度不足。");
        var encoding = Encoding.GetEncoding(936);
        var rows = new StrategyRow[RowCount];
        for (var index = 0; index < RowCount; index++)
        {
            var rowOffset = DataOffset + index * RowSize;
            var nameBytes = data.AsSpan(rowOffset, NameLength);
            var terminator = nameBytes.IndexOf((byte)0);
            if (terminator < 0) terminator = nameBytes.Length;
            var name = encoding.GetString(nameBytes[..terminator]).Trim();
            rows[index] = new StrategyRow(index, name, data[rowOffset + IconOffsetInRow],
                executable[MeffOffset + index], executable[McallOffset + index]);
        }
        return rows;
    }
}
