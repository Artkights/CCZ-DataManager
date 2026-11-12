using System.Text;
using TransformManager.App;

namespace TierManager.App;

public sealed class TierMarker
{
    public int Version { get; set; } = 1;
    public int Kind { get; set; }
    public int TierBase { get; set; }
    public ushort CodeLength { get; set; }
    public uint CodeVa { get; set; }
    public uint TableVa { get; set; }
    public int DisplayIndexOffset { get; set; }
    public uint DisplayPointerVa { get; set; }
    public uint OriginalTierFuncVa { get; set; }
    public uint OriginalTableVa { get; set; }
    public uint CaveVa { get; set; }
    public uint CaveLength { get; set; }
    public uint MaskTarget { get; set; }
    public bool InlineMask { get; set; }
    public uint CorrectionTarget { get; set; }
    public byte[] MaskSequence { get; set; } = [];
    public List<uint> CallSites { get; } = [];
    public List<byte> Thresholds { get; } = [];
    public List<string> Labels { get; } = [];
}

public static class MarkerCodec
{
    private static readonly byte[] Magic = "TIER1"u8.ToArray();

    public static byte[] Build(TierMarker marker)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write((byte)marker.Version);
        writer.Write((byte)marker.Kind);
        writer.Write((byte)marker.TierBase);
        writer.Write((byte)marker.Thresholds.Count);
        writer.Write(marker.CodeVa);
        writer.Write(marker.CodeLength);
        writer.Write(marker.TableVa);
        writer.Write((byte)marker.DisplayIndexOffset);
        writer.Write(marker.DisplayPointerVa);
        writer.Write(marker.OriginalTierFuncVa);
        writer.Write(marker.OriginalTableVa);
        writer.Write(marker.CaveVa);
        writer.Write(marker.CaveLength);
        writer.Write(marker.MaskTarget);
        writer.Write(marker.InlineMask ? (byte)1 : (byte)0);
        writer.Write(marker.CorrectionTarget);
        writer.Write((ushort)marker.MaskSequence.Length);
        writer.Write(marker.MaskSequence);
        writer.Write((byte)marker.CallSites.Count);
        foreach (var site in marker.CallSites) writer.Write(site);
        foreach (var threshold in marker.Thresholds) writer.Write(threshold);
        writer.Write((byte)marker.Labels.Count);
        var gbk = Encoding.GetEncoding("GBK");
        foreach (var label in marker.Labels)
        {
            var bytes = gbk.GetBytes(label);
            writer.Write((byte)bytes.Length);
            writer.Write(bytes);
        }
        writer.Flush();
        return stream.ToArray();
    }
}

public sealed class MarkerReader
{
    private readonly PeFile _pe;
    private readonly uint _markerVa;

    public MarkerReader(PeFile pe, uint markerVa)
    {
        _pe = pe;
        _markerVa = markerVa;
    }

    public bool TryRead(out TierMarker marker)
    {
        marker = new TierMarker();
        var buffer = _pe.ReadBytes(_markerVa, 1024);
        if (buffer.Length < 16 || !buffer.AsSpan(0, 5).SequenceEqual("TIER1"u8)) return false;
        try
        {
            using var stream = new MemoryStream(buffer);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            reader.ReadBytes(5);
            marker.Version = reader.ReadByte();
            marker.Kind = reader.ReadByte();
            marker.TierBase = reader.ReadByte();
            int thresholdCount = reader.ReadByte();
            marker.CodeVa = reader.ReadUInt32();
            marker.CodeLength = reader.ReadUInt16();
            marker.TableVa = reader.ReadUInt32();
            marker.DisplayIndexOffset = reader.ReadByte();
            marker.DisplayPointerVa = reader.ReadUInt32();
            marker.OriginalTierFuncVa = reader.ReadUInt32();
            marker.OriginalTableVa = reader.ReadUInt32();
            marker.CaveVa = reader.ReadUInt32();
            marker.CaveLength = reader.ReadUInt32();
            marker.MaskTarget = reader.ReadUInt32();
            marker.InlineMask = reader.ReadByte() != 0;
            marker.CorrectionTarget = reader.ReadUInt32();
            int maskLen = reader.ReadUInt16();
            marker.MaskSequence = reader.ReadBytes(maskLen);
            int callCount = reader.ReadByte();
            for (int i = 0; i < callCount; i++) marker.CallSites.Add(reader.ReadUInt32());
            for (int i = 0; i < thresholdCount; i++) marker.Thresholds.Add(reader.ReadByte());
            int labelCount = reader.ReadByte();
            var gbk = Encoding.GetEncoding("GBK");
            for (int i = 0; i < labelCount; i++)
            {
                int length = reader.ReadByte();
                marker.Labels.Add(gbk.GetString(reader.ReadBytes(length)));
            }
            return marker.Version == 1;
        }
        catch
        {
            return false;
        }
    }
}
