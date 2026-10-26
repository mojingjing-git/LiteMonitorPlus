from pathlib import Path
import json
import xml.etree.ElementTree as ET
from hashlib import sha256

root = Path(__file__).resolve().parent
repo = root.parent.parent
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
summaries = {}
for path in (root / "results").glob("*.trx"):
    doc = ET.parse(path).getroot()
    summary = doc.find("t:ResultSummary", ns)
    counter = doc.find("t:ResultSummary/t:Counters", ns)
    results = doc.findall("t:Results/t:UnitTestResult", ns)
    summaries[path.name] = {
        "outcome": summary.get("outcome") if summary is not None else None,
        "counters": counter.attrib if counter is not None else {},
        "failed": [
            {"name": item.get("testName"),
             "message": item.findtext("t:Output/t:ErrorInfo/t:Message", "", ns)}
            for item in results if item.get("outcome") == "Failed"
        ],
        "theme_studio": {
            "executed": sum("ThemeStudio" in item.get("testName", "") for item in results),
            "passed": sum("ThemeStudio" in item.get("testName", "") and item.get("outcome") == "Passed" for item in results),
            "failed": sum("ThemeStudio" in item.get("testName", "") and item.get("outcome") == "Failed" for item in results),
        },
    }
manifest = json.loads((root / "source-manifest-before-full.json").read_text(encoding="utf-8-sig"))
changed = [
    item["Path"] for item in manifest
    if sha256((repo / item["Path"]).read_bytes()).hexdigest().upper() != item["SHA256"]
]
artifacts = {}
for path in [
    root / "artifacts/bin/LiteMonitor/debug/LiteMonitor++.dll",
    root / "artifacts/bin/LiteMonitor.Tests/debug/LiteMonitor.Tests.dll",
    repo / "bin/acceptance-theme-studio-20261026/Debug/net8.0-windows/LiteMonitor++.dll",
    repo / "tests/LiteMonitor.Tests/bin/acceptance-theme-studio-20261026/Debug/net8.0-windows/LiteMonitor.Tests.dll",
]:
    if path.is_file():
        artifacts[str(path.relative_to(repo)).replace("\\", "/")] = sha256(path.read_bytes()).hexdigest().upper()
output = {
    "source_changed_since_manifest": changed,
    "runs": summaries,
    "artifact_sha256": artifacts,
    "notes": {
        "main-full.trx": "First full attempt used an unsupported --artifacts-path layout. Host locator failures are not attributed to this candidate.",
        "main-full-supported-layout.trx": "Attempt used supported BaseOutputPath with same-build Host. Review run was canceled after detecting a separate concurrent testhost. No completed TRX or valid full-pass claim.",
    },
    "full_acceptance": {
        "accepted": False,
        "reason": "No valid completed full suite. First attempt had Host layout failures and testhost crash; second was canceled to respect the serial hardware-test constraint.",
        "concurrency_observation": {
            "review_testhost_pid": 23604,
            "other_testhost_pid": 42240,
            "other_output": "tests/LiteMonitor.Tests/bin/Debug/net8.0-windows",
            "action": "Canceled only review exec session; did not terminate the other testhost.",
        },
    },
}
destination = repo / "docs/verification/theme-studio/independent-review/test-summary.json"
destination.write_text(json.dumps(output, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps({
    "source_changed": changed,
    "runs": {
        key: {
            "outcome": value["outcome"],
            "counters": value["counters"],
            "theme_studio": value["theme_studio"],
        } for key, value in summaries.items()
    }
}, ensure_ascii=False, indent=2))
