#!/usr/bin/env python3
"""Generate ADS's offline vendor-NPC placement fallback from local data."""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import math
import sys
import traceback
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any, Iterable, Mapping, Sequence

SCHEMA_VERSION = 1
CSV_ENCODING = "utf-8-sig"
HANDLER_ORDER = (
    "direct-shop",
    "topic-select-shop",
    "direct-pre-handler",
    "topic-select-pre-handler",
)


def load_csv(path: Path) -> list[dict[str, str]]:
    with path.open(encoding=CSV_ENCODING, newline="") as handle:
        return list(csv.DictReader(handle))


def load_json(path: Path) -> Any:
    with path.open(encoding="utf-8-sig") as handle:
        return json.load(handle)


def parse_row_id(value: Any) -> int:
    if value in (None, ""):
        return 0
    return int(float(str(value)))


def nonzero_ids(row: Mapping[str, Any], prefix: str) -> list[tuple[int, int]]:
    result: list[tuple[int, int]] = []
    index = 0
    while f"{prefix}[{index}]" in row:
        value = parse_row_id(row[f"{prefix}[{index}]"])
        if value:
            result.append((index, value))
        index += 1
    return result


def enumerate_shop_links(
    gil_shop_ids: set[int],
    special_shop_ids: set[int],
    npc_rows: Sequence[Mapping[str, Any]],
    npc_names: Mapping[int, str],
    topic_rows: Sequence[Mapping[str, Any]],
    pre_handler_rows: Sequence[Mapping[str, Any]],
) -> list[dict[str, Any]]:
    """Enumerate the same terminal link paths used by ADS production code."""

    topics = {
        parse_row_id(row["#"]): nonzero_ids(row, "Shop")
        for row in topic_rows
    }
    pre_handlers = {
        parse_row_id(row["#"]): parse_row_id(row.get("Target"))
        for row in pre_handler_rows
    }

    def terminal(value: int) -> tuple[str, int] | None:
        if value in gil_shop_ids:
            return ("gil", value)
        if value in special_shop_ids:
            return ("special", value)
        return None

    links: list[dict[str, Any]] = []
    for npc_row in sorted(npc_rows, key=lambda row: parse_row_id(row["#"])):
        npc_id = parse_row_id(npc_row["#"])
        name = npc_names.get(npc_id) or f"ENpc {npc_id}"
        for event_index, event_id in nonzero_ids(npc_row, "ENpcData"):
            direct = terminal(event_id)
            if direct:
                links.append(_link(npc_id, name, direct, "direct-shop", event_index, None))
                continue

            if event_id in pre_handlers:
                target = terminal(pre_handlers[event_id])
                if target:
                    links.append(_link(npc_id, name, target, "direct-pre-handler", event_index, None))
                    continue

            for topic_index, topic_value in topics.get(event_id, []):
                target = terminal(topic_value)
                handler = "topic-select-shop"
                if target is None and topic_value in pre_handlers:
                    target = terminal(pre_handlers[topic_value])
                    handler = "topic-select-pre-handler"
                if target:
                    links.append(_link(npc_id, name, target, handler, event_index, topic_index))

    return sorted(
        links,
        key=lambda row: (
            row["npcId"],
            HANDLER_ORDER.index(row["handler"]),
            row["shopKind"],
            row["shopId"],
            row["eventIndex"],
            -1 if row["topicIndex"] is None else row["topicIndex"],
        ),
    )


def _link(
    npc_id: int,
    name: str,
    target: tuple[str, int],
    handler: str,
    event_index: int,
    topic_index: int | None,
) -> dict[str, Any]:
    return {
        "npcId": npc_id,
        "npcName": name,
        "shopKind": target[0],
        "shopId": target[1],
        "handler": handler,
        "eventIndex": event_index,
        "topicIndex": topic_index,
    }


def map_coordinate_to_world(map_coordinate: float, size_factor: int, offset: int) -> float:
    """Invert Dalamud's raw-world-to-map-coordinate transform."""

    if size_factor <= 0:
        raise ValueError("map size factor must be positive")
    scale = size_factor / 100.0
    world = (map_coordinate - 1.0) * (2048.0 / 41.0) - (1024.0 / scale) - offset
    return _rounded(world)


def _rounded(value: float) -> float:
    result = round(float(value), 6)
    return 0.0 if abs(result) < 0.0000005 else result


def build_garland_placements(
    linked_npc_ids: set[int],
    garland_rows: Sequence[Mapping[str, Any]],
    map_rows: Sequence[Mapping[str, Any]],
    territory_rows: Sequence[Mapping[str, Any]],
    place_names: Mapping[int, str],
) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    territory_by_id = {parse_row_id(row["#"]): row for row in territory_rows}
    maps_by_place: dict[int, list[Mapping[str, Any]]] = defaultdict(list)
    for row in map_rows:
        place_id = parse_row_id(row.get("PlaceName"))
        territory_id = parse_row_id(row.get("TerritoryType"))
        size_factor = parse_row_id(row.get("SizeFactor"))
        if place_id and territory_id and size_factor:
            maps_by_place[place_id].append(row)

    placements: list[dict[str, Any]] = []
    ambiguous: list[dict[str, Any]] = []
    for npc in sorted(garland_rows, key=lambda row: parse_row_id(row.get("i"))):
        npc_id = parse_row_id(npc.get("i"))
        if npc_id not in linked_npc_ids:
            continue
        coords = npc.get("c")
        zone_id = parse_row_id(npc.get("l"))
        if not isinstance(coords, list) or len(coords) != 2 or not zone_id:
            continue
        if not all(isinstance(value, (int, float)) and math.isfinite(value) for value in coords):
            continue

        candidates = sorted(
            maps_by_place.get(zone_id, []),
            key=lambda row: (parse_row_id(row.get("TerritoryType")), parse_row_id(row["#"])),
        )
        if len(candidates) != 1:
            ambiguous.append(
                {
                    "npcId": npc_id,
                    "npcName": str(npc.get("n") or f"ENpc {npc_id}"),
                    "zonePlaceNameId": zone_id,
                    "zoneName": place_names.get(zone_id, f"PlaceName {zone_id}"),
                    "candidateMaps": [
                        {
                            "mapId": parse_row_id(candidate["#"]),
                            "territoryId": parse_row_id(candidate.get("TerritoryType")),
                        }
                        for candidate in candidates
                    ],
                    "resolvedByCorrection": False,
                }
            )
            continue

        map_row = candidates[0]
        map_id = parse_row_id(map_row["#"])
        territory_id = parse_row_id(map_row.get("TerritoryType"))
        territory = territory_by_id.get(territory_id, {})
        placements.append(
            {
                "npcId": npc_id,
                "territoryId": territory_id,
                "territoryName": territory_name(territory_id, territory, place_names),
                "mapId": map_id,
                "x": map_coordinate_to_world(
                    float(coords[0]),
                    parse_row_id(map_row.get("SizeFactor")),
                    parse_row_id(map_row.get("OffsetX")),
                ),
                "z": map_coordinate_to_world(
                    float(coords[1]),
                    parse_row_id(map_row.get("SizeFactor")),
                    parse_row_id(map_row.get("OffsetY")),
                ),
                "source": "garland-npc-browse",
                "replaceExisting": False,
            }
        )
    return placements, ambiguous


def territory_name(
    territory_id: int,
    territory: Mapping[str, Any],
    place_names: Mapping[int, str],
) -> str:
    place_id = parse_row_id(territory.get("PlaceName"))
    return place_names.get(place_id) or str(territory.get("Name") or f"Territory {territory_id}")


def merge_corrections(
    placements: Sequence[Mapping[str, Any]],
    corrections: Sequence[Mapping[str, Any]],
    linked_npc_ids: set[int],
    territory_rows: Sequence[Mapping[str, Any]],
    place_names: Mapping[int, str],
) -> tuple[list[dict[str, Any]], set[int], list[int]]:
    territory_by_id = {parse_row_id(row["#"]): row for row in territory_rows}
    merged = [dict(row) for row in placements]
    applied: set[int] = set()
    unused: list[int] = []
    for correction in sorted(corrections, key=lambda row: parse_row_id(row.get("npcId"))):
        npc_id = parse_row_id(correction.get("npcId"))
        if npc_id not in linked_npc_ids:
            unused.append(npc_id)
            continue
        replace = bool(correction.get("replaceExisting", False))
        existing = [row for row in merged if parse_row_id(row.get("npcId")) == npc_id]
        if existing and not replace:
            continue
        if replace:
            merged = [row for row in merged if parse_row_id(row.get("npcId")) != npc_id]

        territory_id = parse_row_id(correction.get("territoryId"))
        territory = territory_by_id.get(territory_id)
        if not territory:
            raise ValueError(f"correction NPC {npc_id} uses unknown territory {territory_id}")
        map_id = parse_row_id(correction.get("mapId")) or parse_row_id(territory.get("Map"))
        x = float(correction["x"])
        z = float(correction["z"])
        if not math.isfinite(x) or not math.isfinite(z):
            raise ValueError(f"correction NPC {npc_id} has non-finite coordinates")
        merged.append(
            {
                "npcId": npc_id,
                "territoryId": territory_id,
                "territoryName": territory_name(territory_id, territory, place_names),
                "mapId": map_id,
                "x": _rounded(x),
                "z": _rounded(z),
                "source": "item-vendor-location-correction",
                "replaceExisting": replace,
            }
        )
        applied.add(npc_id)

    ordered = sorted(
        merged,
        key=lambda row: (
            parse_row_id(row["npcId"]),
            parse_row_id(row["territoryId"]),
            str(row["source"]),
            parse_row_id(row["mapId"]),
            float(row["x"]),
            float(row["z"]),
        ),
    )
    return ordered, applied, sorted(set(unused))


def generate(
    csv_root: Path,
    garland_path: Path,
    corrections_path: Path,
) -> tuple[dict[str, Any], dict[str, Any]]:
    required = (
        "ENpcBase.csv",
        "ENpcResident.csv",
        "GilShop.csv",
        "SpecialShop.csv",
        "TopicSelect.csv",
        "PreHandler.csv",
        "Map.csv",
        "TerritoryType.csv",
        "PlaceName.csv",
    )
    missing_files = [name for name in required if not (csv_root / name).is_file()]
    if missing_files:
        raise FileNotFoundError(f"xivdatamine CSV root is missing: {', '.join(missing_files)}")

    gil_ids = {parse_row_id(row["#"]) for row in load_csv(csv_root / "GilShop.csv")}
    special_ids = {parse_row_id(row["#"]) for row in load_csv(csv_root / "SpecialShop.csv")}
    residents = {
        parse_row_id(row["#"]): str(row.get("Singular") or "")
        for row in load_csv(csv_root / "ENpcResident.csv")
    }
    links = enumerate_shop_links(
        gil_ids,
        special_ids,
        load_csv(csv_root / "ENpcBase.csv"),
        residents,
        load_csv(csv_root / "TopicSelect.csv"),
        load_csv(csv_root / "PreHandler.csv"),
    )
    linked_npc_ids = {row["npcId"] for row in links}
    links_by_npc: dict[int, list[dict[str, Any]]] = defaultdict(list)
    for link in links:
        links_by_npc[link["npcId"]].append(link)

    place_rows = load_csv(csv_root / "PlaceName.csv")
    place_names = {
        parse_row_id(row["#"]): str(row.get("Name") or "")
        for row in place_rows
    }
    map_rows = load_csv(csv_root / "Map.csv")
    territory_rows = load_csv(csv_root / "TerritoryType.csv")
    garland_document = load_json(garland_path)
    garland_rows = garland_document.get("browse")
    if not isinstance(garland_rows, list):
        raise ValueError("Garland input must contain a browse array")
    placements, ambiguous = build_garland_placements(
        linked_npc_ids,
        garland_rows,
        map_rows,
        territory_rows,
        place_names,
    )

    correction_document = load_json(corrections_path)
    if correction_document.get("schemaVersion") != SCHEMA_VERSION:
        raise ValueError("unsupported correction schema version")
    corrections = correction_document.get("corrections")
    if not isinstance(corrections, list):
        raise ValueError("correction input must contain a corrections array")
    placements, applied_corrections, unused_corrections = merge_corrections(
        placements,
        corrections,
        linked_npc_ids,
        territory_rows,
        place_names,
    )
    for issue in ambiguous:
        issue["resolvedByCorrection"] = issue["npcId"] in applied_corrections

    placed_npcs = {row["npcId"] for row in placements}
    unresolved_ambiguous_npcs = {
        row["npcId"]
        for row in ambiguous
        if not row["resolvedByCorrection"]
    }
    missing = []
    for npc_id in sorted(linked_npc_ids - placed_npcs - unresolved_ambiguous_npcs):
        npc_links = links_by_npc[npc_id]
        missing.append(
            {
                "npcId": npc_id,
                "npcName": npc_links[0]["npcName"],
                "shops": sorted(
                    {
                        (link["shopKind"], link["shopId"], link["handler"])
                        for link in npc_links
                    }
                ),
            }
        )

    source = {
        "xivdatamineCsv": "local xivapi/ffxiv-datamining export",
        "garlandNpcBrowseSha256": "sha256:" + _sha256(garland_path),
        "corrections": str(correction_document.get("source") or ""),
        "correctionsCommit": str(correction_document.get("sourceCommit") or ""),
    }
    catalog = {
        "schemaVersion": SCHEMA_VERSION,
        "source": source,
        "placements": placements,
    }
    handler_counts = Counter(link["handler"] for link in links)
    source_counts = Counter(row["source"] for row in placements)
    audit = {
        "schemaVersion": SCHEMA_VERSION,
        "source": source,
        "summary": {
            "linkCount": len(links),
            "linkedNpcCount": len(linked_npc_ids),
            "placementCount": len(placements),
            "missingNpcCount": len(missing),
            "ambiguousGarlandMapCount": len(ambiguous),
            "appliedCorrectionCount": len(applied_corrections),
            "unusedCorrectionCount": len(unused_corrections),
        },
        "handlerCounts": {name: handler_counts[name] for name in HANDLER_ORDER},
        "placementSourceCounts": {
            name: source_counts[name]
            for name in ("garland-npc-browse", "item-vendor-location-correction")
        },
        "missing": missing,
        "ambiguous": sorted(ambiguous, key=lambda row: row["npcId"]),
        "unusedCorrections": unused_corrections,
    }
    return catalog, audit


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def render_json(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, indent=2, separators=(",", ": ")) + "\n"


def write_or_check(path: Path, contents: str, check: bool) -> None:
    if check:
        if not path.is_file() or path.read_text(encoding="utf-8") != contents:
            raise RuntimeError(f"generated output is stale: {path}")
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(contents, encoding="utf-8", newline="\n")


def build_parser() -> argparse.ArgumentParser:
    repo = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(
        description="Generate the checked-in ADS offline vendor placement catalog.",
    )
    parser.add_argument("--csv-root", type=Path, required=True, help="Local xivdatamine csv/en directory")
    parser.add_argument("--garland-npcs", type=Path, required=True, help="Local Garland browse/en/2/npc JSON")
    parser.add_argument(
        "--corrections",
        type=Path,
        default=repo / "tools" / "shop-npc-location-corrections.json",
    )
    parser.add_argument(
        "--catalog-output",
        type=Path,
        default=repo / "ADS" / "Resources" / "shop-npc-placements.json",
    )
    parser.add_argument(
        "--audit-output",
        type=Path,
        default=repo / "docs" / "shop-npc-placement-audit.json",
    )
    parser.add_argument("--check", action="store_true", help="Fail if checked-in outputs differ")
    return parser


def main(argv: Iterable[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    catalog, audit = generate(args.csv_root, args.garland_npcs, args.corrections)
    write_or_check(args.catalog_output, render_json(catalog), args.check)
    write_or_check(args.audit_output, render_json(audit), args.check)
    action = "Verified" if args.check else "Wrote"
    print(f"{action} {len(catalog['placements'])} placements: {args.catalog_output}")
    print(
        f"Audit: linked={audit['summary']['linkedNpcCount']} "
        f"missing={audit['summary']['missingNpcCount']} "
        f"ambiguous={audit['summary']['ambiguousGarlandMapCount']}: {args.audit_output}"
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except SystemExit:
        raise
    except Exception as exc:  # pragma: no cover - exercised through CLI failures
        print(f"error: {exc}", file=sys.stderr)
        traceback.print_exc()
        raise SystemExit(1)
