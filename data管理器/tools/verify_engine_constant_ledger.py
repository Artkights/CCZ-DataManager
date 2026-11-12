"""Validate the shipped knowledge ledger without building or touching game files."""
from collections import Counter
import hashlib
import json
from pathlib import Path
import re

DM = Path(__file__).resolve().parents[1]
ROOT = DM.parent


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def key(record):
    source = record.get("sourceFile") or record["source"].replace("\\", "/").split("/")[-1]
    return source, record["entityId"], record["jsonPath"]


def resolve_path(value, path):
    for token in re.findall(r"\.([^.[\]]+)|\[(\d+)\]", path):
        value = value[int(token[1])] if token[1] else value[token[0]]
    return value


catalog = read(DM / "config/engine-constants-catalog.json")
ledger = read(DM / "config/engine-constants-knowledge-ledger.json")
resolution_path = DM / "config/engine-constant-candidate-resolutions.json"
resolutions = read(resolution_path)["records"]
all_source = "\n".join((DM / p).read_text(encoding="utf-8-sig") for p in [
    "src/DataManager.Core/Scanner/EngineConstantService.cs",
    "src/DataManager.Core/Scanner/BattleFormulaConstantDefinitions.cs",
    "src/DataManager.Core/Scanner/EngineGeneralConstantDefinitions.g.cs"])
ids = re.findall(r'new\("([a-z][a-z0-9-]+)",\s*"[^"\n]+",\s*"[^"\n]+",\s*"[^"\n]+",', all_source)
assert len(ids) == len(set(ids)) == 174, "Logical IDs missing or duplicated"
known_ids = set(ids)

sources = {item["path"].replace("\\", "/"): item for item in ledger["sources"]}
assert len(sources) == len(ledger["sources"]) == 101
assert sources == {item["path"].replace("\\", "/"): item for item in catalog["sources"]}
for path, item in sources.items():
    assert hashlib.sha256((ROOT / path).read_bytes()).hexdigest().upper() == item["sha256"], path
review_hash = hashlib.sha256(resolution_path.read_bytes()).hexdigest().upper()
assert ledger["candidateResolutionSource"]["sha256"] == review_hash
assert catalog["candidateResolutionSource"]["sha256"] == review_hash

records = ledger["records"]
identity = [(r["source"], r.get("entityId"), r.get("jsonPath"), r.get("line")) for r in records]
assert len(identity) == len(set(identity)) == 3644, "Duplicate/missing ledger identities"
candidates = [r for r in records if r.get("reviewedCandidate")]
assert len(candidates) == len(resolutions) == 133
assert len({key(r) for r in resolutions}) == 133, "Duplicate resolutions"
assert {key(r) for r in candidates} == {key(r) for r in resolutions}, "Unresolved/unused candidate"
candidate_map = {key(r): r for r in candidates}
source_entities = {}
for resolution in resolutions:
    file = resolution["sourceFile"]
    if file not in source_entities:
        path = ROOT / "Knowledge-base-for-CCZ-mod/6.5/data/mechanisms" / file
        source_entities[file] = {entry["id"]: entry for entry in read(path)["entries"]}
    original = source_entities[file][resolution["entityId"]]
    assert resolve_path(original, resolution["jsonPath"]) == resolution["value"], key(resolution)
    actual = candidate_map[key(resolution)]
    for field in ("resolvedStatus", "logicalIds", "reason", "value"):
        assert actual[field] == resolution[field], (key(resolution), field)
    status, targets = actual["resolvedStatus"], actual["logicalIds"]
    assert status == "adopted" and targets or status.startswith("readOnly") and not targets
    assert len(targets) == len(set(targets)) and set(targets) <= known_ids
    assert actual["reason"].strip() and "reviewedIds" not in actual
    if alias := actual.get("aliasOfJsonPath"):
        sibling = (file, resolution["entityId"], alias)
        assert sibling in candidate_map and sibling != key(resolution)
        assert set(targets) <= set(candidate_map[sibling]["logicalIds"]), "Alias maps to unrelated IDs"

for record in candidates:
    if record["resolvedStatus"] != "adopted":
        continue
    original = record["value"]
    numeric = int(original, 16) if isinstance(original, str) and original.startswith("0x") else int(original)
    for target in record["logicalIds"]:
        definition = next((item for item in catalog["definitions"] if item["id"] == target), None)
        if definition:
            assert any(site["original"] - site["valueOffset"] == numeric
                       for layout in definition["layouts"] for site in layout["sites"]), (key(record), target)

summary = ledger["summary"]
counts = dict(Counter(r["resolvedStatus"] for r in candidates))
assert summary["candidateResolvedStatusCounts"] == counts
assert summary["reviewedCandidateCount"] == 133
assert summary["unresolvedCandidateCount"] == summary["duplicateLedgerKeyCount"] == summary["unusedCandidateResolutionCount"] == 0
assert not any("reviewedIds" in r for r in records), "Module-wide pseudo-resolution remains"
print("ENGINE_CONSTANT_LEDGER_OK sources=101 records=3644 candidateCount=133 adopted=48 readonly=85 "
      "unresolved=0 duplicateKeys=0 unusedResolutions=0 logicalIds=174")
