import importlib.util
import json
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "generate_shop_npc_placements.py"
SPEC = importlib.util.spec_from_file_location("shop_placements", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class ShopNpcPlacementGeneratorTests(unittest.TestCase):
    def test_enumerates_every_supported_handler_path(self):
        npcs = [
            {
                "#": "1000",
                "ENpcData[0]": "10",
                "ENpcData[1]": "30",
                "ENpcData[2]": "40",
                "ENpcData[3]": "0",
            }
        ]
        topics = [{"#": "30", "Shop[0]": "20", "Shop[1]": "41"}]
        pre_handlers = [
            {"#": "40", "Target": "20"},
            {"#": "41", "Target": "10"},
        ]

        links = MODULE.enumerate_shop_links(
            {10},
            {20},
            npcs,
            {1000: "Fixture Vendor"},
            topics,
            pre_handlers,
        )

        self.assertEqual(
            [
                "direct-shop",
                "topic-select-shop",
                "direct-pre-handler",
                "topic-select-pre-handler",
            ],
            [row["handler"] for row in links],
        )

    def test_map_coordinate_conversion_inverts_known_world_coordinate(self):
        world = -397.6349
        size_factor = 200
        offset = 0
        scale = size_factor / 100.0
        map_coordinate = (41.0 / scale * (((world + offset) * scale + 1024.0) / 2048.0)) + 1.0

        converted = MODULE.map_coordinate_to_world(map_coordinate, size_factor, offset)

        self.assertAlmostEqual(world, converted, places=5)

    def test_ambiguous_place_name_is_audited_and_not_guessed(self):
        placements, ambiguous = MODULE.build_garland_placements(
            {1000},
            [{"i": 1000, "n": "Vendor", "l": 77, "c": [10.0, 11.0]}],
            [
                {"#": "1", "PlaceName": "77", "TerritoryType": "100", "SizeFactor": "100", "OffsetX": "0", "OffsetY": "0"},
                {"#": "2", "PlaceName": "77", "TerritoryType": "101", "SizeFactor": "100", "OffsetX": "0", "OffsetY": "0"},
            ],
            [
                {"#": "100", "PlaceName": "80", "Name": "t1", "Map": "1"},
                {"#": "101", "PlaceName": "81", "Name": "t2", "Map": "2"},
            ],
            {77: "Ambiguous Place", 80: "First", 81: "Second"},
        )

        self.assertEqual([], placements)
        self.assertEqual([1, 2], [row["mapId"] for row in ambiguous[0]["candidateMaps"]])

    def test_supplemental_correction_fills_missing_but_does_not_replace(self):
        territory = [{"#": "100", "PlaceName": "80", "Name": "fixture", "Map": "9"}]
        existing = [{"npcId": 1, "territoryId": 100, "territoryName": "Fixture", "mapId": 9, "x": 1.0, "z": 2.0, "source": "garland-npc-browse", "replaceExisting": False}]
        corrections = [
            {"npcId": 1, "territoryId": 100, "x": 30, "z": 40, "replaceExisting": False},
            {"npcId": 2, "territoryId": 100, "x": 50, "z": 60, "replaceExisting": False},
        ]

        merged, applied, unused = MODULE.merge_corrections(existing, corrections, {1, 2}, territory, {80: "Fixture"})

        self.assertEqual([1.0, 50.0], [row["x"] for row in merged])
        self.assertEqual({2}, applied)
        self.assertEqual([], unused)

    def test_replacement_correction_removes_known_invalid_placement(self):
        territory = [{"#": "100", "PlaceName": "80", "Name": "fixture", "Map": "9"}]
        existing = [{"npcId": 1, "territoryId": 100, "territoryName": "Fixture", "mapId": 9, "x": 1.0, "z": 2.0, "source": "garland-npc-browse", "replaceExisting": False}]
        corrections = [{"npcId": 1, "territoryId": 100, "x": 30, "z": 40, "replaceExisting": True}]

        merged, applied, _unused = MODULE.merge_corrections(existing, corrections, {1}, territory, {80: "Fixture"})

        self.assertEqual(1, len(merged))
        self.assertEqual(30.0, merged[0]["x"])
        self.assertTrue(merged[0]["replaceExisting"])
        self.assertEqual({1}, applied)

    def test_rendering_is_deterministic(self):
        value = {"schemaVersion": 1, "placements": [{"npcId": 2}, {"npcId": 3}]}
        self.assertEqual(MODULE.render_json(value), MODULE.render_json(value))
        self.assertTrue(MODULE.render_json(value).endswith("\n"))

    def test_generation_reports_missing_npc_and_is_byte_stable(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            csv_root = root / "csv"
            csv_root.mkdir()
            self._write_csv(csv_root / "GilShop.csv", ["#"], [[10]])
            self._write_csv(csv_root / "SpecialShop.csv", ["#"], [])
            self._write_csv(csv_root / "ENpcResident.csv", ["#", "Singular"], [[1000, "Missing Vendor"]])
            self._write_csv(csv_root / "ENpcBase.csv", ["#", "ENpcData[0]"], [[1000, 10]])
            self._write_csv(csv_root / "TopicSelect.csv", ["#", "Shop[0]"], [])
            self._write_csv(csv_root / "PreHandler.csv", ["#", "Target"], [])
            self._write_csv(csv_root / "Map.csv", ["#", "PlaceName", "TerritoryType", "SizeFactor", "OffsetX", "OffsetY"], [])
            self._write_csv(csv_root / "TerritoryType.csv", ["#", "PlaceName", "Name", "Map"], [])
            self._write_csv(csv_root / "PlaceName.csv", ["#", "Name"], [])
            garland = root / "garland.json"
            garland.write_text(json.dumps({"browse": [{"i": 1000, "n": "Missing Vendor", "s": 1}]}), encoding="utf-8")
            corrections = root / "corrections.json"
            corrections.write_text(json.dumps({"schemaVersion": 1, "source": "fixture", "sourceCommit": "fixture", "corrections": []}), encoding="utf-8")

            first_catalog, first_audit = MODULE.generate(csv_root, garland, corrections)
            second_catalog, second_audit = MODULE.generate(csv_root, garland, corrections)

            self.assertEqual(MODULE.render_json(first_catalog), MODULE.render_json(second_catalog))
            self.assertEqual(MODULE.render_json(first_audit), MODULE.render_json(second_audit))
            self.assertEqual(1, first_audit["summary"]["missingNpcCount"])
            self.assertEqual(1000, first_audit["missing"][0]["npcId"])

    @staticmethod
    def _write_csv(path, headers, rows):
        lines = [",".join(headers)]
        lines.extend(",".join(str(value) for value in row) for row in rows)
        path.write_text("\n".join(lines) + "\n", encoding="utf-8")


if __name__ == "__main__":
    unittest.main()
