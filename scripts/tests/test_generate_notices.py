"""Tests for scripts/generate-notices.py: every failure path of the notices and licence gate.

Run: python3 -m unittest discover -s scripts/tests
Each test builds a small fake repository (projects, restored graph, package cache, inputs) in a
temporary folder and points the script's module-level paths at it.
"""
from __future__ import annotations

import importlib.util
import io
import json
import os
import tempfile
import unittest
from contextlib import redirect_stderr
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "generate-notices.py"
MIT = "MIT License\n\nCopyright (c) Example\n"
CATALOGUE = '''public static class ModelCatalog
{
    public static ModelAsset Layout { get; } = new(
        "layout", "layout.onnx", "https://example.org/layout.onnx", "abc", 1);
}
'''


def load_module():
    spec = importlib.util.spec_from_file_location("generate_notices", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class Repo:
    """A fake repository: one packable project depending on package Lib 1.0.0 (MIT)."""

    def __init__(self, root: Path):
        self.root = root
        self.packages = root / "pkgs"
        self.dependencies = {"Lib": {"target": "Package", "version": "[1.0.0, )"}}
        self.libraries = {"Lib/1.0.0": {"type": "package"}}
        self.write("src/App/App.csproj", "<Project></Project>")
        self.package("Lib", "1.0.0", licence='<license type="expression">MIT</license>', files={"LICENSE.txt": MIT})
        self.write("third-party/lib/METADATA", "Component: Lib\nPackages: Lib\nDeclared: MIT\nSource: https://example.org/lib\n")
        self.write("third-party/models/layout/METADATA",
                   "Component: Layout\nAssets: https://example.org/layout.onnx\nDeclared: Apache-2.0\nSource: https://example.org\n")
        self.write("src/Foliant.Models/ModelCatalog.cs", CATALOGUE)
        self.assets()

    def write(self, relative: str, text: str) -> Path:
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
        return path

    def package(self, package_id: str, version: str, licence: str, files: dict[str, str]) -> None:
        folder = f"pkgs/{package_id.lower()}/{version}"
        self.write(f"{folder}/{package_id.lower()}.nuspec",
                   f'<?xml version="1.0"?><package><metadata><id>{package_id}</id>{licence}</metadata></package>')
        for name, text in files.items():
            self.write(f"{folder}/{name}", text)

    def assets(self, project: str = "App") -> None:
        data = {
            "packageFolders": {str(self.packages) + "/": {}},
            "project": {"frameworks": {"net10.0": {"dependencies": self.dependencies}}},
            "targets": {"net10.0": self.libraries},
        }
        self.write(f"src/{project}/obj/project.assets.json", json.dumps(data))


class NoticesTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.repo = Repo(Path(self.temp.name))
        self.g = load_module()
        root = self.repo.root
        self.g.ROOT, self.g.OUT = root, root / "THIRD-PARTY-NOTICES.txt"
        self.g.THIRD_PARTY, self.g.MODELS = root / "third-party", root / "third-party" / "models"
        self.g.MODEL_CATALOG = root / "src" / "Foliant.Models" / "ModelCatalog.cs"

    def tearDown(self):
        self.temp.cleanup()

    def fails_with(self, fragment: str) -> None:
        with self.assertRaises(self.g.InputError) as raised:
            self.g.generate()
        self.assertIn(fragment, str(raised.exception))

    def run_main(self, *args: str) -> tuple[int, str]:
        err = io.StringIO()
        with redirect_stderr(err):
            code = self.g.main(["generate-notices.py", *args])
        return code, err.getvalue()

    # --- happy path, output and check -------------------------------------------------------

    def test_generates_licence_text_and_check_passes(self):
        self.assertEqual(self.run_main(), (0, ""))
        text = self.g.OUT.read_text(encoding="utf-8")
        self.assertIn("Packages: Lib 1.0.0", text)
        self.assertIn("Copyright (c) Example", text)
        self.assertIn("Asset: https://example.org/layout.onnx", text)
        self.assertEqual(self.run_main("--check"), (0, ""))

    def test_check_fails_when_output_is_stale_or_missing(self):
        code, err = self.run_main("--check")
        self.assertEqual(code, 1)
        self.assertIn("out of date", err)
        self.run_main()
        with self.g.OUT.open("a", encoding="utf-8") as out:
            out.write("edited\n")
        self.assertEqual(self.run_main("--check")[0], 1)

    def test_check_accepts_crlf_checkout(self):
        self.run_main()
        self.g.OUT.write_bytes(self.g.OUT.read_bytes().replace(b"\n", b"\r\n"))
        self.assertEqual(self.run_main("--check"), (0, ""))

    def test_no_temporary_file_left_behind(self):
        self.run_main()
        self.assertFalse(self.g.OUT.with_name(self.g.OUT.name + ".tmp").exists())

    def test_unreadable_inputs_report_cleanly(self):
        self.repo.write("src/App/obj/project.assets.json", "{not json")
        code, err = self.run_main()
        self.assertEqual(code, 1)
        self.assertIn("cannot read the inputs", err)

    # --- graph ------------------------------------------------------------------------------

    def test_missing_restore_fails(self):
        (self.repo.root / "src/App/obj/project.assets.json").unlink()
        self.fails_with("run 'dotnet restore")

    def test_private_assets_are_left_out(self):
        self.repo.dependencies["Tool"] = {"target": "Package", "version": "[1.0.0, )", "suppressParent": "All"}
        self.repo.libraries["Tool/1.0.0"] = {"type": "package", "dependencies": {"ToolDep": "1.0.0"}}
        self.repo.libraries["ToolDep/1.0.0"] = {"type": "package"}
        self.repo.assets()
        graph, _ = self.g.load_graph()
        self.assertEqual(set(graph), {"Lib"})

    def test_transitive_packages_are_included(self):
        self.repo.libraries["Lib/1.0.0"]["dependencies"] = {"Inner": "2.0.0"}
        self.repo.libraries["Inner/2.0.0"] = {"type": "package"}
        self.repo.assets()
        graph, _ = self.g.load_graph()
        self.assertEqual(graph["Inner"], {"2.0.0"})

    def test_non_packable_project_is_left_out(self):
        self.repo.write("src/Tool/Tool.csproj", "<Project><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>")
        self.repo.dependencies = {"Other": {"target": "Package", "version": "[1.0.0, )"}}
        self.repo.libraries = {"Other/1.0.0": {"type": "package"}}
        self.repo.assets("Tool")
        graph, _ = self.g.load_graph()
        self.assertNotIn("Other", graph)

    def test_property_valued_is_packable_counts_as_packable(self):
        self.repo.write("src/Tool/Tool.csproj",
                        "<Project><PropertyGroup><IsPackable>$(PackTool)</IsPackable></PropertyGroup></Project>")
        self.repo.dependencies = {"Other": {"target": "Package", "version": "[1.0.0, )"}}
        self.repo.libraries = {"Other/1.0.0": {"type": "package"}}
        self.repo.assets("Tool")
        graph, _ = self.g.load_graph()
        self.assertIn("Other", graph)

    def test_conditional_is_packable_is_rejected(self):
        self.repo.write("src/App/App.csproj",
                        "<Project><PropertyGroup><IsPackable Condition=\"'$(X)'=='1'\">false</IsPackable></PropertyGroup></Project>")
        self.fails_with("conditional <IsPackable>")

    # --- coverage of packages by components ---------------------------------------------------

    def test_uncovered_package_fails(self):
        self.repo.libraries["Extra/1.0.0"] = {"type": "package"}
        self.repo.dependencies["Extra"] = {"target": "Package", "version": "[1.0.0, )"}
        self.repo.assets()
        self.fails_with("package Extra is not covered")

    def test_stale_exact_entry_fails(self):
        self.repo.write("third-party/lib/METADATA", "Component: Lib\nPackages: Lib, Gone\nDeclared: MIT\nSource: s\n")
        self.fails_with("package Gone is not in the shipped graph")

    def test_unused_pattern_fails(self):
        self.repo.write("third-party/other/METADATA", "Component: O\nPackages: Nothing.*\nDeclared: MIT\nSource: s\n")
        self.fails_with("pattern Nothing.* matches no package")

    def test_overlapping_patterns_fail(self):
        self.repo.write("third-party/lib/METADATA", "Component: Lib\nPackages: L*\nDeclared: MIT\nSource: s\n")
        self.repo.write("third-party/lib2/METADATA", "Component: Lib2\nPackages: Li*\nDeclared: MIT\nSource: s\n")
        self.fails_with("matches several components")

    def test_package_listed_twice_fails(self):
        self.repo.write("third-party/lib2/METADATA", "Component: Lib2\nPackages: Lib\nDeclared: MIT\nSource: s\n")
        self.fails_with("listed by both")

    def test_missing_metadata_field_fails(self):
        self.repo.write("third-party/lib/METADATA", "Component: Lib\nPackages: Lib\nSource: s\n")
        self.fails_with("missing 'Declared'")

    # --- licences ---------------------------------------------------------------------------

    def test_declared_licence_mismatch_fails(self):
        self.repo.write("third-party/lib/METADATA", "Component: Lib\nPackages: Lib\nDeclared: Apache-2.0\nSource: s\n")
        self.fails_with("declares licence 'MIT'")

    def test_licence_with_extra_attributes_is_read(self):
        self.repo.package("Lib", "1.0.0", licence='<license version="1.0.0" type="expression">MIT</license>',
                          files={"LICENSE.txt": MIT})
        self.g.generate()

    def test_licence_file_outside_package_fails(self):
        self.repo.package("Lib", "1.0.0", licence='<license type="file">../../secret.txt</license>', files={})
        self.repo.write("pkgs/secret.txt", "x")
        self.repo.write("third-party/lib/METADATA",
                        "Component: Lib\nPackages: Lib\nDeclared: file\nApproved: test\nSource: s\n")
        self.fails_with("which is not in the package")

    def test_declared_licence_file_is_included(self):
        self.repo.package("Lib", "1.0.0", licence='<license type="file">docs/TERMS.rtf</license>',
                          files={"docs/TERMS.rtf": "Custom terms\n"})
        (self.repo.packages / "lib/1.0.0/LICENSE.txt").unlink()
        self.repo.write("third-party/lib/METADATA",
                        "Component: Lib\nPackages: Lib\nDeclared: file\nApproved: test\nSource: s\nVersion: 1.0.0\n")
        self.assertIn("Custom terms", self.g.generate())

    def test_malformed_nuspec_fails_cleanly(self):
        self.repo.write("pkgs/lib/1.0.0/lib.nuspec", "<package><metadata>")
        self.fails_with("unreadable nuspec")

    def test_unlisted_licence_needs_approval(self):
        self.repo.package("Lib", "1.0.0", licence='<license type="expression">GPL-3.0-only</license>',
                          files={"LICENSE.txt": MIT})
        self.repo.write("third-party/lib/METADATA", "Component: Lib\nPackages: Lib\nDeclared: GPL-3.0-only\nSource: s\n")
        self.fails_with("not in the permitted list")

    def test_or_expression_with_a_permitted_option_passes(self):
        self.assertTrue(self.g.is_permitted("MIT OR GPL-2.0-only"))
        self.assertFalse(self.g.is_permitted("MIT AND GPL-2.0-only"))

    def test_copyleft_text_needs_review(self):
        self.repo.package("Lib", "1.0.0", licence='<license type="expression">MIT</license>',
                          files={"LICENSE.txt": MIT, "THIRD-PARTY-NOTICES.txt": "bundles code under the Mozilla Public License\n"})
        self.fails_with("mention copyleft terms (Mozilla Public License)")
        self.repo.write("third-party/lib/METADATA",
                        "Component: Lib\nPackages: Lib\nDeclared: MIT\nCopyleft-review: test\nSource: s\nVersion: 1.0.0\n")
        self.g.generate()

    def test_component_without_licence_text_fails(self):
        self.repo.package("Lib", "1.0.0", licence='<license type="expression">MIT</license>',
                          files={"THIRD-PARTY-NOTICES.txt": "notices only\n"})
        (self.repo.packages / "lib/1.0.0/LICENSE.txt").unlink()
        self.fails_with("no licence text")

    # --- committed inputs -------------------------------------------------------------------

    def test_committed_texts_need_version(self):
        self.repo.write("third-party/lib/LICENSE", MIT)
        self.fails_with("committed texts and decisions need 'Version:'")

    def test_committed_texts_version_drift_fails(self):
        self.repo.write("third-party/lib/LICENSE", MIT)
        self.repo.write("third-party/lib/METADATA", "Component: Lib\nPackages: Lib\nDeclared: MIT\nSource: s\nVersion: 0.9.0\n")
        self.fails_with("texts were fetched for 0.9.0")

    def test_unexpected_committed_file_fails_and_dotfiles_are_skipped(self):
        self.repo.write("third-party/lib/.DS_Store", "\x00binary")
        self.g.generate()
        self.repo.write("third-party/lib/notes.orig", "x")
        self.fails_with("unexpected input")

    # --- text decoding ------------------------------------------------------------------------

    def test_utf16_and_latin1_texts_decode(self):
        path = self.repo.root / "a.txt"
        path.write_bytes("Licence ©\n".encode("utf-16"))
        self.assertEqual(self.g.read_text(path), "Licence ©\n")
        path.write_bytes(b"Copyright \xa9 X\r\n")
        self.assertEqual(self.g.read_text(path), "Copyright \u00a9 X\n")

    def test_binary_text_fails(self):
        path = self.repo.root / "a.txt"
        path.write_bytes(b"abc\x00def")
        with self.assertRaises(self.g.InputError):
            self.g.read_text(path)

    def test_clean_escapes_c0_and_c1_controls(self):
        self.assertEqual(self.g.clean("MIT\n::stop\x1b[31m\x9b"), "MIT\\x0a::stop\\x1b[31m\\x9b")

    @unittest.skipIf(os.name == "nt", "symbolic links need privileges on Windows")
    def test_package_folder_through_symlink_lists_text_once(self):
        self.repo.package("Lib", "1.0.0", licence='<license type="file">LICENSE.txt</license>', files={"LICENSE.txt": MIT})
        link = self.repo.root / "pkgs-link"
        link.symlink_to(self.repo.packages)
        texts = self.g.shipped_texts(link / "lib" / "1.0.0", (link / "lib" / "1.0.0" / "LICENSE.txt").resolve())
        self.assertEqual(len(texts), 1)

    # --- models -------------------------------------------------------------------------------

    def test_catalogue_url_without_model_entry_fails(self):
        self.repo.write("third-party/models/layout/METADATA",
                        "Component: Layout\nAssets: https://example.org/other.onnx\nDeclared: Apache-2.0\nSource: s\n")
        self.fails_with("has no third-party/models entry")

    def test_url_outside_model_asset_fails(self):
        self.repo.write("src/Foliant.Models/ModelCatalog.cs", CATALOGUE + '// card: "https://example.org/card"\n')
        self.fails_with("URL outside a 'ModelAsset")

    def test_interpolated_model_url_fails(self):
        self.repo.write("src/Foliant.Models/ModelCatalog.cs", CATALOGUE + 'var u = $"https://example.org/{x}";\n')
        self.fails_with("no interpolation")

    def test_model_licence_needs_approval_when_unlisted(self):
        self.repo.write("third-party/models/layout/METADATA",
                        "Component: Layout\nAssets: https://example.org/layout.onnx\nDeclared: MIT (upstream)\nSource: s\n")
        self.fails_with("third-party/models/layout: licence 'MIT (upstream)'")


    # --- pass 4: policy precision, privacy, containment, remaining failure paths -------------

    def test_grouped_expression_is_judged_conservatively(self):
        self.assertFalse(self.g.is_permitted("(MIT OR GPL-2.0-only) AND LGPL-2.1-only"))
        self.assertTrue(self.g.is_permitted("(MIT OR Apache-2.0)"))

    def test_copyleft_detection_is_case_insensitive_and_covers_nc_nd(self):
        for text in ("GNU GENERAL PUBLIC LICENSE Version 2", "CC BY-SA 4.0", "EUPL-1.2", "MS-RL",
                     "OSL-3.0", "CC-BY-NC-4.0", "Creative Commons Attribution-NoDerivatives"):
            self.assertIsNotNone(self.g.COPYLEFT.search(text), text)
        self.assertIsNone(self.g.COPYLEFT.search("MIT License, Apache License 2.0, BSD 3-Clause"))

    def test_empty_decision_fails(self):
        self.repo.write("third-party/lib/METADATA",
                        "Component: Lib\nPackages: Lib\nDeclared: MIT\nApproved:\nSource: s\nVersion: 1.0.0\n")
        self.fails_with("'Approved:' is empty")

    def test_decision_without_version_fails(self):
        self.repo.write("third-party/lib/METADATA",
                        "Component: Lib\nPackages: Lib\nDeclared: MIT\nCopyleft-review: owner\nSource: s\n")
        self.fails_with("decisions need 'Version:'")

    def test_decisions_are_not_published(self):
        self.repo.write("third-party/lib/METADATA",
                        "Component: Lib\nPackages: Lib\nDeclared: MIT\nApproved: private note\nSource: s\nVersion: 1.0.0\n")
        self.assertNotIn("private note", self.g.generate())

    def test_platform_target_framework_is_found(self):
        data = json.loads((self.repo.root / "src/App/obj/project.assets.json").read_text())
        data["targets"] = {"net10.0-windows7.0": data["targets"]["net10.0"]}
        entry = dict(data["project"]["frameworks"]["net10.0"], framework="net10.0-windows7.0")
        data["project"]["frameworks"] = {"net10.0-windows": entry}
        self.repo.write("src/App/obj/project.assets.json", json.dumps(data))
        graph, _ = self.g.load_graph()
        self.assertIn("Lib", graph)

    @unittest.skipIf(os.name == "nt", "symbolic links need privileges on Windows")
    def test_text_linking_outside_the_package_fails(self):
        secret = self.repo.write("outside/secret.txt", "token")
        target = self.repo.packages / "lib/1.0.0/NOTICE.txt"
        target.symlink_to(secret)
        self.fails_with("points outside the package")

    def test_unparsable_project_fails(self):
        self.repo.write("src/App/App.csproj", "<Project>")
        self.fails_with("App.csproj")

    def test_no_packable_projects_fails(self):
        self.repo.write("src/App/App.csproj", "<Project><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>")
        self.fails_with("no packable projects")

    def test_model_asset_needs_one_literal_url(self):
        self.repo.write("src/Foliant.Models/ModelCatalog.cs",
                        'class C { public static ModelAsset A { get; } = new("a", "a.onnx", Base, "h", 1); }')
        self.fails_with("exactly one literal https URL")

    def test_model_asset_listed_twice_fails(self):
        self.repo.write("third-party/models/copy/METADATA",
                        "Component: Copy\nAssets: https://example.org/layout.onnx\nDeclared: Apache-2.0\nSource: s\n")
        self.fails_with("is listed by both")

    def test_model_entry_not_in_catalogue_fails(self):
        self.repo.write("third-party/models/extra/METADATA",
                        "Component: Extra\nAssets: https://example.org/extra.onnx\nDeclared: Apache-2.0\nSource: s\n")
        self.fails_with("is not in ModelCatalog.cs")

    def test_missing_models_folder_fails(self):
        (self.repo.root / "third-party/models/layout/METADATA").unlink()
        (self.repo.root / "third-party/models/layout").rmdir()
        (self.repo.root / "third-party/models").rmdir()
        self.fails_with("third-party/models is missing")

    def test_metadata_line_without_key_fails(self):
        self.repo.write("third-party/lib/METADATA", "Component: Lib\nPackages: Lib\nDeclared: MIT\nSource: s\njust text\n")
        self.fails_with("line without 'Key: value'")

    def test_missing_nuspec_fails(self):
        (self.repo.packages / "lib/1.0.0/lib.nuspec").unlink()
        self.fails_with("lib.nuspec not found")

    def test_missing_package_folder_fails(self):
        self.repo.libraries = {"Lib/2.0.0": {"type": "package"}}
        self.repo.assets()
        self.fails_with("Lib 2.0.0 is not in any restored package folder")

    def test_unknown_argument_prints_usage(self):
        self.assertEqual(self.run_main("--bogus")[0], 2)

    # --- pass 5 -------------------------------------------------------------------------------

    def test_copyleft_detection_handles_wrapped_and_wider_terms(self):
        for text in ("GNU Lesser General\nPublic License", "Mozilla Public\n   License, v. 2.0",
                     "Server Side Public License", "Common Public License", "CeCILL",
                     "for non-commercial use only", "Creative Commons Attribution-Share Alike 3.0"):
            self.assertIsNotNone(self.g.COPYLEFT.search(text), text)

    def test_platform_target_uses_its_own_framework_entry(self):
        data = json.loads((self.repo.root / "src/App/obj/project.assets.json").read_text())
        data["project"]["frameworks"] = {
            "net10.0": {"framework": "net10.0", "dependencies": {}},
            "net10.0-windows": {"framework": "net10.0-windows7.0", "dependencies": self.repo.dependencies},
        }
        data["targets"] = {"net10.0": {}, "net10.0-windows7.0": self.repo.libraries}
        self.repo.write("src/App/obj/project.assets.json", json.dumps(data))
        graph, _ = self.g.load_graph()
        self.assertIn("Lib", graph)

    @unittest.skipIf(os.name == "nt", "symbolic links need privileges on Windows")
    def test_committed_link_is_rejected(self):
        secret = self.repo.write("outside/secret.txt", "token")
        (self.repo.root / "third-party/lib/LICENSE").symlink_to(secret)
        self.fails_with("not links")

    def test_repeated_metadata_key_fails(self):
        self.repo.write("third-party/lib/METADATA",
                        "Component: Lib\nPackages: Lib\nDeclared: MIT\nDeclared: GPL-3.0-only\nSource: s\n")
        self.fails_with("'Declared' appears more than once")

    def test_version_is_checked_whenever_present(self):
        self.repo.write("third-party/lib/METADATA",
                        "Component: Lib\nPackages: Lib\nDeclared: MIT\nSource: s\nVersion: 9.0.0\n")
        self.fails_with("texts were fetched for 9.0.0")

    def test_source_offer_is_published(self):
        self.repo.write("third-party/lib/METADATA",
                        "Component: Lib\nPackages: Lib\nDeclared: MIT\nSource: s\nSource-offer: get it at X\n")
        self.assertIn("Source-offer: get it at X", self.g.generate())

    def test_control_characters_are_escaped_and_form_feeds_dropped(self):
        path = self.repo.root / "a.txt"
        path.write_bytes(b"Terms\x1b[31m red\n\x0cPage two\tend\n")
        self.assertEqual(self.g.read_text(path), "Terms\\x1b[31m red\nPage two\tend\n")

    def test_windows_1252_text_decodes(self):
        path = self.repo.root / "a.txt"
        path.write_bytes(b"\x93quoted\x94 \xa9\n")
        self.assertEqual(self.g.read_text(path), "\u201cquoted\u201d \u00a9\n")

if __name__ == "__main__":
    unittest.main()
