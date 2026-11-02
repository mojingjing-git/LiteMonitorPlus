#!/usr/bin/env python3
"""Summarize Theme Studio audit TRX files without requiring repository-local run output."""

from __future__ import annotations

import argparse
import json
import sys
import xml.etree.ElementTree as ET
from hashlib import sha256
from pathlib import Path

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def file_sha256(path: Path) -> str:
    return sha256(path.read_bytes()).hexdigest().upper()


def summarize_trx(path: Path) -> dict:
    root = ET.parse(path).getroot()
    summary = root.find("t:ResultSummary", NS)
    counters = root.find("t:ResultSummary/t:Counters", NS)
    results = root.findall("t:Results/t:UnitTestResult", NS)
    return {
        "outcome": summary.get("outcome") if summary is not None else None,
        "counters": dict(counters.attrib) if counters is not None else {},
        "failed": [
            {
                "name": item.get("testName"),
                "message": item.findtext("t:Output/t:ErrorInfo/t:Message", "", NS),
            }
            for item in results
            if item.get("outcome") == "Failed"
        ],
        "theme_studio": {
            "executed": sum("ThemeStudio" in item.get("testName", "") for item in results),
            "passed": sum(
                "ThemeStudio" in item.get("testName", "") and item.get("outcome") == "Passed"
                for item in results
            ),
            "failed": sum(
                "ThemeStudio" in item.get("testName", "") and item.get("outcome") == "Failed"
                for item in results
            ),
        },
    }


def changed_from_manifest(manifest_path: Path, repo_root: Path) -> list[str]:
    entries = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    changed: list[str] = []
    for item in entries:
        relative = str(item["Path"])
        expected = str(item["SHA256"])
        candidate = repo_root / relative
        if not candidate.is_file() or file_sha256(candidate) != expected:
            changed.append(relative)
    return changed


def parse_args() -> argparse.Namespace:
    probe_root = Path(__file__).resolve().parent
    repo_root = probe_root.parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--trx-dir", type=Path, default=probe_root / "results")
    parser.add_argument(
        "--manifest",
        type=Path,
        help="Optional JSON inventory containing Path and SHA256 entries.",
    )
    parser.add_argument("--repo-root", type=Path, default=repo_root)
    parser.add_argument(
        "--artifact",
        type=Path,
        action="append",
        default=[],
        help="Optional artifact path to hash. May be supplied more than once.",
    )
    parser.add_argument(
        "--output",
        type=Path,
        help="Write JSON to this path. Without it, JSON is written to stdout.",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    trx_dir: Path = args.trx_dir.resolve()
    repo_root: Path = args.repo_root.resolve()

    runs = {
        path.name: summarize_trx(path)
        for path in sorted(trx_dir.glob("*.trx"))
    }
    changed = (
        changed_from_manifest(args.manifest.resolve(), repo_root)
        if args.manifest is not None and args.manifest.is_file()
        else []
    )
    artifacts: dict[str, str] = {}
    for raw_path in args.artifact:
        path = raw_path.resolve()
        if not path.is_file():
            continue
        try:
            key = path.relative_to(repo_root).as_posix()
        except ValueError:
            key = str(path)
        artifacts[key] = file_sha256(path)

    output = {
        "trx_directory": str(trx_dir),
        "source_changed_since_manifest": changed,
        "runs": runs,
        "artifact_sha256": artifacts,
    }
    rendered = json.dumps(output, ensure_ascii=False, indent=2)
    if args.output is not None:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(rendered + "\n", encoding="utf-8")
    else:
        sys.stdout.write(rendered + "\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
