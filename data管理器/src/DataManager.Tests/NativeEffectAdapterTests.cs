using System.Buffers.Binary;
using DataManager.Core.Scanner;

namespace DataManager.Tests;

/// <summary>Uses only byte arrays. The supplied game files are never opened for writing.</summary>
internal static class NativeEffectAdapterTests
{
    public static void Run(string workspaceRoot)
    {
        var tested = 0;
        foreach (var name in new[] { "复刻版6.6山贼 国庆", "复刻版6.6山贼", "加强版6.5未加密版", "洗隋", "刘备6.4模组 - 副本" })
        {
            var path = Path.Combine(workspaceRoot, "基底", name, "Ekd5.exe");
            if (!File.Exists(path)) continue;
            Verify(File.ReadAllBytes(path), name);
            tested++;
        }
        Check(tested > 0, "没有找到原生适配只读测试样本。");
        Check(!NativeEffectAdapter.Read([0x4d, 0x5a]).IsSupported, "损坏PE必须被拒绝。");
        Console.WriteLine($"NATIVE_EFFECT_ADAPTER_TESTS_OK ({tested} samples)");
    }

    private static void Verify(byte[] source, string name)
    {
        var baseline = NativeEffectAdapter.Read(source);
        Check(baseline.IsSupported && !baseline.IsInstalled, name + ": " + string.Join(";", baseline.Diagnostics));
        Check(baseline.Hooks.Count == 6 && baseline.Hooks.Count(h => h.IsAuxiliary) == 3,
            "必须覆盖全部六处计算/显示/预览消费者。");
        var is66 = name.StartsWith("复刻版6.6", StringComparison.Ordinal);
        var expectedHooks = is66
            ? new uint[] { 0x407c98, 0x461e83, 0x465ea4, 0x405676, 0x407cda, 0x465ed8 }
            : [0x407caa, 0x461e83, 0x465ea4, 0x429add, 0x407ce9, 0x465ed8];
        Check(baseline.Hooks.Select(h => h.Address).Order().SequenceEqual(expectedHooks.Order()),
            "实际hook清单必须匹配逐指令审查地址，尤其不能遗漏465ED8。");
        Console.WriteLine("NATIVE_EFFECT_ADAPTER_HOOKS " + name + " " +
            string.Join(" ", baseline.Hooks.Select(h => h.Address.ToString("X8"))));
        var saved = (byte[])source.Clone();
        var build = NativeEffectAdapter.Build(source, 42, 42, true);
        Check(source.AsSpan().SequenceEqual(saved), "纯候选构建不得修改输入数组。");
        Check(build.State.IsInstalled && build.State.HasFfGuard && build.State.HpCurrentSelector == 42 &&
            build.State.MpCurrentSelector == 42 && build.State.Hooks.Count == 7, "复读应保留同号双能力及FF守卫。");
        var after = build.After;
        var pe = Read32(after, 0x3c); var opt = pe + 24;
        Check(Read16(after, pe + 6) == Read16(source, pe + 6) + 1, "专用节数必须增加1。");
        Check(after.Length > source.Length && after.Length % Read32(after, opt + 36) == 0, "新节原始长度必须正确对齐。");
        foreach (var hook in build.State.Hooks.Where(h => h.RequestedAbilityOffset != 0))
        {
            Check(ReadCall(after, hook.Address) == hook.AdapterTarget, "原E8必须跳到各自适配helper。");
            foreach (var requested in new[] { 0, 2, 4, 5, 6, 7 })
            {
                var result = ExecuteHelper(after, hook, requested, 42, 5);
                Check(result == (requested is 5 or 6 ? requested : 5), "同号HP/MP或其他能力保留路径错误。");
                if (requested is 5 or 6)
                    Check(ExecuteHelper(after, hook, requested, 37, 5) == -1,
                        "HP改绑后不应再隐式保留旧37号。");
            }
        }
        var unchangedAux = is66
            ? new uint[] { 0x4038d2, 0x4086de, 0x42139d, 0x461ff8 }
            : [0x4038d2, 0x4086d7, 0x42139d, name.StartsWith("刘备6.4", StringComparison.Ordinal) ? 0x461fffu : 0x461ff8u];
        foreach (var address in unchangedAux)
            Check(after.AsSpan(Offset(after, address), 5).SequenceEqual(source.AsSpan(Offset(source, address), 5)),
                "无HP/MP请求的四处基础分类消费者必须保持原调用。");
        var guard = build.State.Hooks.Single(h => h.RequestedAbilityOffset == 0);
        Check(ExecuteGuard(after, guard, 255) == 0, "FF守卫必须返回值0与命中标志0。");
        Check(ExecuteGuard(after, guard, 37) == 1, "非FF必须原栈尾调用旧装备通道。");
        var restored = NativeEffectAdapter.Remove(after).After;
        Check(restored.AsSpan().SequenceEqual(source), "卸载必须逐字节恢复全部原调用、PE头和长度。");
        var disabled = NativeEffectAdapter.Build(after, 255, 42, true);
        foreach (var hook in disabled.State.Hooks.Where(h => h.RequestedAbilityOffset != 0))
        {
            Check(ExecuteHelper(disabled.After, hook, 5, 255, 5) == -1, "HP选择FF必须拒绝FF装备。");
            Check(ExecuteHelper(disabled.After, hook, 6, 42, 6) == 6, "HP禁用不得影响MP。");
        }
        var lowSelector = NativeEffectAdapter.Build(source, 5, 5, true);
        foreach (var hook in lowSelector.State.Hooks.Where(h => h.RequestedAbilityOffset != 0))
            Check(ExecuteHelper(lowSelector.After, hook, 5, 5, 0) == (hook.IsAuxiliary ? 0 : 5),
                "辅助分类器低于26的基本类型不能被误当HP效果号，武甲独立效果字段仍可使用低号。");
        Check(disabled.After.Length <= after.Length, "重复更新应替换自己的节而不累积节或长度。");
        Check(NativeEffectAdapter.Build(after, 42, 42, true).After.AsSpan().SequenceEqual(after), "相同绑定重复构建必须逐字节幂等。");
        var unrelated = (byte[])after.Clone();
        unrelated[0x9e800] ^= 1; // name/data region, outside every hook and the adapter section
        Check(NativeEffectAdapter.Read(unrelated).IsSupported, "无关名称数据修改应保持可更新。");
        var removedEdited = NativeEffectAdapter.Remove(unrelated).After;
        Check(removedEdited[0x9e800] == unrelated[0x9e800], "卸载应保留安装后的无关数据修改。");
        var tampered = (byte[])after.Clone();
        tampered[Offset(tampered, build.State.Hooks[0].Address)] ^= 1;
        Check(!NativeEffectAdapter.Read(tampered).IsSupported, "被其他修改覆盖的hook必须拒绝。");
        ExpectRejected(() => NativeEffectAdapter.Remove(tampered));
        tampered = (byte[])after.Clone();
        tampered[Offset(tampered, build.State.Hooks[0].AdapterTarget)] ^= 1;
        Check(!NativeEffectAdapter.Read(tampered).IsSupported, "helper摘要不符必须拒绝。");
        var unknown = (byte[])source.Clone();
        unknown[Offset(unknown, baseline.Hooks[0].OriginalTarget)] ^= 1;
        Check(!NativeEffectAdapter.Read(unknown).IsSupported, "原始分类器签名变化必须拒绝套地址。");
        var weaponClassifier = baseline.Hooks.First(h => !h.IsAuxiliary).OriginalTarget;
        var weaponTable = (uint)Read32(source, Offset(source, weaponClassifier) + 43);
        unknown = (byte[])source.Clone(); unknown[Offset(unknown, weaponTable) + 5] = 6;
        Check(!NativeEffectAdapter.Read(unknown).IsSupported, "HP分类表已改号时不能仍报告固定37基准。");
        var auxClassifier = baseline.Hooks.First(h => h.IsAuxiliary).OriginalTarget;
        unknown = (byte[])source.Clone(); unknown[Offset(unknown, auxClassifier) + 0x52] ^= 1;
        Check(!NativeEffectAdapter.Read(unknown).IsSupported, "辅助HP立即数改绑必须使旧基准识别失败。");
        var auxTable = (uint)Read32(source, Offset(source, auxClassifier) + 35);
        unknown = (byte[])source.Clone(); unknown[Offset(unknown, auxTable)] = 5;
        Check(!NativeEffectAdapter.Read(unknown).IsSupported, "基础类型表若引入HP分类必须拒绝旧消费者假设。");
        var noGuard = NativeEffectAdapter.Build(source, 37, 38, false);
        Check(!noGuard.State.HasFfGuard && noGuard.State.Hooks.Count == 6, "禁用guard选项必须精确复读。");
        Check(NativeEffectAdapter.Remove(noGuard.After).After.AsSpan().SequenceEqual(source), "无guard安装亦可精确恢复。");
        Console.WriteLine("NATIVE_EFFECT_ADAPTER_SAMPLE_OK " + name);
    }

    /// <summary>
    /// Executes the emitted helper instruction subset, intercepting only the unchanged original
    /// classifier CALL. This checks real branch displacements, source-field choice, EBP access,
    /// saved registers and both cdecl/stdcall stack depths rather than mirroring C# selection code.
    /// </summary>
    private static int ExecuteHelper(byte[] image, NativeEffectHook hook, int requested, byte effect, int originalClass)
    {
        var code = Offset(image, hook.AdapterTarget); var ip = code;
        var stack = new List<int> { 0x123456, 16 };
        var eax = 0; var ecx = 0x1234; var edx = 0x5678; var zero = false; var below = false;
        var calleeCleanup = !hook.IsAuxiliary || image[Offset(image, hook.OriginalTarget) + 0x7e] == 0xc2;
        for (var steps = 0; steps < 100; steps++)
        {
            switch (image[ip++])
            {
                case 0x52: stack.Insert(0, edx); break;
                case 0x51: stack.Insert(0, ecx); break;
                case 0x59: ecx = Pop(); break;
                case 0x5a: edx = Pop(); break;
                case 0xff:
                    Check(image[ip++] == 0x74 && image[ip++] == 0x24, "未知helper FF编码。");
                    var n = image[ip++] / 4; var value = stack[n]; stack.Insert(0, value); break;
                case 0xe8:
                    var target = unchecked((uint)(hook.AdapterTarget + ip - code + 4 + Read32(image, ip)));
                    ip += 4; Check(target == hook.OriginalTarget, "helper仅能调用原分类器。");
                    Check(stack[0] == 16, "分类器参数必须是原物品ID。");
                    if (calleeCleanup) Pop();
                    eax = originalClass; ecx = 0x100000; break;
                case 0x83:
                    Check(image[ip++] == 0xc4 && image[ip++] == 4, "未知helper栈清理。"); Pop(); break;
                case 0x80:
                    var mod = image[ip++]; var disp = image[ip++]; var immediate = image[ip++];
                    if (mod == 0x7d) { Check(disp == hook.RequestedAbilityOffset, "请求能力EBP偏移错误。"); zero = requested == immediate; below = requested < immediate; }
                    else { Check(mod == 0x79 && disp == (hook.IsAuxiliary ? 0x11 : 0x14), "装备效果字段错误。"); zero = effect == immediate; below = effect < immediate; }
                    break;
                case 0x0f:
                    var op = image[ip++];
                    if (op == 0xb6) { Check(image[ip++] == 0x45 && image[ip++] == hook.RequestedAbilityOffset, "命中返回能力索引错误。"); eax = requested; }
                    else { Check(op is 0x82 or 0x84 or 0x85, "未知helper条件跳转。"); var rel = Read32(image, ip); ip += 4; if (op == 0x82 ? below : zero == (op == 0x84)) ip += rel; }
                    break;
                case 0xe9: var delta = Read32(image, ip); ip += 4 + delta; break;
                case 0xb8: eax = Read32(image, ip); ip += 4; break;
                case 0xc2:
                    Check(calleeCleanup && Read16(image, ip) == 4, "stdcall返回清理不符。");
                    Check(stack.Count == 2 && ecx == 0x1234 && edx == 0x5678, "helper寄存器/栈不平衡。"); return eax;
                case 0xc3:
                    Check(!calleeCleanup && stack.Count == 2 && ecx == 0x1234 && edx == 0x5678, "cdecl返回清理不符。"); return eax;
                default: throw new InvalidDataException("helper包含未测试机器指令。");
            }
        }
        throw new InvalidDataException("helper控制流没有返回。");
        int Pop() { var value = stack[0]; stack.RemoveAt(0); return value; }
    }

    private static int ExecuteGuard(byte[] image, NativeEffectHook guard, byte selector)
    {
        var p = Offset(image, guard.AdapterTarget);
        Check(image.AsSpan(p, 7).SequenceEqual(new byte[] { 0x80, 0x7c, 0x24, 4, 255, 0x0f, 0x85 }), "guard参数判断编码错误。");
        var original = p + 11 + Read32(image, p + 7);
        Check(image.AsSpan(p + 11, 7).SequenceEqual(new byte[] { 0x33, 0xc0, 0x33, 0xc9, 0xc2, 12, 0 }), "FF返回必须清EAX/ECX并ret12。");
        Check(image[original] == 0xe9, "非FF必须尾调用。");
        var target = unchecked((uint)(guard.AdapterTarget + original - p + 5 + Read32(image, original + 1)));
        Check(target == guard.OriginalTarget, "guard原装备入口错误。");
        return selector == 255 ? 0 : 1;
    }

    private static uint ReadCall(byte[] image, uint address)
    {
        var p = Offset(image, address); Check(image[p] == 0xe8, "调用点不是E8。");
        return unchecked(address + 5 + (uint)Read32(image, p + 1));
    }
    private static int Offset(byte[] image, uint va)
    {
        var pe = Read32(image, 0x3c); var optional = pe + 24; var rva = va - (uint)Read32(image, optional + 28);
        var table = optional + Read16(image, pe + 20);
        for (var i = 0; i < Read16(image, pe + 6); i++)
        {
            var h = table + 40 * i; var sectionRva = (uint)Read32(image, h + 12);
            if (rva >= sectionRva && rva < sectionRva + (uint)Read32(image, h + 16))
                return checked((int)(rva - sectionRva) + Read32(image, h + 20));
        }
        throw new InvalidDataException("测试VA没有磁盘映射。");
    }
    private static int Read32(byte[] image, int offset) => BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(offset, 4));
    private static ushort Read16(byte[] image, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset, 2));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static void ExpectRejected(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new InvalidDataException("本应拒绝的原生适配修改被接受。");
    }
}
