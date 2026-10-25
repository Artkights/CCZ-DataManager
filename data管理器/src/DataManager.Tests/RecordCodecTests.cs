using DataManager.Core.Binary;
using DataManager.Core.Models;

namespace DataManager.Tests;

internal static class RecordCodecTests
{
    public static void Run()
    {
        TextConstraintTests.Run();
        var byteField = new TableFieldDefinition("值", 0, 1, FieldKind.UInt8, FieldPolicy.Editable, 0, 255);
        TestSupport.Assert(BinaryValueCodec.Encode(42, byteField)[0] == 42, "UInt8 编码失败");
        var textField = new TableFieldDefinition("文本", 0, 8, FieldKind.FixedString);
        var encodedText = BinaryValueCodec.Encode("测试", textField);
        TestSupport.Assert(Convert.ToString(BinaryValueCodec.Decode(encodedText, textField)) == "测试", "固定 GBK 字符串往返失败");
        var overlongRejected = false;
        try { BinaryValueCodec.Encode("超过八字节的中文", textField); }
        catch (ArgumentOutOfRangeException) { overlongRejected = true; }
        TestSupport.Assert(overlongRejected, "固定字符串超长值必须拒绝");

        var uintField = new TableFieldDefinition("32位值", 0, 4, FieldKind.UInt32, FieldPolicy.Editable, 0, uint.MaxValue);
        var encodedUInt = BinaryValueCodec.Encode((long)uint.MaxValue, uintField);
        TestSupport.Assert(encodedUInt.SequenceEqual(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }), "UInt32 小端编码失败");
        TestSupport.Assert(Convert.ToUInt64(BinaryValueCodec.Decode(encodedUInt, uintField)) == uint.MaxValue, "UInt32 小端解码失败");

        var codec = new Item66RecordCodec();
        var type = new TableFieldDefinition("宝物类型", 17, 1, FieldKind.UInt8);
        var effect = new TableFieldDefinition("装备特效号", 18, 1, FieldKind.UInt8);
        var name = new TableFieldDefinition("名称", 0, 15, FieldKind.FixedString);

        var normal = new byte[25];
        normal[17] = 7;
        normal[18] = 9;
        TestSupport.Assert(Convert.ToInt32(codec.Decode(normal, type)) == 7 && Convert.ToInt32(codec.Decode(normal, effect)) == 9, "6.6 普通装备逻辑字段解码错误");
        TestSupport.Assert(codec.EncodeChangedField(normal, effect, 11).Single().Offset == 0x12, "6.6 普通装备特效必须写 row+0x12");
        TestSupport.Assert(codec.EncodeChangedField(normal, name, "测试").Single().Bytes.Length == 15, "6.6 物品名必须只编码 15 字节");
        TestSupport.Assert(codec.EncodeChangedField(normal, effect, 2).Single().Offset == 0x12,
            "6.6 类型<26时效果2仍为普通装备特效，不得交换字段");
        foreach (var (rawType, rawEffect) in new[] { (0, 2), (20, 3) })
        {
            var conflict = new byte[25]; conflict[0x11] = (byte)rawType; conflict[0x12] = (byte)rawEffect;
            TestSupport.Assert(Convert.ToInt32(codec.Decode(conflict, type)) == rawType &&
                Convert.ToInt32(codec.Decode(conflict, effect)) == rawEffect &&
                codec.EncodeChangedField(conflict, effect, 7).Single().Offset == 0x12,
                "6.6 普通类型优先于效果2/3的引擎分类规则未保留");
            var conversionRejected = false;
            try { codec.EncodeChangedField(conflict, type, 50); }
            catch (InvalidOperationException) { conversionRejected = true; }
            TestSupport.Assert(conversionRejected, "普通行类型变为>=26造成字段交换时没有拒绝");
        }

        var marker = new byte[25];
        marker[17] = 88;
        marker[18] = 2;
        TestSupport.Assert(Convert.ToInt32(codec.Decode(marker, type)) == 2 && Convert.ToInt32(codec.Decode(marker, effect)) == 88, "6.6 辅助行逻辑字段解码错误");
        TestSupport.Assert(codec.EncodeChangedField(marker, effect, 89).Single().Offset == 0x11, "6.6 辅助/道具特效必须写 row+0x11");
        TestSupport.Assert(codec.EncodeChangedField(marker, type, 3).Single().Offset == 0x12, "6.6 辅助/道具类别必须写 row+0x12");
        var markerConversionRejected = false;
        try { codec.EncodeChangedField(marker, type, 7); }
        catch (InvalidOperationException) { markerConversionRejected = true; }
        TestSupport.Assert(markerConversionRejected, "6.6 辅助/道具行不得隐式转换为普通装备");
        markerConversionRejected = false;
        try { codec.EncodeChangedField(marker, effect, 0); }
        catch (InvalidOperationException) { markerConversionRejected = true; }
        TestSupport.Assert(markerConversionRejected, "6.6 辅助效果降至普通类型区间造成字段交换时没有拒绝");
    }
}
