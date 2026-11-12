"""Freeze reviewed constant sites and produce source plus a complete KB disposition ledger.

Run from the game workspace. This generator reads the three official bases; the generated
C# and JSON are shipped with source, so building DataManager never needs the games/Python.
Requires pefile and capstone. It never writes a game file.
"""
from __future__ import annotations

import csv
from collections import Counter
import hashlib
import json
import re
from pathlib import Path

import capstone
import pefile

ROOT = Path(__file__).resolve().parents[2]
DM = ROOT / "data管理器"
BASE_NAMES = {"6.4": "洗隋", "6.5": "加强版6.5未加密版", "6.6": "复刻版6.6山贼"}
BASES = {}
CS = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
CS.detail = True
for version, name in BASE_NAMES.items():
    path = ROOT / "基底" / name / "Ekd5.exe"
    BASES[version] = (pefile.PE(str(path)), path.read_bytes())


def offset(version: str, va: int) -> int:
    pe, _ = BASES[version]
    return pe.get_offset_from_rva(va - pe.OPTIONAL_HEADER.ImageBase)


def site(version: str, instruction: int, value_offset: int = 0) -> dict:
    _, raw = BASES[version]
    i = next(CS.disasm(raw[offset(version, instruction):][:16], instruction, 1))
    assert i.imm_size in (1, 2, 4), (version, hex(instruction), i)
    va = instruction + i.imm_offset
    return {"va": va, "length": i.imm_size, "instructionVa": instruction,
            "instruction": f"{i.mnemonic} {i.op_str}", "valueOffset": value_offset,
            "original": int.from_bytes(i.bytes[i.imm_offset:i.imm_offset+i.imm_size], "little")}


DEFINITIONS: list[dict] = []
ENRICHMENTS: dict[str, dict] = {}


def add(id: str, module: str, title: str, instructions, minimum=0, maximum=127,
        source="engine-general-constants.json", description="", unit="点", versions=None):
    versions = versions or list(BASES)
    layouts = []
    for version in versions:
        addresses = instructions[version] if isinstance(instructions, dict) else instructions
        if isinstance(addresses, int):
            addresses = [addresses]
        sites = [site(version, a) for a in addresses]
        layouts.append({"version": version, "sites": sites})
    kind = "Int32" if layouts[0]["sites"][0]["length"] == 4 else "Byte"
    DEFINITIONS.append(dict(id=id, module=module, title=title, minimum=minimum, maximum=maximum,
        kind=kind, source=source, description=description or title, unit=unit, layouts=layouts))


# Equipment levels share several consumers. The turnover special argument is cap + 1.
for id, addresses in {
    "equip-level-cap-special": [(0x407DAB, 0), (0x4201CD, 1), (0x4201E0, 0)],
    "equip-level-cap-normal": [(0x407DD8, 0), (0x4201D1, 0)],
}.items():
    ENRICHMENTS[id] = {v: [site(v, address, delta) for address, delta in addresses] for v in BASES}

growth = "成长与宝物常量总表-三基底.csv"
for id, title, a, minimum, maximum in [
    ("equip-generation-band", "装备代次分段", 0x407DA8, 1, 85),
    ("equip-special-level-divisor", "宝物平均等级除数", 0x407F91, 1, 255),
    ("equip-normal-level-divisor", "普通装备段内除数", 0x407FA2, 1, 255),
    ("promotion-level-step", "转职需求等级间隔", 0x41D3E2, 1, 127),
    ("promotion-max-job-id", "可转职兵种ID上限", 0x41D3EB, 0, 79),
]:
    add(id, "成长与转职", title, a, minimum, maximum, growth, unit="级" if "等级" in title else "值")
add("char-exp-band-limit-65", "经验公式", "升级经验最大分段", [0x40D22E, 0x40D233],
    0, 127, growth, versions=["6.5"], unit="档")
add("char-exp-band-base-65", "经验公式", "升级经验分段基数", 0x40D238,
    1, 127, growth, versions=["6.5"], unit="档")
add("transform-type-threshold", "质变门槛", "类型变换等级门槛",
    {"6.4": 0x42013C, "6.5": 0x42013C, "6.6": 0x42010D}, 1, 255, growth,
    description="原生类型变换路径的等级门槛；自定义质变规则独立管理。", unit="级")
add("transform-override-threshold", "质变门槛", "原生覆盖表等级门槛",
    {"6.4": 0x42016B, "6.5": 0x42016B, "6.6": 0x420155}, 1, 255, growth,
    description="原生覆盖表生效的等级门槛；自定义规则通过质变设定管理。", unit="级")

add("money-cap", "金钱与费用", "金钱持有上限", [0x406C0E, 0x406C1A], 1, 2147483647,
    "engine-money-global.json", "结算增加金钱时的封顶值；比较与回写两处同步。", "金")
add("money-fixed-charge", "金钱与费用", "固定扣款金额", 0x414E92, 0, 999999,
    "engine-money-global.json", "固定扣款路径 0x414E92 使用的金额。", "金")
add("training-level-cost", "金钱与费用", "练武等级费用系数", 0x423814, 1, 127,
    "engine-payment-chain.json", "按人物等级计费时的乘数。", "金/级")
add("training-auto-cost", "金钱与费用", "自动提升费用系数", 0x423867, 1, 999999,
    "engine-payment-chain.json", "自动提升菜单的费用乘数；仅修改明确的乘法立即数。", "金")
add("slot-consume-cost", "金钱与费用", "槽位消耗费用系数", 0x4212C1, 1, 127,
    "engine-unit-slot-arrays.json", "消耗槽位时按 (slot0−1)×本值计费。", "金")

for index, a in enumerate([0x407175, 0x4071AA, 0x4071DF, 0x407214, 0x407249]):
    add(f"ability-display-cap-{index}", "能力上限", ["攻击", "防御", "精神", "爆发", "士气"][index] + "显示值上限",
        a, 1, 65535, "engine-ability-display-table.json", unit="点")
add("hp-display-cap", "能力上限", "HP计算上限", 0x40727F, 1, 65535, "engine-ability-display-table.json")
add("mp-display-cap", "能力上限", "MP计算上限", 0x4072AB, 1, 65535, "engine-ability-display-table.json")

add("hit-direction-step", "命中修正", "方向每档命中增幅", 0x408E1E, 0, 127,
    "engine-hit-height-modifier.json", "按方向分类 0/1/2 乘本值，再按百分比增加命中。", "%")
add("hit-extra-bonus", "命中修正", "附加条件命中增幅", 0x408E45, 0, 100,
    "engine-hit-height-modifier.json", "阵营及全局条件满足后追加的命中百分比。", "%")
add("hit-wrapper-floor", "命中修正", "最终命中保底", 0x408EF1, 0, 100,
    "engine-hit-height-modifier.json", unit="%")
add("hit-wrapper-cap", "命中修正", "最终命中封顶", 0x408F01, 1, 100,
    "engine-hit-height-modifier.json", unit="%")

for id, title, a, maximum in [
    ("strategy-special-series", "方向0增幅兵种系ID", 0x43C18E, 39),
    ("strategy-series-bonus", "方向0指定兵种系增幅", 0x43C194, 127),
    ("strategy-terrain-a", "方向0增幅地形ID一", 0x43C1A4, 29),
    ("strategy-terrain-b", "方向0增幅地形ID二", 0x43C1A8, 29),
    ("strategy-weather-threshold", "天气增幅天气ID下限", 0x43C1C4, 4),
    ("strategy-other-series", "方向2增幅对方兵种系ID", 0x43C1DA, 39),
    ("strategy-condition-bonus", "方向天气地形条件增幅", 0x43C1DE, 127),
]:
    # 6.4 uses series 0x40 outside the 40-row table as an intentionally disabled selector.
    add(id, "策略修正", title, a, 0, 255 if id == "strategy-special-series" else maximum,
        "engine-strategy-direction-weather.json", unit="%" if "增幅" in title and "ID" not in title else "ID")

# Reviewed score immediates. Structure offsets, dispatch IDs, stack adjustment and loop
# cardinalities are deliberately not balance controls. Every variant has its own guard.
for id, title, addresses, minimum, maximum, source in [
    ("ai4-hit-low", "AI分支4·命中低阈值", 0x439994, 0, 100, "346"),
    ("ai4-hit-high", "AI分支4·命中高阈值", 0x4399A0, 0, 100, "346"),
    ("ai4-score-low", "AI分支4·基础加分", 0x439999, 0, 127, "346"),
    ("ai4-score-high", "AI分支4·高命中加分", 0x4399A5, 0, 127, "346"),
    ("ai3-hp-divisor", "AI分支3·HP除数", 0x439A48, 1, 65535, "346"),
    ("ai3-hp-threshold", "AI分支3·HP阈值", 0x439A65, 0, 127, "346"),
    ("ai3-score", "AI分支3·条件加分", 0x439A6A, 0, 127, "346"),
    ("ai6-hit-low", "AI分支6·命中低阈值", 0x439A91, 0, 100, "346"),
    ("ai6-hit-high", "AI分支6·命中高阈值", 0x439A9A, 0, 100, "346"),
    ("ai6-score", "AI分支6·基础加分", 0x439A96, 0, 127, "346"),
    ("ai6-extra", "AI分支6·高命中加分", 0x439AA0, 0, 127, "346"),
    ("ai789-score", "AI分支7至9·条件加分", 0x4399FD, 0, 127, "712"),
    ("ai789-extra-a", "AI分支7至9·策略一加分", 0x439A0C, 0, 127, "712"),
    ("ai789-extra-b", "AI分支7至9·策略二加分", 0x439A16, 0, 127, "712"),
    ("ai10-score", "AI分支10·能力条件加分", 0x439B1B, 0, 127, "712"),
    ("ai11-score", "AI分支11·附加权重", {"6.4": 0x4399AF, "6.5": 0x4399EA, "6.6": 0x4399EA}, 0, 127, "712"),
    ("ai12-score-a", "AI分支12·兵种条件一加分", 0x439B55, 0, 127, "712"),
    ("ai12-score-b", "AI分支12·兵种条件二加分", 0x439B5F, 0, 127, "712"),
    ("ai12-score-c", "AI分支12·状态条件加分", 0x439B69, 0, 127, "712"),
    ("ai12-fallback-base", "AI分支12·回退评分基数", 0x439B7B, 0, 127, "712"),
    ("ai2-threshold", "AI分支2·能力阈值", 0x439B8B, 0, 255, "2-13-15"),
    ("ai2-score", "AI分支2·条件加分", 0x439B9B, 0, 127, "2-13-15"),
]:
    add(id, "策略AI评分", title, addresses, minimum, maximum,
        f"engine-ai-branch-{source}-scoring.json" if source != "2-13-15" else "engine-ai-branch-2-13-15.json")
for branch, starts in [(14, [0x439BD8, 0x439BE9, 0x439BFD, 0x439C0A]),
                       (15, [0x439C34, 0x439C45, 0x439C59, 0x439C66])]:
    for index, a in enumerate(starts):
        add(f"ai{branch}-score-{index}", "策略AI评分", f"AI分支{branch}·状态路径{index+1}加分", a,
            0, 127, "engine-ai-branch-2-13-15.json")

# Explicitly reviewed byte tables. 6.6's growth gate overlaps the suffix table and
# stays read-only; no general-constant definition is emitted for those seven bytes.
def table(id, title, va_by_version, count, maximum, source):
    for index in range(count):
        layouts = []
        for version, va in va_by_version.items():
            raw = BASES[version][1]
            address = va + index
            layouts.append({"version": version, "sites": [{"va": address, "length": 1,
                "instructionVa": None, "instruction": "内嵌字节表", "valueOffset": 0,
                "original": raw[offset(version, address)], "tableStart": va, "tableLength": count}]})
        DEFINITIONS.append(dict(id=f"{id}-{index}", module="内嵌参数表", title=f"{title} [{index}]",
            minimum=0, maximum=maximum, kind="Byte", source=source,
            description=f"{title}的第 {index} 项；固定地址与表外上下文校验。", unit="值", layouts=layouts))

table("equip-generation-base", "装备代次类型基表", {v: 0x482427 for v in BASES}, 13, 255, growth)
table("equip-growth-gate", "宝物能力成长门控", {"6.4": 0x46230F, "6.5": 0x46230F}, 7, 2, growth)
table("ai-direction-floor", "AI方向评分下限", {"6.5": 0x4395D8, "6.6": 0x412793},
      4, 100, "engine-ai-handler-constants-66.json")


def q(s): return json.dumps(s, ensure_ascii=False)
def arr(data): return "[" + ", ".join(f"0x{x:02X}" for x in data) + "]"


# Protect the opcode/address context while masking every editable field in the window.
all_fields = {v: [] for v in BASES}
for definition in DEFINITIONS:
    for layout in definition["layouts"]:
        for field in layout["sites"]:
            assert definition["minimum"] <= field["original"]-field["valueOffset"] <= definition["maximum"], (definition["id"], layout["version"], field)
            all_fields[layout["version"]].append((field["va"], field["length"]))
for layouts in ENRICHMENTS.values():
    for v, fields in layouts.items():
        for field in fields: all_fields[v].append((field["va"], field["length"]))


def context(v, f):
    _, raw = BASES[v]
    va, length = f["va"], f["length"]
    o = offset(v, va)
    pre, post = raw[o-16:o], raw[o+length:o+length+16]
    def mask(start, size):
        return [0 if any(a <= start+i < a+n for a,n in all_fields[v]) else 1 for i in range(size)]
    return pre, post, mask(va-len(pre),len(pre)), mask(va+length,len(post))


def extra(v, f):
    pre, post, pm, sm = context(v,f)
    return (f"new(0x{f['va']:08X}, 0x{offset(v,f['va']):X}, {arr(pre)}, {arr(post)}, "
            f"Length: {f['length']}, PrefixMask: {arr(pm)}, SuffixMask: {arr(sm)}, ValueOffset: {f['valueOffset']})")


lines = ["// Generated by tools/build_engine_constant_catalog.py; reviewed operand sites only.",
         "namespace DataManager.Core.Scanner;", "", "internal static class EngineGeneralConstantDefinitions", "{",
         "    public static readonly IReadOnlyList<EngineConstantDefinition> All =", "    ["]
for definition in DEFINITIONS:
    d = definition
    evidence = d["source"] + "；三基底原始站点、指令与SHA见 config/engine-constants-catalog.json。"
    lines += [f"        new({q(d['id'])}, {q(d['module'])}, {q(d['title'])}, {q(d['unit'])},",
              f"            {q(d['description'])}, {q(evidence)}, EngineConstantKind.{d['kind']}, {d['minimum']}, {d['maximum']},", "            ["]
    for layout in d["layouts"]:
        v, fields = layout["version"], layout["sites"]
        f = fields[0]; pre,post,pm,sm=context(v,f)
        extras = ", ExtraFields: [" + ", ".join(extra(v,x) for x in fields[1:]) + "]" if len(fields)>1 else ""
        nature = ", Nature: \"内嵌表值\"" if f.get("tableStart") else ""
        relocate = "false" if f.get("tableStart") else "true"
        lines += [f"                new({q(v)}, 0x{f['va']:08X}, 0x{offset(v,f['va']):X}, {f['length']}, {arr(pre)}, {arr(post)},",
                  f"                    Bases: [{q(v)}], Relocate: {relocate}, PrefixMask: {arr(pm)}, SuffixMask: {arr(sm)}{nature}{extras}),"]
    lines += ["            ], ApplicableBases: [" + ", ".join(q(x["version"]) for x in d["layouts"]) + "]),"]
lines += ["    ];", "", "    public static EngineConstantDefinition Enrich(EngineConstantDefinition definition)", "    {", "        return definition.Id switch", "        {"]
for id, layouts in ENRICHMENTS.items():
    lines += [f"            {q(id)} => definition with {{ Layouts = ["]
    for v, fields in layouts.items():
        lines += [f"                definition.Layouts.First(layout => layout.AppliesTo({q(v)})) with {{ Bases = [{q(v)}], ExtraFields = [" + ", ".join(extra(v,f) for f in fields) + "] },"]
    lines += ["            ] },"]
lines += ["            _ => definition", "        };", "    }", "}"]
(DM/"src/DataManager.Core/Scanner/EngineGeneralConstantDefinitions.g.cs").write_text("\n".join(lines)+"\n",encoding="utf-8-sig")

kb=ROOT/"Knowledge-base-for-CCZ-mod/6.5/data/mechanisms"
csv_path=ROOT/"工具整合包/本地知识库/03-机制详解"/growth
sources=[dict(path=path.relative_to(ROOT).as_posix(), sha256=hashlib.sha256(path.read_bytes()).hexdigest().upper())
         for path in [csv_path, *sorted(kb.glob("engine-*.json"))]]
resolution_path=DM/"config/engine-constant-candidate-resolutions.json"
resolution_source=dict(path=resolution_path.relative_to(DM).as_posix(),
    sha256=hashlib.sha256(resolution_path.read_bytes()).hexdigest().upper())
resolution_records=json.loads(resolution_path.read_text(encoding="utf-8"))["records"]
resolution_keys=[(r["sourceFile"],r["entityId"],r["jsonPath"]) for r in resolution_records]
assert len(resolution_keys)==len(set(resolution_keys)), "Duplicate candidate resolution keys"
resolutions=dict(zip(resolution_keys,resolution_records))
used_resolution_keys=set()
catalog = dict(schemaVersion=1, generatedFrom="2026-11-12 local knowledge inventory", sources=sources,
    candidateResolutionSource=resolution_source, bases={v: dict(name=BASE_NAMES[v],
    sha256=hashlib.sha256(data[1]).hexdigest().upper()) for v,data in BASES.items()},
    definitions=DEFINITIONS, linkedExisting=ENRICHMENTS)
(DM/"config/engine-constants-catalog.json").write_text(json.dumps(catalog,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")

# Preserve a disposition for each explicit CSV row and every numeric/address leaf.
# Candidate leaves require an exact source/entity/path resolution; never infer a
# writable parameter from equal values or a shared module name.
ledger=[]
existing_text="\n".join((DM/p).read_text(encoding="utf-8-sig") for p in [
    "src/DataManager.Core/Scanner/EngineConstantService.cs", "src/DataManager.Core/Scanner/BattleFormulaConstantDefinitions.cs"])
existing_addresses={int(value,16) for value in re.findall(r"0x[0-9A-Fa-f]{6,8}", existing_text)}
existing_ids=set(re.findall(r'new\("([a-z][a-z0-9-]+)",\s*"[^"\n]+",\s*"[^"\n]+",\s*"[^"\n]+",',existing_text))
known_ids=existing_ids|{d["id"] for d in DEFINITIONS}
assert len(known_ids)==174, ("Unexpected logical ID inventory",len(known_ids))
new_addresses={f["va"]: d["id"] for d in DEFINITIONS for l in d["layouts"] for f in l["sites"]}
new_addresses.update({f["va"]:id for id,ls in ENRICHMENTS.items() for fs in ls.values() for f in fs})
instruction_targets={f["instructionVa"]:d["id"] for d in DEFINITIONS for l in d["layouts"] for f in l["sites"] if f["instructionVa"]}
instruction_targets.update({f["instructionVa"]:id for id,ls in ENRICHMENTS.items() for fs in ls.values() for f in fs})
for index,row in enumerate(csv.DictReader(csv_path.open(encoding="utf-8-sig")),2):
    if row.get("可改性") not in ("可改","只读"): continue
    address=row["地址(VA)"]; va=int(address,16) if address.startswith("0x") else 0
    target=new_addresses.get(va); disposition="新增/关联常量" if target else "只读"
    reason="指令/表值已按当前基底重核" if target else "结构常量或缺少独立写入契约"
    if row["分类"]=="五维档位" and row["可改性"]=="可改":
        target="tier-corrections" if "修正表" in row["名称"] else "tier-cap" if "封顶" in row["名称"] else "tier-thresholds"
        disposition="专用档位设定";reason="统一档位模型编译，避免独立写入覆盖"
    elif va in existing_addresses and not target:
        disposition="已有常量";reason="已有签名定义，保持现有ID"
    if row["基底"]=="6.6" and row["名称"].startswith("宝物能力门控"):
        target=None;disposition="冲突只读";reason="与0x4CB6BA后缀类型共享字节，禁止独立门控写入"
    if target=="tier-corrections":
        disposition="结构只读保留";reason="当前样本空组，word ID/负数编码/扩容边界缺实际非空证据；只读扫描并保留原组内容"
    ledger.append(dict(source=str(csv_path.relative_to(ROOT)),line=index,version=row["基底"],
        name=row["名称"],va=address,policy=row["可改性"],disposition=disposition,targetId=target,reason=reason))

def leaves(value,path=""):
    if isinstance(value,dict):
        for key,item in value.items(): yield from leaves(item,path+"."+key)
    elif isinstance(value,list):
        for i,item in enumerate(value): yield from leaves(item,f"{path}[{i}]")
    elif isinstance(value,(int,float)) and not isinstance(value,bool): yield path,value
    elif isinstance(value,str) and re.fullmatch(r"(?:0x[0-9a-fA-F]+|\d+)",value): yield path,value

for path in sorted(kb.glob("engine-*.json")):
    data=json.loads(path.read_text(encoding="utf-8-sig"))
    source_defs=[d["id"] for d in DEFINITIONS if d["source"]==path.name]
    for entity in data.get("entries",[]):
        for json_path,value in leaves(entity):
            token=str(value);va=int(token,16) if token.startswith("0x") else None
            target=(new_addresses.get(va) or instruction_targets.get(va)) if va else None
            disposition="只读研究/结构数据";reason="地址、结构、哨兵、运行期状态或尚未形成独立可写站点"
            if target: disposition="常量站点";reason="已建立签名与范围"
            elif source_defs and any(k in json_path.lower() for k in ("weight","threshold","bonus","cap","constants","extra")):
                disposition="机制候选已逐站点筛选";reason="本模块可写子集见reviewedIds；其余数字不自动开放"
            if "4D1A80" in token.upper(): disposition="证据冲突只读";reason="40×40相克与41×41等级差描述冲突，沿用既有表页"
            record=dict(source=str(path.relative_to(ROOT)),entityId=entity.get("id"),jsonPath=json_path,
                value=value,disposition=disposition,targetId=target,reason=reason)
            if disposition=="机制候选已逐站点筛选":
                key=(path.name,entity.get("id"),json_path)
                assert key in resolutions,("Candidate lacks individual resolution",key)
                resolution=resolutions[key]
                assert resolution["value"]==value,("Resolution source value changed",key)
                ids=resolution["logicalIds"]
                status=resolution["resolvedStatus"]
                assert len(ids)==len(set(ids)) and set(ids)<=known_ids,("Unknown/duplicate logical IDs",key,ids)
                assert status=="adopted" and ids or status.startswith("readOnly") and not ids, ("Unresolved candidate",key)
                assert resolution["reason"].strip(),("Missing individual reason",key)
                record.update(originalDisposition=disposition,reviewedCandidate=True,
                    resolvedStatus=status,logicalIds=ids,reason=resolution["reason"],
                    disposition="已采纳常量" if ids else "只读已说明",
                    resolutionKey="|".join(key))
                if resolution.get("aliasOfJsonPath"):
                    record["aliasOfJsonPath"]=resolution["aliasOfJsonPath"]
                used_resolution_keys.add(key)
            ledger.append(record)
assert used_resolution_keys==set(resolutions),("Unused candidate resolutions",set(resolutions)-used_resolution_keys)
candidate_records=[r for r in ledger if r.get("reviewedCandidate")]
assert len(candidate_records)==133, ("Candidate inventory changed",len(candidate_records))
leaf_keys=[(r["source"],r.get("entityId"),r.get("jsonPath"),r.get("line")) for r in ledger]
assert len(leaf_keys)==len(set(leaf_keys)),"Duplicate ledger identities"
csv_records=[r for r in ledger if "line" in r]
json_records=[r for r in ledger if "jsonPath" in r]
out=dict(schemaVersion=2,sources=sources,candidateResolutionSource=resolution_source,
    summary=dict(existingLogicalDefinitions=88,addedLogicalDefinitions=len(DEFINITIONS),
    totalLogicalDefinitions=88+len(DEFINITIONS), sourceFiles=len(sources),
    csvRecords=len(csv_records),csvEditableRecords=sum(r["policy"]=="可改" for r in csv_records),
    jsonNumericLeaves=len(json_records),
    csvDispositionCounts=dict(Counter(r["disposition"] for r in csv_records)),
    jsonDispositionCounts=dict(Counter(r["disposition"] for r in json_records)),
    reviewedCandidateCount=len(candidate_records),
    candidateResolvedStatusCounts=dict(Counter(r["resolvedStatus"] for r in candidate_records)),
    unresolvedCandidateCount=0,duplicateLedgerKeyCount=0,
    unusedCandidateResolutionCount=len(set(resolutions)-used_resolution_keys)),records=ledger)
(DM/"config/engine-constants-knowledge-ledger.json").write_text(json.dumps(out,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
print(f"Generated {len(DEFINITIONS)} definitions, {len(ledger)} knowledge dispositions")
